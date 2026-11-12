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
//    POST /api/telemetry            FVP-Native 播放状态遥测（HMAC 校验）
//    GET  /ws                       WebSocket 接入点（FoldEngine 插件长连接）
// ============================================================

var builder = WebApplication.CreateBuilder(args);
var listenUrl = builder.Configuration["ListenUrl"] ?? "http://127.0.0.1:9000";
builder.WebHost.UseUrls(listenUrl);

// 子进程控制台使用系统 OEM 代码页输出（中文 Windows 为 GBK），需要注册代码页编码提供程序。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

const string FoldVersion = "V15";

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

var startTime = DateTime.UtcNow;
var modulesDir = Path.Combine(app.Environment.ContentRootPath, "Modules");
Directory.CreateDirectory(modulesDir);

// ---- 管理面板配置：持久化到 Data/dashboard-config.json ----
var dataDir = Path.Combine(app.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(dataDir);
var dashboardConfigPath = Path.Combine(dataDir, "dashboard-config.json");
var dashboardConfig = DashboardConfig.Load(dashboardConfigPath);
FoldLog.Level = FoldLog.ParseLevel(dashboardConfig.LogLevel);

// ---- 统一日志中心 / 用户库 / JWT / VPN ----
var logService = new LogService(dataDir);
FoldLog.Sink = logService;
var userStore = new UserStore(dataDir);
var jwtService = new JwtService(Path.Combine(dataDir, "jwt-secret.b64"));
var vpnService = new VpnService(
    builder.Configuration["VpnServer"] ?? "vpn.example.com",
    builder.Configuration["VpnTunnelType"] ?? "L2tp");
var gameServerOptions = builder.Configuration.GetSection("GameServer").Get<GameServerOptions>() ?? new GameServerOptions();
var gameServer = new GameServerManager(gameServerOptions, logService, app.Environment.ContentRootPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(assetsDir),
    RequestPath = "/Assets"
});
app.UseDefaultFiles();
app.UseStaticFiles();

// ---- API 请求日志中间件（跳过日志/状态等高频轮询） ----
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    bool noisy = path.StartsWithSegments("/api/logs") ||
                 path.StartsWithSegments("/api/status") ||
                 path.StartsWithSegments("/api/health");
    if (!path.StartsWithSegments("/api") || noisy)
    {
        await next();
        return;
    }

    DateTime started = DateTime.UtcNow;
    await next();
    long elapsed = (long)(DateTime.UtcNow - started).TotalMilliseconds;
    logService.Write("Debug",
        $"API {context.Request.Method} {path} -> {context.Response.StatusCode} ({elapsed}ms)",
        $"API {context.Request.Method} {path} -> {context.Response.StatusCode} ({elapsed}ms)");
});

// ---- JWT 会话与 RBAC 网关：未登录 / 无权限无法访问受保护接口 ----
app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? string.Empty;
    if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) || IsPublicApiPath(path))
    {
        await next();
        return;
    }

    if (!userStore.HasOwner)
    {
        context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
        await context.Response.WriteAsJsonAsync(new { error = "setup_required" });
        return;
    }

    string header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
        return;
    }

    if (!jwtService.TryValidate(header.Substring(7).Trim(), out var payload, out var reason) || payload == null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = reason });
        return;
    }

    var user = userStore.FindById(payload.Sub);
    if (user == null ||
        user.RevokedAtUtc != null ||
        user.TokenVersion != payload.Ver ||
        (user.ExpiresAtUtc.HasValue && user.ExpiresAtUtc.Value <= DateTime.UtcNow))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "session_revoked" });
        return;
    }

    context.Items["foldUser"] = user;
    await next();
});

var lastTelemetry = "none";

// 多语言：默认 zh-CN（后续可从配置或请求头切换，见 Loc.Load）
Loc.Load("zh-CN");
FoldLog.Info($"[FoldEdge] {Loc.Get("service.start")}");

// ECDSA 密钥：首次启动生成 keys/fold-ecdsa-private.pem，公钥需硬编码进原生客户端
MediaSigner.Initialize();
FoldLog.Info("[FoldEdge] ECDSA public key (SPKI PEM):");
FoldLog.Info(MediaSigner.PublicKeyPem);

