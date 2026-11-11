using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FoldEdge;
using Microsoft.Extensions.FileProviders;

// ============================================================
//  FoldEdge —— Fold 引擎外部主程序 (.NET 8 / ASP.NET Core)
//  监听 127.0.0.1:9000，作为游戏内 FoldEngine 插件的控制端：
//    GET  /api/status               运行状态 / 最后遥测
//    POST /api/trigger/{module}     读取 Modules/*.fdll → 下发 manifest + timeline
//    POST /api/image                调试：图片 URL → 像素矩阵
//    POST /api/media/upload         上传视频 → FFmpeg 转码 → /Assets URL
//    POST /api/module/publish       视频 URL + 海报 → 一键打包 .fdll
//    POST /api/control/stop         下发 [FOLD_CMD] STOP
//    POST /api/control/jump/{sec}   下发 [FOLD_CMD] JUMP
//    POST /api/telemetry            FoldClient 播放状态遥测（HMAC 校验）
//    GET  /ws                       WebSocket 接入点（FoldEngine 插件长连接）
// ============================================================

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["ListenUrl"] ?? "http://127.0.0.1:9000");

var app = builder.Build();
app.UseWebSockets();

// ---- 媒体存储策略：Local（本机静态文件）或 CDN（外链） ----
var storageStrategy = (builder.Configuration["MediaStorageStrategy"] ?? "Local").Trim();
var publicBaseUrl = builder.Configuration["PublicBaseUrl"] ?? "http://127.0.0.1:9000";
var cdnBaseUrl = builder.Configuration["CdnBaseUrl"] ?? string.Empty;

var assetsDir = Path.Combine(app.Environment.ContentRootPath, "Assets");
var uploadDir = Path.Combine(app.Environment.ContentRootPath, "Media", "Uploads");
Directory.CreateDirectory(assetsDir);
Directory.CreateDirectory(uploadDir);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(assetsDir),
    RequestPath = "/Assets"
});
app.UseDefaultFiles();
app.UseStaticFiles();

var startTime = DateTime.UtcNow;
var modulesDir = Path.Combine(app.Environment.ContentRootPath, "Modules");
Directory.CreateDirectory(modulesDir);

var lastTelemetry = "none";

// 多语言：默认 zh-CN（后续可从配置或请求头切换，见 Loc.Load）
Loc.Load("zh-CN");
Console.WriteLine($"[FoldEdge] {Loc.Get("service.start")}");

// ECDSA 密钥：首次启动生成 keys/fold-ecdsa-private.pem，公钥需硬编码进 FoldClient
MediaSigner.Initialize();
Console.WriteLine("[FoldEdge] ECDSA public key (SPKI PEM):");
Console.WriteLine(MediaSigner.PublicKeyPem);

// ---- WebSocket 客户端（游戏内 FoldEngine 插件）连接表 ----
var clients = new ConcurrentDictionary<Guid, WebSocket>();

static string GetString(JsonElement root, string name) =>
    root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

// 计算仍然存活的客户端，并顺手清理已断开/半关闭的连接
int CountAliveClients()
{
    int count = 0;
    foreach (var (id, socket) in clients)
    {
        if (socket.State == WebSocketState.Open)
        {
            count++;
        }
        else
        {
            clients.TryRemove(id, out _);
        }
    }
    return count;
}

// 推送并返回实际成功发送的客户端数量；失败的连接立即从活动列表移除
async Task<int> BroadcastAsync(string json)
{
    var payload = Encoding.UTF8.GetBytes(json);
    int sent = 0;
    foreach (var (id, socket) in clients)
    {
        if (socket.State != WebSocketState.Open)
        {
            clients.TryRemove(id, out _);
            continue;
        }
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
            sent++;
        }
        catch
        {
            clients.TryRemove(id, out _);
            try { socket.Abort(); } catch { /* 已断开 */ }
        }
    }
    return sent;
}

