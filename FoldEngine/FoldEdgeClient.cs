using System;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features.Console;

namespace FoldEngine
{
    /// <summary>
    /// FoldEdge 主程序的 WebSocket 客户端（纯 .NET 4.8 ClientWebSocket，零 Unity 依赖）。
    /// 后台线程收发；断线自动重连。插件只接收指令并原样广播，不做任何媒体解码。
    /// </summary>
    public sealed class FoldEdgeClient
    {
        public static FoldEdgeClient Instance { get; } = new FoldEdgeClient();

        // 控制指令 4KB；加密模块/时间轴允许更大，但仍有 256KB 硬上限。
        private const int MaxControlBytes = 4096;
        private const int MaxPacketBytes = 256 * 1024;
        private const int MaxTimelineChars = 200_000;
        private const int MaxManifestChars = 32_000;

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

        /// <summary>请求 FoldEdge 读取指定的 .fdll 模块并回推 manifest + timeline。</summary>
        public void TriggerModule(string moduleName)
        {
            if (!IsSafeModuleName(moduleName))
            {
                Logger.Warn("[Fold] 拒绝非法模块名");
                return;
            }

            var json = "{\"type\":\"trigger\",\"module\":\"" + moduleName.Replace("\"", "\\\"") + "\"}";
            _ = SendAsync(json);
        }

        /// <summary>请求 FoldEdge 现场签名一条媒体指令（插件不持有密钥）。</summary>
        public void RequestMedia(string kind, string url = null, string text = null,
            float volume = 1f, float duration = 5f)
        {
            if (kind != "image" && kind != "video" && kind != "audio" && kind != "text")
                return;
            if (url != null && url.Length > 2048)
            {
                Logger.Warn("[Fold] 拒绝超长媒体 URL");
                return;
            }
            if (text != null && text.Length > 2048)
            {
                Logger.Warn("[Fold] 拒绝超长字幕文本");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("{\"type\":\"media-request\",\"kind\":\"").Append(kind).Append('"');
            if (!string.IsNullOrEmpty(url))
                sb.Append(",\"url\":\"").Append(Escape(url)).Append('"');
            if (!string.IsNullOrEmpty(text))
                sb.Append(",\"text\":\"").Append(Escape(text)).Append('"');
            sb.Append(",\"volume\":").Append(volume.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"duration\":").Append(duration.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            _ = SendAsync(sb.ToString());
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        private static bool IsSignedEnvelope(string marker)
        {
            if (string.IsNullOrEmpty(marker) || marker.Length > MaxControlBytes)
                return false;

            int end = marker.IndexOf(']');
            if (end <= 6 || end > 32)
                return false;

            string prefix = marker.Substring(0, end + 1);
            switch (prefix)
            {
                case "[FOLD_VIDEO]":
                case "[FOLD_IMG]":
                case "[FOLD_AUDIO]":
                case "[FOLD_TEXT]":
                case "[FOLD_CMD]":
                    break;
                default:
                    return false;
            }

            string body = marker.Substring(end + 1);
            int first = body.IndexOf('|');
            int second = first < 0 ? -1 : body.IndexOf('|', first + 1);
            int third = second < 0 ? -1 : body.IndexOf('|', second + 1);
            if (first <= 0 || second <= first + 1 || third <= second + 1)
                return false;

            string timestamp = body.Substring(0, first);
            string nonce = body.Substring(first + 1, second - first - 1);
            string signature = body.Substring(second + 1, third - second - 1);
            string payload = body.Substring(third + 1);

            long parsed;
            if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return false;
            if (nonce.Length < 8 || nonce.Length > 64)
                return false;
            if (signature.Length < 16 || signature.Length > 256)
                return false;
            if (payload.Length == 0 || payload.Length > 2048)
                return false;

            return true;
        }

        private static bool IsSafeModuleName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64)
                return false;

            foreach (char c in name)
            {
                bool allowed = (c >= 'a' && c <= 'z') ||
                               (c >= 'A' && c <= 'Z') ||
                               (c >= '0' && c <= '9') ||
                               c == '_' || c == '-';
                if (!allowed)
                    return false;
            }

            return true;
        }

        private async Task SendAsync(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json) || json.Length > MaxControlBytes)
                    return;

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
            var buffer = new byte[8192];
            var sb = new StringBuilder();
            int messageBytes = 0;
            bool oversized = false;

            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                messageBytes += result.Count;