// ---- WebSocket 客户端连接表（FoldEngine 插件 / FVP-Native 原生客户端） ----
// 客户端连接后发送 {"type":"hello","role":"fe"|"fvp"} 以便面板区分在线数量。
var clients = new ConcurrentDictionary<Guid, ClientConnection>();

static string GetString(JsonElement root, string name) =>
    root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

static int CountTimelineActions(string json)
{
    try
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return document.RootElement.GetArrayLength();
        if (document.RootElement.TryGetProperty("entries", out var entries) &&
            entries.ValueKind == JsonValueKind.Array)
            return entries.GetArrayLength();
    }
    catch (JsonException)
    {
    }

    return 0;
}

// 计算仍然存活的客户端，并顺手清理已断开/半关闭的连接
int CountAliveClients()
{
    int count = 0;
    foreach (var (id, connection) in clients)
    {
        if (connection.Socket.State == WebSocketState.Open)
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

// 按角色统计在线连接：fe = FoldEngine 插件，fvp = FVP-Native 原生客户端，unknown = 尚未 hello
(int Fe, int Fvp, int Unknown) CountClientRoles()
{
    int fe = 0, fvp = 0, unknown = 0;
    foreach (var (id, connection) in clients)
    {
        if (connection.Socket.State != WebSocketState.Open)
        {
            clients.TryRemove(id, out _);
            continue;
        }

        switch (connection.Role)
        {
            case "fe": fe++; break;
            case "fvp": fvp++; break;
            default: unknown++; break;
        }
    }

    return (fe, fvp, unknown);
}

// 推送并返回实际成功发送的客户端数量；失败的连接立即从活动列表移除
async Task<int> BroadcastAsync(string json)
{
    var payload = Encoding.UTF8.GetBytes(json);
    int sent = 0;
    foreach (var (id, connection) in clients)
    {
        var socket = connection.Socket;
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

        // 分离签名：<module>.fdll.sig = Base64(DER ECDSA P-256 / SHA-256)
        var signaturePath = file + ".sig";
        if (File.Exists(signaturePath))
        {
            try
            {
                var signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim());
                if (!MediaSigner.VerifyDetached(raw, signature))
                    return (null, null, false, "module signature invalid");
                FoldLog.Info($"[FoldEdge] module signature verified: {moduleName}");
            }
            catch (FormatException)
            {
                return (null, null, false, "module .sig is not valid Base64");
            }
        }
        else
        {
            FoldLog.Warn($"[FoldEdge] warning: {moduleName}.fdll has no detached signature");
        }

        // 文件头不是 ZIP（"PK"）时按 FOLDENC1 加密容器处理，仅在内存中解密
        bool encrypted = raw.Length >= FoldCrypto.Magic.Length &&
                         Encoding.ASCII.GetString(raw, 0, FoldCrypto.Magic.Length) == FoldCrypto.Magic;
        var payload = encrypted ? FoldCrypto.Decrypt(raw, FoldCrypto.Magic.Length) : raw;

        using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
        var manifest = ReadEntry(zip, "manifest.json");
        var timeline = ReadEntry(zip, "timeline.json");
        if (string.IsNullOrWhiteSpace(manifest)) return (null, null, encrypted, "manifest.json missing");
        if (string.IsNullOrWhiteSpace(timeline)) return (null, null, encrypted, "timeline.json missing");

        try
        {
            using var _ = JsonDocument.Parse(manifest);
        }
        catch (JsonException)
        {
            return (null, null, encrypted, "manifest.json is not valid JSON");
        }

        try
        {
            using var _ = JsonDocument.Parse(timeline);
        }
        catch (JsonException)
        {
            return (null, null, encrypted, "timeline.json is not valid JSON");
        }

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
    FoldLog.Info($"[FoldEdge] module {moduleName}: {CountTimelineActions(timeline)} timeline action(s) signed");

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
    FoldLog.Info($"[FoldEdge] {Loc.Get("image.converted", frame.Width, frame.Height, sent)}");
    return frame;
}

// ---- 处理插件/客户端发来的请求（hello / image-request / trigger / media-request） ----
async Task HandleClientMessage(Guid clientId, string msg)
{
    try
    {
        using var doc = JsonDocument.Parse(msg);
        if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;
        var type = typeEl.GetString();

        // 角色握手：FE 插件发送 role=fe，FVP-Native 原生客户端发送 role=fvp
        if (type == "hello")
        {
            if (clients.TryGetValue(clientId, out var connection))
            {
                var role = doc.RootElement.TryGetProperty("role", out var roleEl)
                    ? (roleEl.GetString() ?? string.Empty).ToLowerInvariant()
                    : string.Empty;
                connection.Role = role == "fe" || role == "fvp" ? role : "unknown";
                connection.Name = doc.RootElement.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;
                FoldLog.Info($"[FoldEdge] client hello: {clientId} role={connection.Role} name={connection.Name}");
            }
            return;
        }

        if (type == "get_public_key")
        {
            await BroadcastAsync(JsonSerializer.Serialize(new
            {
                type = "public-key",
                publicKey = MediaSigner.PublicKeyBase64
            }, jsonOpts));
            return;
        }

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
            FoldLog.Info(error == null
                ? $"[FoldEdge] 已下发模块: {module}"
                : $"[FoldEdge] 模块下发失败 ({module}): {error}");
        }
    }
    catch (Exception ex)
    {
        FoldLog.Warn($"[FoldEdge] {Loc.Get("image.failed", ex.Message)}");
    }
}

// ---- FileSystemWatcher：监控 Modules/*.fdll ----
async Task OnModuleChanged(object sender, FileSystemEventArgs e)
{
    FoldLog.Info($"[FoldEdge] {Loc.Get("module.changed", Path.GetFileNameWithoutExtension(e.Name), e.ChangeType.ToString())}");
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
FoldLog.Info($"[FoldEdge] {Loc.Get("modules.watching", modulesDir)}");

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
    clients[id] = new ClientConnection(socket);
    FoldLog.Info($"[FoldEdge] {Loc.Get("ws.client.connected", id)}");

    // 握手后立即下发公钥，客户端据此动态初始化验签器。
    var keyPayload = JsonSerializer.Serialize(new
    {
        type = "public-key",
        publicKey = MediaSigner.PublicKeyBase64
    }, jsonOpts);
    await socket.SendAsync(
        Encoding.UTF8.GetBytes(keyPayload),
        WebSocketMessageType.Text,
        true,
        CancellationToken.None);

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
                await HandleClientMessage(id, msg);
            }
        }
    }
    catch (Exception ex)
    {
        clients.TryRemove(id, out _);
        FoldLog.Warn($"[FoldEdge] {Loc.Get("ws.client.error", ex.Message)}");
    }
    finally
    {
        clients.TryRemove(id, out _);
        FoldLog.Info($"[FoldEdge] {Loc.Get("ws.client.disconnected", id)}");
    }
});