// ---- .fdll 模块包（ZIP）读取：manifest.json + timeline.json ----
// 合规标签由 FoldEngine 依据 manifest.tags 注入服务器名前缀，
// 因此 manifest 必须原样下发给插件，不能被丢弃。
var jsonOpts = new JsonSerializerOptions
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

string? ReadEntry(ZipArchive zip, string entryName)
{
    var entry = zip.Entries.FirstOrDefault(e =>
        string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase));
    if (entry == null) return null;
    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
    return reader.ReadToEnd();
}

(string? Manifest, string? Timeline, bool Encrypted, string? Error) LoadModule(string moduleName)
{
    var file = Path.Combine(modulesDir, moduleName + ".fdll");
    if (!File.Exists(file)) return (null, null, false, "module not found");
    try
    {
        var raw = File.ReadAllBytes(file);
        // 文件头不是 ZIP（"PK"）时按 FOLDENC1 加密容器处理，仅在内存中解密
        bool encrypted = raw.Length >= FoldCrypto.Magic.Length &&
                         Encoding.ASCII.GetString(raw, 0, FoldCrypto.Magic.Length) == FoldCrypto.Magic;
        var payload = encrypted ? FoldCrypto.Decrypt(raw, FoldCrypto.Magic.Length) : raw;

        using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
        var manifest = ReadEntry(zip, "manifest.json");
        var timeline = ReadEntry(zip, "timeline.json");
        if (string.IsNullOrWhiteSpace(manifest)) return (null, null, encrypted, "manifest.json missing");
        if (string.IsNullOrWhiteSpace(timeline)) return (null, null, encrypted, "timeline.json missing");
        return (manifest, timeline, encrypted, null);
    }
    catch (CryptographicException)
    {
        return (null, null, true, "AES decrypt failed (key/IV mismatch or corrupted package)");
    }
    catch (InvalidDataException)
    {
        return (null, null, false, "not a valid ZIP / FOLDENC1 package");
    }
}

// 读取模块并下发 play 指令；成功返回 null，失败返回错误文本。
// 加密模块不再下发明文，而是用预设密钥加密负载放进 enc 字段，由插件内存解密。
async Task<string?> SendPlayCommandAsync(string moduleName)
{
    var (manifest, timeline, encrypted, error) = LoadModule(moduleName);
    if (error != null) return error;

    // 媒体条目在离开 FoldEdge 前完成签名；FoldEngine 只负责透传
    timeline = MediaSigner.SignTimeline(timeline!);

    string payload;
    if (encrypted)
    {
        var inner = JsonSerializer.Serialize(new { manifest, timeline }, jsonOpts);
        payload = JsonSerializer.Serialize(new
        {
            type = "command",
            command = "play",
            module = moduleName,
            enc = FoldCrypto.EncryptToBase64(inner)
        }, jsonOpts);
    }
    else
    {
        payload = JsonSerializer.Serialize(new
        {
            type = "command",
            command = "play",
            module = moduleName,
            manifest,
            timeline
        }, jsonOpts);
    }

    await BroadcastAsync(payload);
    return null;
}

// ---- 图片 → JSON 颜色矩阵 → 推送给全部插件客户端 ----
// url == "test" → 本地 8x6 渐变图；url == "smiley" → 本地 8x6 彩色笑脸（零网络依赖）
async Task<ImageFrame> PushImageAsync(string url, int maxWidth, int maxHeight, float duration)
{
    var frame = url == "test" ? ImageService.GenerateTestFrame(8, 6)
        : url == "smiley" ? ImageService.GenerateSmileyFrame()
        : await ImageService.DownloadAndConvertAsync(url, maxWidth, maxHeight);
    var payload = JsonSerializer.Serialize(new
    {
        type = "image-frame",
        width = frame.Width,
        height = frame.Height,
        duration,
        frames = frame.Frames
    });
    int sent = await BroadcastAsync(payload);
    Console.WriteLine($"[FoldEdge] {Loc.Get("image.converted", frame.Width, frame.Height, sent)}");
    return frame;
}

