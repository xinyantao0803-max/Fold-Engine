using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FoldClient
{
    /// <summary>
    /// 连接 FoldEdge /ws，接收 {"type":"command","command":"media","marker":...}。
    /// 播放请求统一 Dispatcher 回 UI 线程，解码器自身在后台线程运行。
    /// </summary>
    internal sealed class FoldEdgeClient : IDisposable
    {
        private readonly string _url;
        private readonly OverlayWindow _overlay;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _gate = new object();

        private ClientWebSocket _socket;
        private string _pendingRequest;

        public FoldEdgeClient(string url, OverlayWindow overlay)
        {
            _url = url;
            _overlay = overlay;
        }

        public void Start()
        {
            _ = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>请求 FH 现场签名一条媒体指令（用于测试或主动触发）。</summary>
        public void RequestMedia(string kind, string url)
        {
            string json = "{\"type\":\"media-request\",\"kind\":\"" + Escape(kind) +
                          "\",\"url\":\"" + Escape(url) +
                          "\",\"volume\":1,\"duration\":5}";

            lock (_gate)
            {
                _pendingRequest = json;
            }

            _ = SendAsync(json);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var socket = new ClientWebSocket())
                    {
                        lock (_gate)
                        {
                            _socket = socket;
                        }

                        await socket.ConnectAsync(new Uri(_url), ct);
                        AppLog.Write("connected to FoldEdge: " + _url);

                        // 主动索取公钥；FH 也会在握手后自动推送一次。
                        await SendOnSocketAsync(socket, "{\"type\":\"get_public_key\"}");

                        string pending;
                        lock (_gate)
                        {
                            pending = _pendingRequest;
                            _pendingRequest = null;
                        }

                        if (!string.IsNullOrEmpty(pending))
                            await SendOnSocketAsync(socket, pending);

                        await ReceiveLoopAsync(socket, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Write("FoldEdge connection failed: " + ex.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        _socket = null;
                    }
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
            var builder = new StringBuilder();

            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result =
                    await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    string json = builder.ToString();
                    builder.Clear();
                    Handle(json);
                }
            }
        }

        private void Handle(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("type", out JsonElement type))
                    return;

                string typeName = type.GetString();

                if (typeName == "public-key")
                {
                    string publicKey = root.TryGetProperty("publicKey", out JsonElement keyElement)
                        ? keyElement.GetString()
                        : null;

                    bool accepted = SignedMediaCommand.SetPublicKey(publicKey);
                    AppLog.Write("public key received: length=" +
                                 (publicKey == null ? 0 : publicKey.Length) +
                                 ", accepted=" + accepted);
                    return;
                }

                if (typeName != "command")
                    return;

                if (!root.TryGetProperty("command", out JsonElement command) ||
                    command.GetString() != "media")
                    return;

                if (!root.TryGetProperty("marker", out JsonElement markerElement))
                    return;

                string marker = markerElement.GetString();
                if (string.IsNullOrEmpty(marker))
                    return;

                int end = marker.IndexOf(']');
                if (end <= 0)
                    return;

                string prefix = marker.Substring(0, end + 1);
                string body = marker.Substring(end + 1);
                string kind = KindFor(prefix);
                if (kind == null)
                    return;

                if (!SignedMediaCommand.TryVerify(kind, body, out string payload))
                {
                    AppLog.Write("signature rejected: " + prefix);
                    return;
                }

                if (!FvpConfig.Allows(payload))
                {
                    AppLog.Write("blocked by parental control");
                    return;
                }

                string url = SignedMediaCommand.GetString(payload, "url");
                if (string.IsNullOrEmpty(url) && kind != "text")
                {
                    AppLog.Write("media payload missing url: " + prefix);
                    return;
                }

                if (kind == "video")
                    _overlay.Dispatcher.BeginInvoke(new Action(() => _overlay.PlayVideo(url)));
                else if (kind == "image")
                    _overlay.Dispatcher.BeginInvoke(new Action(() => _overlay.PlayImage(url)));
                else
                    AppLog.Write("unsupported media kind: " + kind);
            }
            catch (Exception ex)
            {
                AppLog.Write("message error: " + ex.Message);
            }
        }

        private static string KindFor(string prefix)
        {
            switch (prefix)
            {
                case "[FOLD_VIDEO]": return "video";
                case "[FOLD_IMG]": return "image";
                case "[FOLD_AUDIO]": return "audio";
                case "[FOLD_TEXT]": return "text";
                case "[FOLD_CMD]": return "cmd";
                default: return null;
            }
        }

        private async Task SendAsync(string json)
        {
            ClientWebSocket socket;
            lock (_gate)
            {
                socket = _socket;
            }

            if (socket == null || socket.State != WebSocketState.Open)
                return;

            await SendOnSocketAsync(socket, json);

            lock (_gate)
            {
                if (_pendingRequest == json)
                    _pendingRequest = null;
            }
        }

        private static Task SendOnSocketAsync(ClientWebSocket socket, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            return socket.SendAsync(
                new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        public void Dispose()
        {
            _cts.Cancel();

            ClientWebSocket socket;
            lock (_gate)
            {
                socket = _socket;
            }

            try { socket?.Abort(); }
            catch { /* 已断开 */ }

            _cts.Dispose();
        }
    }
}