// ---- Web 管理面板：GET /dashboard ----
var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
app.MapGet("/dashboard", () => Results.File(Path.Combine(webRoot, "dashboard.html"), "text/html"));

// ---- GET /api/status ----
app.MapGet("/api/status", () =>
{
    var (fe, fvp, unknown) = CountClientRoles();
    return Results.Ok(new
    {
        status = "ok",
        service = "FoldEdge",
        version = FoldVersion,
        listen = listenUrl,
        uptimeSeconds = (long)(DateTime.UtcNow - startTime).TotalSeconds,
        clients = fe + fvp + unknown,
        fe,
        fvp,
        unknown,
        mediaStorage = storageStrategy,
        lastTelemetry,
        users = userStore.All().Count,
        ownerInitialized = userStore.HasOwner,
        vpnServer = vpnService.Server,
        vpnTunnelType = vpnService.TunnelType,
        rateLimitEnabled = dashboardConfig.RateLimitEnabled,
        logLevel = dashboardConfig.LogLevel,
        modules = Directory.GetFiles(modulesDir, "*.fdll")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()
    });
});

// ---- GET /api/modules：面板模块列表（实时读取 Modules/*.fdll） ----
app.MapGet("/api/modules", () =>
{
    var modules = Directory.GetFiles(modulesDir, "*.fdll")
        .Select(path =>
        {
            var info = new FileInfo(path);
            return new
            {
                name = Path.GetFileNameWithoutExtension(path),
                file = info.Name,
                size = info.Length,
                modifiedUtc = info.LastWriteTimeUtc,
                signed = File.Exists(path + ".sig")
            };
        })
        .OrderBy(item => item.name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(new { modules });
});

// ---- SCP:SL 服务器进程生命周期 ----
app.MapGet("/api/server/status", () => Results.Ok(gameServer.GetStatus()));

app.MapPost("/api/server/start", async (HttpContext context) =>
{
    var current = CurrentUser(context);
    if (current == null || !IsOperator(current))
        return Forbidden();

    var result = await gameServer.StartAsync(current.Username);
    return Results.Ok(result);
});

app.MapPost("/api/server/stop", async (HttpContext context) =>
{
    var current = CurrentUser(context);
    if (current == null || !IsOperator(current))
        return Forbidden();

    var result = await gameServer.StopAsync(current.Username);
    return Results.Ok(result);
});

app.MapPost("/api/server/restart", async (HttpContext context) =>
{
    var current = CurrentUser(context);
    if (current == null || !IsOperator(current))
        return Forbidden();

    var result = await gameServer.RestartAsync(current.Username);
    return Results.Ok(result);
});

// ---- 认证 / 初始化 / 用户 / VPN / 日志 ----
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = FoldVersion }));
app.MapGet("/api/setup/status", () => Results.Ok(new { initialized = userStore.HasOwner }));