                if (messageBytes > MaxPacketBytes)
                {
                    oversized = true;
                    sb.Clear();
                }
                else if (!oversized)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }

                if (result.EndOfMessage)
                {
                    if (oversized)
                    {
                        Logger.Warn("[Fold] 丢弃超长数据包");
                    }
                    else if (sb.Length > 0)
                    {
                        HandleMessage(sb.ToString(), messageBytes);
                    }

                    sb.Clear();
                    messageBytes = 0;
                    oversized = false;
                }
            }
        }

        private void HandleMessage(string json, int byteCount)
        {
            try
            {
                var type = MiniJson.GetStringValue(json, "type");
                if (type == "command" && HandleCommand(json, byteCount)) return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Fold] 指令解析失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>处理主程序指令：media 原样广播；play 注入合规标签并播放时间轴。</summary>
        private bool HandleCommand(string json, int byteCount)
        {
            var command = MiniJson.GetStringValue(json, "command");

            // 媒体/控制标记由 FoldEdge 签名，插件只做原样透传
            if (string.Equals(command, "media", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "control", StringComparison.OrdinalIgnoreCase))
            {
                if (byteCount > MaxControlBytes)
                {
                    Logger.Warn("[Fold] 丢弃超长控制指令");
                    return true;
                }

                var marker = MiniJson.GetStringValue(json, "marker");
                if (!string.IsNullOrEmpty(marker))
                {
                    // FE 不持有私钥；只允许原样转发 FH 生成的签名信封。
                    if (!IsSignedEnvelope(marker))
                    {
                        Logger.Warn("[Fold] 拒绝转发未签名/畸形指令");
                        return true;
                    }

                    FoldCore.RunOnMain(() => FoldCore.UI?.BroadcastMarker(marker));
                }
                return true;
            }

            if (!string.Equals(command, "play", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var module = MiniJson.GetStringValue(json, "module");
            if (!IsSafeModuleName(module))
            {
                Logger.Warn("[Fold] 拒绝非法模块名");
                return true;
            }

            var manifest = MiniJson.GetStringValue(json, "manifest") ?? string.Empty;
            var timeline = MiniJson.GetStringValue(json, "timeline") ?? string.Empty;
            if (manifest.Length > MaxManifestChars || timeline.Length > MaxTimelineChars)
            {
                Logger.Warn("[Fold] 丢弃超长 manifest/timeline");
                return true;
            }

            // 加密 .fdll：主程序以 enc 字段下发 AES-256-CBC 密文，在内存解密后再解析
            var enc = MiniJson.GetStringValue(json, "enc");
            if (!string.IsNullOrEmpty(enc))
            {
                if (enc.Length > MaxPacketBytes)
                {
                    Logger.Warn("[Fold] 丢弃超长加密模块");
                    return true;
                }

                var payload = FoldCrypto.DecryptToJson(enc);
                manifest = MiniJson.GetStringValue(payload, "manifest") ?? string.Empty;
                timeline = MiniJson.GetStringValue(payload, "timeline") ?? string.Empty;

                if (manifest.Length > MaxManifestChars || timeline.Length > MaxTimelineChars)
                {
                    Logger.Warn("[Fold] 解密后 manifest/timeline 超长");
                    return true;
                }
            }

            var tags = PlaylistTransport.ParseTags(manifest);
            var items = PlaylistTransport.ParseTimeline(timeline);
            if (items == null || items.Count > 512)
            {
                Logger.Warn("[Fold] 时间轴事件过多或格式无效，已拒绝");
                return true;
            }

            Logger.Info($"[Fold] 收到模块 {module}：{items.Count} 个时间轴事件");

            // 解析在后台线程完成；合规标签注入与播放启动切回主线程。
            FoldCore.RunOnMain(() =>
            {
                FoldCore.ApplyComplianceTags(tags);
                var ui = FoldCore.UI;
                if (ui != null)
                    _ = ui.Play(module, items);
            });

            return true;
        }
    }
}
