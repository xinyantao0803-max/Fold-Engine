using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FoldEngine
{
    /// <summary>播放列表条目：时间轴上的一个事件节点。</summary>
    public struct PlaylistItem
    {
        public float Time;    // 触发时间（秒）
        public string Action; // 动作：SHOW_TEXT / SHOW_IMAGE / PLAY_VIDEO / PLAY_AUDIO / CHANGE_ROLE / SPAWN_ITEM / CONTROL_DOOR / CLEAR
        public string Url;    // CDN 地址（文本动作时作为文本内容）
        public string Raw;    // 原始 JSON 片段，供动作执行器读取 playerId / role / itemId 等扩展参数
    }

    /// <summary>
    /// PlaylistTransport：.fdll 数据包解析层。
    /// 负责把 manifest.json 与 timeline.json 解析为可播放的列表（纯字符串解析，见 MiniJson）。
    /// </summary>
    public static class PlaylistTransport
    {
        /// <summary>解析 manifest.json，返回模块名（缺失时回退到 id）。</summary>
        public static string ParseManifestName(string manifestJson)
        {
            if (string.IsNullOrEmpty(manifestJson)) return "unknown";
            var name = MiniJson.GetStringValue(manifestJson, "name");
            if (!string.IsNullOrEmpty(name)) return name;
            var id = MiniJson.GetStringValue(manifestJson, "id");
            return string.IsNullOrEmpty(id) ? "unknown" : id;
        }

        /// <summary>解析 manifest.json 的合规标签数组（18+ / GORE / FLASH / POLITICS 等）。</summary>
        public static List<string> ParseTags(string manifestJson)
        {
            return string.IsNullOrEmpty(manifestJson)
                ? new List<string>()
                : MiniJson.ExtractStringArray(manifestJson, "tags");
        }

        /// <summary>解析 timeline.json，返回按时间升序排列的播放列表。</summary>
        public static List<PlaylistItem> ParseTimeline(string timelineJson)
        {
            var items = new List<PlaylistItem>();
            if (string.IsNullOrEmpty(timelineJson)) return items;

            var body = MiniJson.ExtractArrayBody(timelineJson, "entries");
            if (string.IsNullOrEmpty(body))
            {
                // 规范允许 timeline.json 直接是裸数组 [...]，也兼容 { "entries": [...] }
                var trimmed = timelineJson.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]") && trimmed.Length >= 2)
                {
                    body = trimmed.Substring(1, trimmed.Length - 2).Trim();
                }
            }
            if (string.IsNullOrEmpty(body)) return items;

            foreach (var chunk in MiniJson.SplitObjects(body))
            {
                var action = MiniJson.GetStringValue(chunk, "action");
                if (string.IsNullOrEmpty(action)) action = "SHOW_TEXT";
                var url = MiniJson.GetStringValue(chunk, "url") ?? string.Empty;
                // SHOW_TEXT 允许使用 "text" 字段代替 "url"
                if (url.Length == 0) url = MiniJson.GetStringValue(chunk, "text") ?? string.Empty;
                var time = MiniJson.GetNumberValue(chunk, "time", 0f);

                items.Add(new PlaylistItem { Time = time, Action = action, Url = url, Raw = chunk });
            }
            items.Sort((a, b) => a.Time.CompareTo(b.Time));
            return items;
        }
    }

    /// <summary>
    /// 双缓冲监控画风渲染器。
    /// 受 SLAC 限制不推送高分辨率图片，改用“像素监控风格”ASCII 文本帧：
    /// 前台缓冲区负责当前显示，后台缓冲区负责预拼装下一帧，帧末交换。
    /// </summary>
    public class MonitorRenderer
    {
        private const int FrameWidth = 40;
        private const int FrameHeight = 8;

        private readonly StringBuilder _front = new StringBuilder();
        private readonly StringBuilder _back = new StringBuilder();

        /// <summary>把当前状态帧渲染到前台缓冲并返回。</summary>
        public string RenderFrame(string module, PlaylistItem current, float countdown)
        {
            _front.Clear();
            _front.AppendLine($"[FOLD MONITOR] {module}");
            _front.AppendLine(new string('=', FrameWidth));
            _front.AppendLine($"EVENT : {current.Action.ToUpperInvariant()}");
            _front.AppendLine($"T-{countdown:F0}s");
            _front.AppendLine($"SRC   : {current.Url}");
            _front.AppendLine(new string('-', FrameWidth));
            for (int y = 0; y < FrameHeight; y++)
            {
                for (int x = 0; x < FrameWidth; x++)
                {
                    _front.Append(((x * 7 + y * 13) % 5) switch
                    {
                        0 => '█',
                        1 => '▓',
                        2 => '▒',
                        _ => '░'
                    });
                }
                _front.AppendLine();
            }
            return _front.ToString();
        }

        /// <summary>后台缓冲：预拼装下一事件信息（等待间隙调用）。</summary>
        public void PrepareNextFrame(PlaylistItem next)
        {
            _back.Clear();
            _back.Append($"[NEXT {next.Time:F0}s] {next.Action} -> {next.Url}");
        }

        /// <summary>交换前后缓冲，返回新的前台内容。</summary>
        public string Swap()
        {
            string tmp = _front.ToString();
            _front.Clear().Append(_back);
            _back.Clear().Append(tmp);
            return _front.ToString();
        }
    }

    /// <summary>
    /// 帧渲染器（终极降维版）：8x6 共 48 像素。
    /// 彩色版：缩写颜色标签 &lt;color=#F00&gt;■&lt;/color&gt;（无 mspace）；
    /// 灰阶降级版：纯 ".:+#@" 字符，只保轮廓。
    /// </summary>
    public static class FrameToText
    {
        public const int FixedWidth = 8;
        public const int FixedHeight = 6;
        private const char BlockChar = '■'; // 若游戏内乱码，改用 '█' (U+2588)
        private const string GrayRamp = ".:+#@";

        /// <summary>彩色版：三字符缩写色值，无 mspace，行尾 \n。</summary>
        public static string BuildColored(IList<PixelData> pixels)
        {
            if (pixels == null || pixels.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            for (int y = 0; y < FixedHeight; y++)
            {
                for (int x = 0; x < FixedWidth; x++)
                {
                    int i = y * FixedWidth + x;
                    var p = i < pixels.Count ? pixels[i] : default(PixelData);
                    if (p.A < 32)
                    {
                        sb.Append(' ');
                        continue;
                    }
                    sb.Append("<color=#");
                    sb.Append(ToHex(p.R));
                    sb.Append(ToHex(p.G));
                    sb.Append(ToHex(p.B));
                    sb.Append('>');
                    sb.Append(BlockChar);
                    sb.Append("</color>");
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>灰阶降级版：纯字符，无任何标签（最短形式）。</summary>
        public static string BuildGrayscale(IList<PixelData> pixels)
        {
            if (pixels == null || pixels.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            for (int y = 0; y < FixedHeight; y++)
            {
                for (int x = 0; x < FixedWidth; x++)
                {
                    int i = y * FixedWidth + x;
                    var p = i < pixels.Count ? pixels[i] : default(PixelData);
                    if (p.A < 32)
                    {
                        sb.Append(' ');
                        continue;
                    }
                    int gray = (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
                    int idx = Math.Min(GrayRamp.Length - 1, gray * (GrayRamp.Length - 1) / 255);
                    sb.Append(GrayRamp[idx]);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>0-255 → 单字符十六进制（0-F）。</summary>
        private static char ToHex(byte v)
        {
            int n = v * 15 / 255;
            return (char)(n < 10 ? '0' + n : 'A' + (n - 10));
        }
    }

    /// <summary>
    /// Fold 引擎 UI 管理器（纯服务端实现，无任何 Unity 依赖）。
    /// 倒计时逻辑使用 C# 原生 async/await + Task.Delay 驱动，
    /// 不使用 MonoBehaviour / Coroutine / GameObject。
    /// </summary>
    public class FoldUIManager
    {
        /// <summary>纯文本/Hint 广播槽：由 FoldCore 注入 LabAPI 的广播实现。</summary>
        public static Action<string, float> HintSink;

        private readonly MonitorRenderer _renderer = new MonitorRenderer();
        private readonly object _gate = new object();
        private CancellationTokenSource _cts;

        /// <summary>播放指定模块的播放列表。</summary>
        public async Task Play(string module, List<PlaylistItem> items)
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                }
                _cts = new CancellationTokenSource();
                cts = _cts;
            }

            try
            {
                await PlayRoutineAsync(module, items, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Broadcast($"[Fold] 模块 {module} 播放被切换");
            }
        }

        /// <summary>停止当前播放并清理（含服务端扬声器）。</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_cts == null) return;
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            ActionExecutor.StopAll();
        }

        /// <summary>后台倒计时主循环：按时间轴顺序逐项执行（Task.Delay 实现）。</summary>
        private async Task PlayRoutineAsync(string module, List<PlaylistItem> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0)
            {
                Broadcast($"[Fold] 模块 {module} 时间轴为空");
                return;
            }

            float elapsed = 0f;
            int index = 0;
            var current = items[0];

            while (index < items.Count)
            {
                ct.ThrowIfCancellationRequested();

                // 触发所有到点事件
                while (index < items.Count && items[index].Time <= elapsed)
                {
                    current = items[index];
                    ExecuteAction(module, current);
                    index++;
                }
                if (index >= items.Count) break;

                // 前台渲染监控帧；后台缓冲预拼装下一帧
                string frame = _renderer.RenderFrame(module, current, items[index].Time - elapsed);
                _renderer.PrepareNextFrame(items[index]);
                Broadcast(frame);

                await Task.Delay(1000, ct);
                elapsed += 1f;
                _renderer.Swap();
            }

            Broadcast($"[Fold] 模块 {module} 播放结束");
        }

        /// <summary>执行单个动作：媒体动作走广播，游戏动作交给 ActionExecutor（LabAPI 原生 API）。</summary>
        private void ExecuteAction(string module, PlaylistItem item)
        {
            switch (item.Action.ToLowerInvariant())
            {
                case "clear":
                    Broadcast($"[Fold] {module} 画面已清除");
                    break;
                case "show_image":
                case "image":
                    PlayImage(item.Url);
                    break;
                case "play_video":
                    PlayVideo(item.Url);
                    break;
                case "play_audio":
                case "change_role":
                case "spawn_item":
                case "control_door":
                    try
                    {
                        ActionExecutor.Execute(module, item);
                    }
                    catch (Exception ex)
                    {
                        LabApi.Features.Console.Logger.Warn($"[Fold] {module}: {item.Action} 执行失败: {ex.Message}");
                    }
                    break;
                case "show_text":
                case "text":
                default: // 纯文本广播
                    Broadcast($"[Fold][{item.Time:F0}s] {item.Url}");
                    break;
            }
        }

        /// <summary>
        /// 向客户端广播 [FOLD_IMG] 标记：装有 FoldClient 的玩家拦截后全屏渲染真实图片，
        /// 未装模组的玩家看到的是标记文本（MVP 取舍）。
        /// 插件端不下载图片，只通过 FoldEdge 把 url 转成颜色矩阵。
        /// </summary>
        public void PlayImage(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            Broadcast("[FOLD_IMG]" + url);
            ShowImage(url);
        }

        /// <summary>向客户端广播 [FOLD_VIDEO] 标记：FoldClient 拦截后全屏播放 WebM/MP4。</summary>
        public void PlayVideo(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            Broadcast("[FOLD_VIDEO]" + url, 8f);
        }

        /// <summary>
        /// 图片渲染接口：向 FoldEdge 主程序发起转换请求（url → JSON 颜色矩阵），
        /// 主程序转换完成后通过 WebSocket 回推 image-frame，由 RenderImageFrame 渲染。
        /// 插件端不下载、不解码任何图片，只做纯渲染。
        /// </summary>
        public void ShowImage(string url)
        {
            FoldEdgeClient.Instance.RequestImage(url);
        }

        /// <summary>
        /// 渲染主程序下发的图片帧：颜色矩阵 → 富文本 → 广播。
        /// 长度控制：彩色帧超过 800 字符自动降级为灰阶帧；灰阶帧仍超限则放弃发送并报错。
        /// </summary>
        public void RenderImageFrame(int width, int height, IList<PixelData> pixels, float duration)
        {
            if (pixels == null || pixels.Count == 0) return;

            // 1) 彩色版（缩写颜色标签）
            string colored = FrameToText.BuildColored(pixels);
            ServerConsole.AddLog($"[Fold] 彩色帧长度: {colored.Length}");
            if (colored.Length <= 800)
            {
                Broadcast(colored, duration);
                return;
            }

            // 2) 超限 → 灰阶降级（只保轮廓）
            string gray = FrameToText.BuildGrayscale(pixels);
            ServerConsole.AddLog($"[Fold] 彩色帧超限(>{800})，降级灰阶帧长度: {gray.Length}");
            if (gray.Length > 800)
            {
                ServerConsole.AddLog($"[Fold] 灰阶帧仍超限({gray.Length})，放弃发送");
                Broadcast($"[Fold][ERROR] 图像帧超长被丢弃: {gray.Length}", 5f);
                return;
            }
            Broadcast(gray, duration);
        }

        /// <summary>纯文本广播：优先走 HintSink（LabAPI 广播接口）。</summary>
        private void Broadcast(string message, float duration = 1.5f)
        {
            if (HintSink != null)
            {
                HintSink(message, duration);
            }
        }
    }
}