app.MapPost("/api/setup/owner", (SetupRequest request) =>
{
    if (userStore.HasOwner)
        return Results.Conflict(new { error = "already_initialized" });
    if (!IsValidUsername(request.Username))
        return Results.BadRequest(new { error = "invalid_username" });
    if (!IsStrongPassword(request.Password))
        return Results.BadRequest(new { error = "weak_password" });

    var (salt, hash) = PasswordHasher.Hash(request.Password);
    var owner = new FoldUser
    {
        Username = request.Username.Trim(),
        PasswordSalt = salt,
        PasswordHash = hash,
        Role = "Owner"
    };
    userStore.Add(owner);
    logService.Write("Info", $"Owner 账号已初始化: {owner.Username}", $"Owner account initialized: {owner.Username}");
    string token = jwtService.Issue(owner, TimeSpan.FromHours(12));
    return Results.Ok(new { token, user = ToUserDto(owner) });
});

app.MapPost("/api/auth/login", (LoginRequest request) =>
{
    var user = userStore.FindByUsername(request.Username);
    if (user == null || !PasswordHasher.Verify(request.Password, user.PasswordSalt, user.PasswordHash))
    {
        logService.Write("Warn", $"登录失败: {request.Username}", $"Sign-in failed: {request.Username}");
        return Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
    }
    if (user.RevokedAtUtc != null)
        return Results.Json(new { error = "revoked" }, statusCode: StatusCodes.Status403Forbidden);
    if (user.ExpiresAtUtc.HasValue && user.ExpiresAtUtc.Value <= DateTime.UtcNow)
        return Results.Json(new { error = "expired" }, statusCode: StatusCodes.Status403Forbidden);

    string token = jwtService.Issue(user, TimeSpan.FromHours(12));
    logService.Write("Info", $"用户登录: {user.Username} ({user.Role})", $"User signed in: {user.Username} ({user.Role})");
    return Results.Ok(new { token, user = ToUserDto(user) });
});

app.MapGet("/api/auth/me", (HttpContext context) =>
{
    var user = CurrentUser(context);
    return user == null
        ? Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new { user = ToUserDto(user) });
});

app.MapGet("/api/users", (HttpContext context) =>
{
    var current = CurrentUser(context);
    if (current == null || current.Role != "Owner")
        return Forbidden();

    return Results.Ok(new { users = userStore.All().Select(ToUserDto).ToArray() });
});

