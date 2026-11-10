using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FoldEdge;

// ============================================================
//  FoldEdge —— Fold 引擎外部主程序 (.NET 8 / ASP.NET Core)
//  监听 127.0.0.1:9000，作为游戏内 FoldEngine 插件的控制端：
//    GET  /api/status            查询运行状态
//    POST /api/trigger/{module}  向插件下发播放指令
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
    Console.WriteLine($"[FoldEdge] 图片已转换 {frame.Width}x{frame.Height}，推送 {clients.Count} 个客户端");
    await BroadcastAsync(payload);
    return frame;
}

// ---- 处理插件发来的请求（当前支持 image-request） ----
async Task HandleClientMessage(string msg)
{
    try
    {
        using var doc = JsonDocument.Parse(msg);
        if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;
        if (typeEl.GetString() == "image-request" &&
            doc.RootElement.TryGetProperty("url", out var urlEl) &&
            !string.IsNullOrEmpty(urlEl.GetString()))
        {
            await PushImageAsync(urlEl.GetString()!, 8, 6, 8f);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FoldEdge] 客户端消息处理失败: {ex.Message}");
    }
}

// ---- FileSystemWatcher：监控 Modules/*.fdll ----
async Task OnModuleChanged(object sender, FileSystemEventArgs e)
{
    Console.WriteLine($"[FoldEdge] 模块变化: {e.ChangeType} {e.Name}");
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
Console.WriteLine($"[FoldEdge] 已开始监控: {modulesDir}");

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
    Console.WriteLine($"[FoldEdge] 客户端接入: {id}");

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
        Console.WriteLine($"[FoldEdge] 客户端连接异常断开: {ex.Message}");
    }
    finally
    {
        clients.TryRemove(id, out _);
        Console.WriteLine($"[FoldEdge] 客户端断开: {id}");
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

// ---- POST /api/trigger/{moduleName}：下发播放指令 ----
app.MapPost("/api/trigger/{moduleName}", async (string moduleName) =>
{
    var file = Path.Combine(modulesDir, moduleName + ".fdll");
    if (!File.Exists(file))
        return Results.NotFound(new { error = $"module '{moduleName}' not found" });

    await BroadcastAsync(JsonSerializer.Serialize(new
    {
        type = "command",
        command = "play",
        module = moduleName,
        sentAt = DateTime.UtcNow
    }));

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