// ---- 处理插件发来的请求（image-request / trigger） ----
async Task HandleClientMessage(string msg)
{
    try
    {
        using var doc = JsonDocument.Parse(msg);
        if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;
        var type = typeEl.GetString();

        if (type == "image-request" &&
            doc.RootElement.TryGetProperty("url", out var urlEl) &&
            !string.IsNullOrEmpty(urlEl.GetString()))
        {
            await PushImageAsync(urlEl.GetString()!, 8, 6, 8f);
            return;
        }

        // 游戏内 fold video/image/audio/text 指令：由主程序签名后回推 media 指令
        if (type == "media-request" &&
            doc.RootElement.TryGetProperty("kind", out var kindEl))
        {
            var kind = kindEl.GetString();
            string url = doc.RootElement.TryGetProperty("url", out var mediaUrlEl) ? mediaUrlEl.GetString() ?? "" : "";
            string text = doc.RootElement.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
            double volume = doc.RootElement.TryGetProperty("volume", out var volumeEl) &&
                            volumeEl.TryGetDouble(out var parsedVolume)
                ? parsedVolume
                : 1.0;
            double duration = doc.RootElement.TryGetProperty("duration", out var durationEl) &&
                              durationEl.TryGetDouble(out var parsedDuration)
                ? parsedDuration
                : 5.0;

            string prefix;
            string payloadJson;
            switch (kind)
            {
                case "video":
                    prefix = "[FOLD_VIDEO]";
                    payloadJson = JsonSerializer.Serialize(new { url }, jsonOpts);
                    break;
                case "image":
                    prefix = "[FOLD_IMG]";
                    payloadJson = JsonSerializer.Serialize(new { url }, jsonOpts);
                    break;
                case "audio":
                    prefix = "[FOLD_AUDIO]";
                    payloadJson = JsonSerializer.Serialize(new { url, volume }, jsonOpts);
                    break;
                case "text":
                    prefix = "[FOLD_TEXT]";
                    payloadJson = JsonSerializer.Serialize(new { text, duration }, jsonOpts);
                    break;
                default:
                    return;
            }

            var marker = MediaSigner.BuildMarker(prefix, kind!, payloadJson);
            await BroadcastAsync(JsonSerializer.Serialize(new
            {
                type = "command",
                command = "media",
                marker
            }, jsonOpts));
            return;
        }

        // 游戏内 `fold module <name>` 指令：由主程序读取 .fdll 并回推 play 指令
        if (type == "trigger" &&
            doc.RootElement.TryGetProperty("module", out var moduleEl) &&
            !string.IsNullOrEmpty(moduleEl.GetString()))
        {
            var module = moduleEl.GetString()!;
            var error = await SendPlayCommandAsync(module);
            Console.WriteLine(error == null
                ? $"[FoldEdge] 已下发模块: {module}"
                : $"[FoldEdge] 模块下发失败 ({module}): {error}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FoldEdge] {Loc.Get("image.failed", ex.Message)}");
    }
}

// ---- FileSystemWatcher：监控 Modules/*.fdll ----
async Task OnModuleChanged(object sender, FileSystemEventArgs e)
{
    Console.WriteLine($"[FoldEdge] {Loc.Get("module.changed", Path.GetFileNameWithoutExtension(e.Name), e.ChangeType.ToString())}");
    await BroadcastAsync(JsonSerializer.Serialize(new
    {
        type = "module-updated",
        module = Path.GetFileNameWithoutExtension(e.Name),
        change = e.ChangeType.ToString()
    }));
}

var watcher = new FileSystemWatcher(modulesDir, "*.fdll")
{
    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
    IncludeSubdirectories = false
};
watcher.Created += (s, e) => _ = OnModuleChanged(s, e);
watcher.Changed += (s, e) => _ = OnModuleChanged(s, e);
watcher.Deleted += (s, e) => _ = OnModuleChanged(s, e);
watcher.EnableRaisingEvents = true;
Console.WriteLine($"[FoldEdge] {Loc.Get("modules.watching", modulesDir)}");

// ---- WebSocket 服务：FoldEngine 插件接入点 ----
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var id = Guid.NewGuid();
    clients[id] = socket;
    Console.WriteLine($"[FoldEdge] {Loc.Get("ws.client.connected", id)}");

    var buffer = new byte[16384];
    var sb = new StringBuilder();
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // 对端主动关闭：立刻从活动列表移除，后续推送不会再计入该连接
                clients.TryRemove(id, out _);
                break;
            }
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                var msg = sb.ToString();
                sb.Clear();
                await HandleClientMessage(msg);
            }
        }
    }
    catch (Exception ex)
    {
        clients.TryRemove(id, out _);
        Console.WriteLine($"[FoldEdge] {Loc.Get("ws.client.error", ex.Message)}");
    }
    finally
    {
        clients.TryRemove(id, out _);
        Console.WriteLine($"[FoldEdge] {Loc.Get("ws.client.disconnected", id)}");
    }
});