app.MapPost("/api/users", (HttpContext context, CreateUserRequest request) =>
{
    var current = CurrentUser(context);
    if (current == null || current.Role != "Owner")
        return Forbidden();
    if (!IsValidUsername(request.Username))
        return Results.BadRequest(new { error = "invalid_username" });
    if (!IsStrongPassword(request.Password))
        return Results.BadRequest(new { error = "weak_password" });
    if (request.Role != "Admin" && request.Role != "Viewer")
        return Results.BadRequest(new { error = "invalid_role" });
    if (userStore.FindByUsername(request.Username) != null)
        return Results.Conflict(new { error = "username_taken" });

    var (salt, hash) = PasswordHasher.Hash(request.Password);
    var user = new FoldUser
    {
        Username = request.Username.Trim(),
        PasswordSalt = salt,
        PasswordHash = hash,
        Role = request.Role,
        SteamId = (request.SteamId ?? string.Empty).Trim(),
        ExpiresAtUtc = request.DurationMinutes > 0
            ? DateTime.UtcNow.AddMinutes(request.DurationMinutes)
            : null,
        Vpn = request.CreateVpn ? vpnService.CreateAccount() : null
    };
    userStore.Add(user);

    logService.Write("Info",
        $"已创建 {user.Role} 用户 {user.Username}" +
        (user.ExpiresAtUtc.HasValue ? $"，有效期至 {user.ExpiresAtUtc:u}" : "（永久）") +
        (user.Vpn != null ? "，已生成 VPN 凭证" : string.Empty),
        $"Created {user.Role} user {user.Username}" +
        (user.ExpiresAtUtc.HasValue ? $", expires {user.ExpiresAtUtc:u}" : " (permanent)") +
        (user.Vpn != null ? ", VPN credentials generated" : string.Empty));

    return Results.Ok(new { user = ToUserDto(user), vpnPassword = user.Vpn?.Password });
});

app.MapPost("/api/users/{id}/revoke", async (HttpContext context, string id) =>
{
    var current = CurrentUser(context);
    if (current == null || current.Role != "Owner")
        return Forbidden();

    var target = userStore.FindById(id);
    if (target == null)
        return Results.NotFound(new { error = "not_found" });
    if (target.Role == "Owner")
        return Results.BadRequest(new { error = "cannot_revoke_owner" });

    if (!userStore.TryMarkRevoked(id, out var revoked) || revoked == null)
        return Results.Ok(new { revoked = false });

    logService.Write("Warn",
        $"Owner 手动吊销用户 {revoked.Username}：Web 会话与 VPN 凭证已失效",
        $"Owner manually revoked user {revoked.Username}: Web session and VPN credential invalidated");

    if (!string.IsNullOrWhiteSpace(revoked.SteamId))
    {
        string payload = JsonSerializer.Serialize(new
        {
            type = "command",
            command = "revoke-access",
            steamId = revoked.SteamId,
            roundRestart = true
        }, jsonOpts);
        int sent = await BroadcastAsync(payload);
        logService.Write("Warn",
            $"已通知 FoldEngine 收回 {revoked.SteamId} 的游戏权限并重启回合（节点 {sent}）",
            $"FoldEngine notified to revoke in-game access for {revoked.SteamId} and restart the round (nodes {sent})");
    }

    return Results.Ok(new { revoked = true, user = ToUserDto(revoked) });
});

app.MapGet("/api/users/{id}/vpn", (HttpContext context, string id) =>
{
    var current = CurrentUser(context);
    if (current == null || current.Role != "Owner")
        return Forbidden();

    var user = userStore.FindById(id);
    if (user?.Vpn == null)
        return Results.NotFound(new { error = "vpn_not_found" });

    return Results.Ok(new
    {
        user = user.Username,
        vpn = new
        {
            username = user.Vpn.Username,
            password = user.Vpn.Password,
            enabled = user.Vpn.Enabled
        },
        server = vpnService.Server,
        tunnelType = vpnService.TunnelType,
        scriptUrl = $"/api/users/{id}/vpn/script"
    });
});

