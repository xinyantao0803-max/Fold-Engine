using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features.Console;

namespace FoldEngine
{
    /// <summary>主程序下发的图片帧（JSON 颜色矩阵）。</summary>
    public sealed class ImageFrameData
    {
        public int Width;
        public int Height;
        public float Duration = 8f;
        public List<PixelData> Pixels = new List<PixelData>();

        public static ImageFrameData Parse(string json)
        {
            var frame = new ImageFrameData
            {
                Width = (int)MiniJson.GetNumberValue(json, "width", 0),
                Height = (int)MiniJson.GetNumberValue(json, "height", 0),
                Duration = MiniJson.GetNumberValue(json, "duration", 8f)
            };
            if (frame.Width <= 0 || frame.Height <= 0) return null;

            var body = MiniJson.ExtractArrayBody(json, "frames");
            if (string.IsNullOrEmpty(body)) return null;

            foreach (var chunk in MiniJson.SplitObjects(body))
            {
                frame.Pixels.Add(new PixelData
                {
                    R = (byte)MiniJson.GetNumberValue(chunk, "r", 0),
                    G = (byte)MiniJson.GetNumberValue(chunk, "g", 0),
                    B = (byte)MiniJson.GetNumberValue(chunk, "b", 0),
                    A = (byte)MiniJson.GetNumberValue(chunk, "a", 255)
                });
            }
            return frame;
        }
    }

    /// <summary>单个像素（RGBA）。</summary>
    public struct PixelData
    {
        public byte R, G, B, A;
    }

    /// <summary>
    /// FoldEdge 主程序的 WebSocket 客户端（纯 .NET 4.8 ClientWebSocket，零 Unity 依赖）。
    /// 后台线程收发；断线自动重连。插件端不下载/解码任何图片，只接收帧数据纯渲染。
    /// 注：广播从非主线程调用在 SCP:SL 社区插件中常见，如遇稳定性问题可改为队列 + 主线程轮询。
    /// </summary>
    public sealed class FoldEdgeClient
    {
        public static FoldEdgeClient Instance { get; } = new FoldEdgeClient();

        /// <summary>收到图片帧时触发（后台线程）。</summary>
        public event Action<ImageFrameData> ImageFrameReceived;

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;

        /// <summary>启动并保持与 FoldEdge 主程序的连接。</summary>
        public void Start(string url = "ws://127.0.0.1:9000/ws")
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => RunAsync(url, token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _socket?.Abort(); } catch { /* ignore */ }
        }

        /// <summary>请求 FoldEdge 转换并推送一张图片（url → 颜色矩阵 → 回推渲染）。</summary>
        public void RequestImage(string url)
        {
            var json = "{\"type\":\"image-request\",\"url\":\"" + url.Replace("\"", "\\\"") + "\"}";
            _ = SendAsync(json);
        }

        /// <summary>请求 FoldEdge 读取指定的 .fdll 模块并回推 manifest + timeline。</summary>
        public void TriggerModule(string moduleName)
        {
            var json = "{\"type\":\"trigger\",\"module\":\"" + moduleName.Replace("\"", "\\\"") + "\"}";
            _ = SendAsync(json);
        }

        private async Task SendAsync(string json)
        {
            try
            {
                var socket = _socket;
                if (socket == null || socket.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // 发送失败由重连循环兜底
            }
        }

        private async Task RunAsync(string url, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var socket = new ClientWebSocket())
                    {
                        _socket = socket;
                        await socket.ConnectAsync(new Uri(url), ct);
                        Logger.Info($"[Fold] 已连接 FoldEdge: {url}");
                        await ReceiveLoopAsync(socket, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Fold] FoldEdge 连接失败: {ex.Message}，5 秒后重试");
                }
                finally
                {
                    _socket = null;
                }
                try
                {
                    await Task.Delay(5000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16384];
            var sb = new StringBuilder();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    var json = sb.ToString();
                    sb.Clear();
                    HandleMessage(json);
                }
            }
        }

        private void HandleMessage(string json)
        {
            try
            {
                var type = MiniJson.GetStringValue(json, "type");
                if (type == "image-frame")
                {
                    var frame = ImageFrameData.Parse(json);
                    if (frame != null)
                    {
                        ImageFrameReceived?.Invoke(frame);
                    }
                    return;
                }

                if (type == "command" && HandleCommand(json)) return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Fold] 帧解析失败: {ex.Message}");
            }
        }

        /// <summary>处理主程序指令：play → 注入合规标签 + 播放时间轴。</summary>
        private bool HandleCommand(string json)
        {
            if (!string.Equals(MiniJson.GetStringValue(json, "command"), "play",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var module = MiniJson.GetStringValue(json, "module") ?? "unknown";
            var manifest = MiniJson.GetStringValue(json, "manifest") ?? string.Empty;
            var timeline = MiniJson.GetStringValue(json, "timeline") ?? string.Empty;

            // 合规标签来自 .fdll 的 manifest.json，由主程序原样下发
            FoldCore.ApplyComplianceTags(PlaylistTransport.ParseTags(manifest));

            var items = PlaylistTransport.ParseTimeline(timeline);
            Logger.Info($"[Fold] 收到模块 {module}：{items.Count} 个时间轴事件");
            _ = FoldCore.UI?.Play(module, items);
            return true;
        }
    }
}