// ---- GET /api/status ----
app.MapGet("/api/status", () => Results.Ok(new
{
    status = "ok",
    service = "FoldEdge",
    listen = "127.0.0.1:9000",
    uptimeSeconds = (long)(DateTime.UtcNow - startTime).TotalSeconds,
    clients = CountAliveClients(),
    mediaStorage = storageStrategy,
    lastTelemetry,
    modules = Directory.GetFiles(modulesDir, "*.fdll").Select(Path.GetFileName).ToArray()
}));

// ---- POST /api/trigger/{moduleName}：读取 .fdll 并下发播放指令 ----
app.MapPost("/api/trigger/{moduleName}", async (string moduleName) =>
{
    var error = await SendPlayCommandAsync(moduleName);
    if (error != null)
        return Results.NotFound(new { error = $"module '{moduleName}': {error}" });

    return Results.Ok(new { sent = true, module = moduleName, clients = CountAliveClients() });
});

// ---- POST /api/image：测试接口 ----
//  body: { "url": "...", "maxWidth": 8, "maxHeight": 6, "duration": 8 }
//  url 传 "test"/"smiley" 走本地生成图；其余下载 → 转 JSON 颜色矩阵 → 推送给已连接的 FoldEngine 插件。
app.MapPost("/api/image", async (HttpContext context) =>
{
    var req = await context.Request.ReadFromJsonAsync<ImageRequest>();
    if (req == null || string.IsNullOrEmpty(req.Url))
        return Results.BadRequest(new { error = "missing 'url'" });

    try
    {
        var frame = await PushImageAsync(req.Url, req.MaxWidth, req.MaxHeight, req.Duration);
        return Results.Ok(new
        {
            sent = true,
            clients = CountAliveClients(),
            width = frame.Width,
            height = frame.Height,
            frames = frame.Frames
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---- POST /api/media/upload：上传原始视频 → FFmpeg 转码 → /Assets URL ----
app.MapPost("/api/media/upload", async (HttpContext context) =>
{
    if (!context.Request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "missing file" });

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    var id = Guid.NewGuid().ToString("N");
    var input = Path.Combine(uploadDir, id + extension);
    await using (var stream = File.Create(input))
    {
        await file.CopyToAsync(stream);
    }

    string fileName;
    bool transcoded = false;

    if (await MediaPipeline.HasFfmpegAsync())
    {
        fileName = id + ".mp4";
        try
        {
            await MediaPipeline.TranscodeAsync(
                input, Path.Combine(assetsDir, fileName), CancellationToken.None);
            transcoded = true;
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
    else if (extension is ".mp4" or ".webm")
    {
        fileName = id + extension;
        File.Copy(input, Path.Combine(assetsDir, fileName), true);
    }
    else
    {
        return Results.BadRequest(new
        {
            error = "ffmpeg not found; only mp4/webm can be served without transcoding"
        });
    }

    var baseUrl = storageStrategy.Equals("CDN", StringComparison.OrdinalIgnoreCase)
        ? cdnBaseUrl
        : publicBaseUrl;
    var url = string.IsNullOrEmpty(baseUrl)
        ? "/Assets/" + fileName
        : baseUrl.TrimEnd('/') + "/Assets/" + fileName;

    return Results.Ok(new { url, fileName, transcoded, storage = storageStrategy });
});

// ---- POST /api/module/publish：视频 + 海报 + 时间轴 → 一键生成 .fdll ----
app.MapPost("/api/module/publish", (ModulePublishRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.VideoUrl))
        return Results.BadRequest(new { error = "name and videoUrl are required" });

    var path = MediaPipeline.BuildModule(modulesDir, request);
    return Results.Ok(new
    {
        module = Path.GetFileNameWithoutExtension(path),
        file = path
    });
});

// ---- POST /api/control/*：[FOLD_CMD] STOP / JUMP，ECDSA 签名后由插件透传 ----
async Task<IResult> SendControlAsync(string command, double seconds)
{
    var payload = command == "JUMP"
        ? JsonSerializer.Serialize(new { command, time = seconds }, jsonOpts)
        : JsonSerializer.Serialize(new { command }, jsonOpts);
    var marker = MediaSigner.BuildMarker("[FOLD_CMD]", "cmd", payload);

    int sent = await BroadcastAsync(JsonSerializer.Serialize(new
    {
        type = "command",
        command = "control",
        marker
    }, jsonOpts));

    return Results.Ok(new { sent, marker });
}

app.MapPost("/api/control/stop", () => SendControlAsync("STOP", 0));
app.MapPost("/api/control/jump/{seconds:double}",
    (double seconds) => SendControlAsync("JUMP", seconds));

// ---- POST /api/telemetry：FoldClient 播放状态（HMAC 校验） ----
app.MapPost("/api/telemetry", async (HttpContext context) =>
{
    using var doc = await JsonDocument.ParseAsync(context.Request.Body);
    var root = doc.RootElement;

    string status = GetString(root, "status");
    string mediaType = GetString(root, "mediaType");
    string url = GetString(root, "url");
    double currentTime = root.TryGetProperty("currentTime", out var timeEl) &&
                         timeEl.TryGetDouble(out var parsedTime)
        ? parsedTime
        : 0;
    long timestamp = root.TryGetProperty("timestamp", out var tsEl) &&
                     tsEl.TryGetInt64(out var parsedTimestamp)
        ? parsedTimestamp
        : 0;
    string signature = GetString(root, "signature");

    if (!TelemetryVerifier.Verify(status, mediaType, url, currentTime, timestamp, signature))
        return Results.Unauthorized();

    lastTelemetry = $"{status} {mediaType} {url} {currentTime:F1}s";
    Console.WriteLine($"[FoldEdge] telemetry: {lastTelemetry}");
    return Results.Ok(new { ok = true });
});

// ---- 优雅退出：释放 Watcher 并关闭全部连接 ----
app.Lifetime.ApplicationStopping.Register(() =>
{
    watcher.Dispose();
    foreach (var socket in clients.Values)
    {
        try
        {
            socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "FoldEdge shutdown", CancellationToken.None).Wait();
        }
        catch
        {
            // 忽略退出时的连接清理异常
        }
    }
});

app.Run();

/// <summary>POST /api/image 请求体（默认 8x6）。</summary>
public sealed record ImageRequest(string Url, int MaxWidth = 8, int MaxHeight = 6, float Duration = 8f);

/// <summary>
/// 预设密钥 AES-256-CBC（PKCS7）：
///   · 解密 FOLDENC1 加密 .fdll（内存解密，不落盘）；
///   · 加密加密模块的下发负载（enc 字段），由 FoldEngine 内存解密。
/// 密钥与 IV 必须与 FoldEngine/FoldCrypto.cs 完全一致。
/// </summary>
internal static class FoldCrypto
{
    public const string Magic = "FOLDENC1";
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("Fold-Engine-Preset-AES-Key-2026!");
    private static readonly byte[] Iv = Encoding.UTF8.GetBytes("FoldEngine-IV-01");

    public static byte[] Decrypt(byte[] data, int offset)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, offset, data.Length - offset);
    }

    public static string EncryptToBase64(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
    }
}
