using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FoldEdge;

// ============================================================
//  FoldEdge —— Fold 引擎外部主程序 (.NET 8 / ASP.NET Core)
//  监听 127.0.0.1:9000，作为游戏内 FoldEngine 插件的控制端：
//    GET  /api/status            查询运行状态
//    POST /api/trigger/{module}  读取 Modules/*.fdll（ZIP）→ 下发 manifest + timeline
//    POST /api/image             下载图片 → JSON 颜色矩阵 → 推送给插件
//    GET  /ws                    WebSocket 接入点（FoldEngine 插件长连接）
//  并用 FileSystemWatcher 监控 Modules 目录下的 *.fdll 模块包。
// ============================================================

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:9000");

var app = builder.Build();
app.UseWebSockets();

var startTime = DateTime.UtcNow;
var modulesDir = Path.Combine(app.Environment.ContentRootPath, "Modules");
Directory.CreateDirectory(modulesDir);

// 多语言：默认 zh-CN（后续可从配置或请求头切换，见 Loc.Load）
Loc.Load("zh-CN");
Console.WriteLine($"[FoldEdge] {Loc.Get("service.start")}");

// ---- WebSocket 客户端（游戏内 FoldEngine 插件）连接表 ----
var clients = new ConcurrentDictionary<Guid, WebSocket>();

async Task BroadcastAsync(string json)
{
    var payload = Encoding.UTF8.GetBytes(json);
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
        }
        catch
        {
            clients.TryRemove(id, out _);
        }
    }
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

(string? Manifest, string? Timeline, string? Error) LoadModule(string moduleName)
{
    var file = Path.Combine(modulesDir, moduleName + ".fdll");
    if (!File.Exists(file)) return (null, null, "module not found");
    try
    {
        using var zip = ZipFile.OpenRead(file);
        var manifest = ReadEntry(zip, "manifest.json");
        var timeline = ReadEntry(zip, "timeline.json");
        if (string.IsNullOrWhiteSpace(manifest)) return (null, null, "manifest.json missing");
        if (string.IsNullOrWhiteSpace(timeline)) return (null, null, "timeline.json missing");
        return (manifest, timeline, null);
    }
    catch (InvalidDataException)
    {
        return (null, null, "not a valid ZIP package (encrypted .fdll is not supported yet)");
    }
}

// 读取模块并下发 play 指令；成功返回 null，失败返回错误文本。
async Task<string?> SendPlayCommandAsync(string moduleName)
{
    var (manifest, timeline, error) = LoadModule(moduleName);
    if (error != null) return error;

    await BroadcastAsync(JsonSerializer.Serialize(new
    {
        type = "command",
        command = "play",
        module = moduleName,
        manifest,
        timeline
    }, jsonOpts));
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
    Console.WriteLine($"[FoldEdge] {Loc.Get("image.converted", frame.Width, frame.Height, clients.Count)}");
    await BroadcastAsync(payload);
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
            if (result.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                var msg = sb.ToString();
                sb.Clear();
                await HandleClientMessage(msg);
            }
        }
    }
    catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
    {
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
    clients = clients.Count,
    modules = Directory.GetFiles(modulesDir, "*.fdll").Select(Path.GetFileName).ToArray()
}));

// ---- POST /api/trigger/{moduleName}：读取 .fdll 并下发播放指令 ----
app.MapPost("/api/trigger/{moduleName}", async (string moduleName) =>
{
    var error = await SendPlayCommandAsync(moduleName);
    if (error != null)
        return Results.NotFound(new { error = $"module '{moduleName}': {error}" });

    return Results.Ok(new { sent = true, module = moduleName, clients = clients.Count });
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
            clients = clients.Count,
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