app.MapGet("/api/users/{id}/vpn/script", (HttpContext context, string id) =>
{
    var current = CurrentUser(context);
    if (current == null || current.Role != "Owner")
        return Forbidden();

    var user = userStore.FindById(id);
    if (user?.Vpn == null)
        return Results.NotFound(new { error = "vpn_not_found" });

    string script = vpnService.BuildWindowsScript(user);
    return Results.File(
        Encoding.UTF8.GetBytes(script),
        "text/plain; charset=utf-8",
        $"Fold-{user.Username}.ps1");
});

app.MapPost("/api/vpn/verify", (HttpContext context, VpnVerifyRequest request) =>
{
    string expectedToken = builder.Configuration["VpnGatewayToken"] ?? string.Empty;
    string providedToken = context.Request.Headers["X-VPN-Gateway-Token"].ToString();
    bool isLoopback = context.Connection.RemoteIpAddress != null &&
                      System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

    if (!string.IsNullOrEmpty(expectedToken))
    {
        if (!FixedTimeEquals(providedToken, expectedToken))
            return Results.Json(new { allowed = false, error = "bad_gateway_token" }, statusCode: StatusCodes.Status401Unauthorized);
    }
    else if (!isLoopback)
    {
        return Results.Json(new { allowed = false, error = "gateway_token_required" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var user = userStore.FindByVpnUsername(request.Username);
    bool active = user?.Vpn != null &&
                  user.Vpn.Enabled &&
                  user.RevokedAtUtc == null &&
                  (!user.ExpiresAtUtc.HasValue || user.ExpiresAtUtc.Value > DateTime.UtcNow);

    if (!active || user == null || !FixedTimeEquals(request.Password, user.Vpn!.Password))
        return Results.Json(new { allowed = false }, statusCode: StatusCodes.Status403Forbidden);

    return Results.Ok(new { allowed = true, user = user.Username, role = user.Role });
});

app.MapGet("/api/logs", (HttpContext context, long? after, int? limit) =>
{
    var current = CurrentUser(context);
    if (current == null)
        return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    long cursor = after ?? 0;
    int take = Math.Clamp(limit ?? 200, 1, 500);
    var entries = logService.ReadAfter(cursor, take)
        .Select(entry => new
        {
            id = entry.Id,
            tsUtc = entry.TsUtc,
            level = entry.Level,
            zh = entry.Zh,
            en = entry.En
        })
        .ToArray();

    long latestId = entries.Length > 0 ? entries[^1].id : cursor;
    return Results.Ok(new { entries, latestId });
});

// ---- GET / PUT /api/config：限流开关与日志级别 ----
app.MapGet("/api/config", () => Results.Ok(dashboardConfig));

app.MapPut("/api/config", async (HttpContext context, DashboardConfig updated) =>
{
    var current = CurrentUser(context);
    if (current == null || current.Role != "Owner")
        return Forbidden();
    if (updated == null)
        return Results.BadRequest(new { error = "missing config" });

    dashboardConfig.RateLimitEnabled = updated.RateLimitEnabled;
    dashboardConfig.LogLevel = FoldLog.NormalizeLevel(updated.LogLevel);
    FoldLog.Level = FoldLog.ParseLevel(dashboardConfig.LogLevel);
    dashboardConfig.Save(dashboardConfigPath);

    // FE 收到 type=config 后即时切换限流；其他客户端会忽略该消息
    int sent = await BroadcastAsync(JsonSerializer.Serialize(new
    {
        type = "config",
        rateLimitEnabled = dashboardConfig.RateLimitEnabled,
        logLevel = dashboardConfig.LogLevel
    }, jsonOpts));

    logService.Write("Info",
        $"配置已更新：限流 {dashboardConfig.RateLimitEnabled}，日志 {dashboardConfig.LogLevel}（广播 {sent} 节点）",
        $"Config updated: rate limit {dashboardConfig.RateLimitEnabled}, log level {dashboardConfig.LogLevel} (broadcast {sent} nodes)");
    return Results.Ok(dashboardConfig);
});

// ---- POST /api/trigger/{moduleName}：读取 .fdll 并下发播放指令 ----
app.MapPost("/api/trigger/{moduleName}", async (HttpContext context, string moduleName) =>
{
    var current = CurrentUser(context);
    if (current == null || !IsOperator(current))
        return Forbidden();

    var error = await SendPlayCommandAsync(moduleName);
    if (error != null)
        return Results.NotFound(new { error = $"module '{moduleName}': {error}" });

    logService.Write("Info",
        $"{current.Username} 触发模块 {moduleName}（在线 {CountAliveClients()}）",
        $"{current.Username} triggered module {moduleName} ({CountAliveClients()} online)");
    return Results.Ok(new { sent = true, module = moduleName, clients = CountAliveClients() });
});

// ---- POST /api/image：测试接口 ----
//  body: { "url": "...", "maxWidth": 8, "maxHeight": 6, "duration": 8 }
//  url 传 "test"/"smiley" 走本地生成图；其余下载 → 转 JSON 颜色矩阵 → 推送给已连接的 FoldEngine 插件。
app.MapPost("/api/image", async (HttpContext context) =>
{
    var current = CurrentUser(context);
    if (current == null || !IsOperator(current))
        return Forbidden();

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
    var current = CurrentUser(context);
    if (current == null || !IsOperator(current))
        return Forbidden();

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
app.MapPost("/api/module/publish", (HttpContext context, ModulePublishRequest request) =>
{
    var current = CurrentUser(context);
    if (current == null || !IsOperator(current))
        return Forbidden();

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

app.MapPost("/api/control/stop", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var current = CurrentUser(context);
    if (current == null || !IsOperator(current))
        return Forbidden();

    return await SendControlAsync("STOP", 0);
});

app.MapPost("/api/control/jump/{seconds:double}",
    async (HttpContext context, double seconds, CancellationToken cancellationToken) =>
    {
        var current = CurrentUser(context);
        if (current == null || !IsOperator(current))
            return Forbidden();

        return await SendControlAsync("JUMP", seconds);
    });

// ---- POST /api/telemetry：FVP-Native 播放状态（HMAC 校验） ----
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
    FoldLog.Info($"[FoldEdge] telemetry: {lastTelemetry}");
    return Results.Ok(new { ok = true });
});

// ---- 优雅退出：释放 Watcher 并关闭全部连接 ----
app.Lifetime.ApplicationStopping.Register(() =>
{
    watcher.Dispose();
    foreach (var connection in clients.Values)
    {
        try
        {
            connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "FoldEdge shutdown", CancellationToken.None).Wait();
        }
        catch
        {
            // 忽略退出时的连接清理异常
        }
    }
});

// ---- 限时权限到期检查（每 5 秒） ----
var expiryService = new ExpiryService(userStore, logService, json => BroadcastAsync(json));
expiryService.Start();
app.Lifetime.ApplicationStopping.Register(expiryService.Stop);

// ---- SCP:SL 进程：启动时自动接管/静默启动，退出时确保清理 ----
app.Lifetime.ApplicationStarted.Register(gameServer.AutoStartIfConfigured);
app.Lifetime.ApplicationStopping.Register(gameServer.Dispose);

logService.Write("Info", $"FoldEdge {FoldVersion} 已启动", $"FoldEdge {FoldVersion} started");
app.Run();

// ---- 认证 / 权限辅助 ----
static bool IsPublicApiPath(string path)
{
    return path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/api/setup/status", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/api/setup/owner", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/api/telemetry", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/api/vpn/verify", StringComparison.OrdinalIgnoreCase);
}

static FoldUser? CurrentUser(HttpContext context)
{
    return context.Items["foldUser"] as FoldUser;
}

static bool IsOperator(FoldUser user)
{
    return user.Role == "Owner" || user.Role == "Admin";
}

static IResult Forbidden()
{
    return Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
}

static object ToUserDto(FoldUser user)
{
    return new
    {
        id = user.Id,
        username = user.Username,
        role = user.Role,
        steamId = user.SteamId,
        createdAtUtc = user.CreatedAtUtc,
        expiresAtUtc = user.ExpiresAtUtc,
        revokedAtUtc = user.RevokedAtUtc,
        active = user.RevokedAtUtc == null &&
                 (!user.ExpiresAtUtc.HasValue || user.ExpiresAtUtc.Value > DateTime.UtcNow),
        vpn = user.Vpn == null
            ? null
            : (object)new
            {
                username = user.Vpn.Username,
                enabled = user.Vpn.Enabled,
                createdAtUtc = user.Vpn.CreatedAtUtc
            }
    };
}

static bool IsValidUsername(string username)
{
    if (string.IsNullOrWhiteSpace(username))
        return false;

    string value = username.Trim();
    if (value.Length < 3 || value.Length > 32)
        return false;

    foreach (char c in value)
    {
        bool allowed = char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
        if (!allowed)
            return false;
    }

    return true;
}

static bool IsStrongPassword(string password)
{
    return !string.IsNullOrWhiteSpace(password) && password.Length >= 8;
}

static bool FixedTimeEquals(string left, string right)
{
    if (left == null || right == null || left.Length != right.Length)
        return false;

    int diff = 0;
    for (int i = 0; i < left.Length; i++)
        diff |= left[i] ^ right[i];

    return diff == 0;
}

/// <summary>POST /api/image 请求体（默认 8x6）。</summary>
public sealed record ImageRequest(string Url, int MaxWidth = 8, int MaxHeight = 6, float Duration = 8f);

public sealed class SetupRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = "Viewer";
    public string? SteamId { get; set; }
    public int DurationMinutes { get; set; }
    public bool CreateVpn { get; set; }
}

public sealed class VpnVerifyRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

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

/// <summary>一条 WebSocket 连接的角色信息（fe / fvp / unknown）。</summary>
internal sealed class ClientConnection
{
    public ClientConnection(WebSocket socket)
    {
        Socket = socket;
        ConnectedUtc = DateTime.UtcNow;
    }

    public WebSocket Socket { get; }
    public DateTime ConnectedUtc { get; }
    public string Role { get; set; } = "unknown";
    public string Name { get; set; } = string.Empty;
}

/// <summary>管理面板可修改的持久化配置。</summary>
public sealed class DashboardConfig
{
    public bool RateLimitEnabled { get; set; } = true;
    public string LogLevel { get; set; } = "Info";

    public static DashboardConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new DashboardConfig();

            var config = JsonSerializer.Deserialize<DashboardConfig>(File.ReadAllText(path));
            return config ?? new DashboardConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FoldEdge] config load failed: {ex.Message}");
            return new DashboardConfig();
        }
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FoldEdge] config save failed: {ex.Message}");
        }
    }
}

/// <summary>FoldEdge 控制台日志级别：由管理面板配置中心实时切换。</summary>
internal enum FoldLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

internal static class FoldLog
{
    public static FoldLogLevel Level { get; set; } = FoldLogLevel.Info;

    /// <summary>统一日志中心；为空时仅输出到控制台。</summary>
    public static LogService? Sink { get; set; }

    public static FoldLogLevel ParseLevel(string value)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "debug": return FoldLogLevel.Debug;
            case "warn":
            case "warning": return FoldLogLevel.Warn;
            case "error": return FoldLogLevel.Error;
            default: return FoldLogLevel.Info;
        }
    }

    public static string NormalizeLevel(string value)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "debug": return "Debug";
            case "warn":
            case "warning": return "Warn";
            case "error": return "Error";
            default: return "Info";
        }
    }

    public static void Debug(string message)
    {
        if (Level <= FoldLogLevel.Debug)
        {
            Console.WriteLine("[FoldEdge][debug] " + message);
            Sink?.Write("Debug", message);
        }
    }

    public static void Info(string message)
    {
        if (Level <= FoldLogLevel.Info)
        {
            Console.WriteLine(message);
            Sink?.Write("Info", message);
        }
    }

    public static void Warn(string message)
    {
        if (Level <= FoldLogLevel.Warn)
        {
            Console.WriteLine("[FoldEdge][warn] " + message);
            Sink?.Write("Warn", message);
        }
    }

    public static void Error(string message)
    {
        if (Level <= FoldLogLevel.Error)
        {
            Console.WriteLine("[FoldEdge][error] " + message);
            Sink?.Write("Error", message);
        }
    }
}
