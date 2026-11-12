using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using CommandSystem;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;

namespace FoldEngine
{
    /// <summary>
    /// Fold 引擎插件入口：严格按 LabAPI 1.1.7 官方 Plugin 基类规范实现。
    /// 服务端环境无 Unity 引擎，不使用任何 MonoBehaviour/GameObject。
    /// </summary>
    public class FoldCore : Plugin
    {
        public override string Name => "FoldEngine";
        public override string Description => "The Next-Gen Media & Module Framework for SCP:SL.";
        public override string Author => "Fold Team";
        public override Version Version => new Version(1, 4, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        /// <summary>当前 UI 管理器实例（供 fold 指令调用）。</summary>
        internal static FoldUIManager UI { get; private set; }

        private readonly FoldCommand _foldCommand = new FoldCommand();

        // ---- 主线程调度：WS 接收线程只做校验/解析，游戏 API 统一回主线程执行 ----
        private static SynchronizationContext _mainThread;
        private static readonly ConcurrentQueue<Action> MainQueue =
            new ConcurrentQueue<Action>();
        private static int _queued;
        private static int _drainScheduled;
        private const int MaxQueued = 64;

        /// <summary>
        /// 把游戏内动作切回主线程；队列超过上限时丢弃最旧指令（熔断）。
        /// </summary>
        public static void RunOnMain(Action action)
        {
            if (action == null) return;

            if (Interlocked.Increment(ref _queued) > MaxQueued)
            {
                if (MainQueue.TryDequeue(out _))
                    Interlocked.Decrement(ref _queued);

                Logger.Warn("[Fold] 指令队列过载，已丢弃最旧指令");
            }

            MainQueue.Enqueue(action);
            ScheduleDrain();
        }

        private static void ScheduleDrain()
        {
            if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
                return;

            var context = _mainThread;
            if (context == null)
            {
                DrainMainQueue();
                Interlocked.Exchange(ref _drainScheduled, 0);
                return;
            }

            try
            {
                context.Post(_ =>
                {
                    Interlocked.Exchange(ref _drainScheduled, 0);
                    DrainMainQueue();
                }, null);
            }
            catch
            {
                Interlocked.Exchange(ref _drainScheduled, 0);
                DrainMainQueue();
            }
        }

        private static void DrainMainQueue()
        {
            while (MainQueue.TryDequeue(out Action action))
            {
                Interlocked.Decrement(ref _queued);
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Fold] 主线程指令执行失败: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public override void Enable()
        {
            _mainThread = SynchronizationContext.Current;
            if (_mainThread == null)
                Logger.Warn("[Fold] 主线程 SynchronizationContext 不可用，动作将就地执行");

            Logger.Info("[Fold] 引擎加载成功");

            // 注入纯文本广播实现：服务端全局文字广播（网络包下发，不创建任何游戏对象）
            FoldUIManager.HintSink = (message, duration) =>
            {
                Server.SendBroadcast(message, (ushort)Math.Ceiling(duration), Broadcast.BroadcastFlags.Normal, false);
                Logger.Info($"[Fold] {message}");
            };

            UI = new FoldUIManager();

            // 连接 FoldEdge 主程序：插件端只接收指令，媒体全部交给客户端渲染
            FoldEdgeClient.Instance.Start();

            // 注册名为 "fold" 的游戏控制台指令
            Server.GameConsoleCommandHandler.RegisterCommand(_foldCommand);
        }

        public override void Disable()
        {
            // 清理资源：断开主程序连接、停止播放、注销指令
            FoldEdgeClient.Instance.Stop();
            UI?.Stop();
            UI = null;

            _mainThread = null;
            while (MainQueue.TryDequeue(out _))
            {
            }
            Interlocked.Exchange(ref _queued, 0);

            Server.GameConsoleCommandHandler.UnregisterCommand(_foldCommand);

            Logger.Info("[Fold] 引擎已卸载");
        }

        /// <summary>
        /// 合规标签 → 服务器名警告前缀（仅用于告知知情权，不做审查）。
        /// 含 18+ / GORE 时强制注入 [18+]；FLASH / POLITICS 按规范注入对应前缀。
        /// </summary>
        public static void ApplyComplianceTags(List<string> tags)
        {
            if (tags == null || tags.Count == 0) return;

            var prefix = new StringBuilder();
            foreach (var tag in tags)
            {
                switch (tag.Trim().ToUpperInvariant())
                {
                    case "18+":
                    case "GORE":
                        if (!ContainsPrefix(prefix, "[18+]")) prefix.Append("[18+] ");
                        break;
                    case "FLASH":
                        if (!ContainsPrefix(prefix, "[FLASH]")) prefix.Append("[FLASH] ");
                        break;
                    case "POLITICS":
                        if (!ContainsPrefix(prefix, "[POLITICS]")) prefix.Append("[POLITICS] ");
                        break;
                }
            }
            if (prefix.Length == 0) return;

            string prefixStr = prefix.ToString();
            string current = Server.ServerListName ?? string.Empty;
            if (current.StartsWith(prefixStr)) return; // 已注入过，避免重复
            Server.ServerListName = prefixStr + current;
            Logger.Info($"[Fold] 合规标签生效，服务器名已加前缀: {prefixStr.Trim()}");
        }

        private static bool ContainsPrefix(StringBuilder sb, string prefix)
        {
            return sb.ToString().Contains(prefix);
        }
    }

    /// <summary>
    /// fold 控制台指令。
    /// 用法：
    ///   fold                    内置文本测试播放
    ///   fold image &lt;url&gt;       全屏图片（客户端渲染，FoldEdge 签名）
    ///   fold video &lt;url&gt;       全屏视频（mp4 / webm）
    ///   fold audio &lt;url&gt; [vol] 全屏 2D 音频（ogg / mp3，音量 0-1）
    ///   fold text &lt;text&gt;       底部字幕
    ///   fold module &lt;name&gt;     让 FoldEdge 读取 Modules/&lt;name&gt;.fdll 并播放
    ///   fold tags &lt;18+,GORE&gt;   手动注入合规标签前缀（验证注入逻辑）
    /// </summary>
    public class FoldCommand : ICommand
    {
        public string Command => "fold";
        public string[] Aliases => new string[0];
        public string Description => "Run a Fold Engine test playlist (fold [image|video|module|tags]).";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            try
            {
                return ExecuteCore(arguments, sender, out response);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Fold] 指令执行异常: {ex.GetType().Name}: {ex.Message}");
                response = "[Fold] 指令执行失败";
                return false;
            }
        }

        private bool ExecuteCore(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            string senderKey = sender?.LogName;
            bool isConsole = string.IsNullOrEmpty(senderKey) ||
                             senderKey.IndexOf("CONSOLE", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isConsole && !RateLimiter.TryAcquire(senderKey))
            {
                response = "[Fold] 请求过于频繁，请稍后再试";
                return false;
            }

            var ui = FoldCore.UI;
            if (ui == null)
            {
                response = "[Fold] 引擎未加载";
                return false;
            }

            string sub = (GetArg(arguments, 0) ?? string.Empty).ToLowerInvariant();
            switch (sub)
            {
                case "":
                case "demo":
                case "play":
                    _ = ui.Play("demo", new List<PlaylistItem>
                    {
                        new PlaylistItem { Time = 0f, Action = "text",  Url = "Fold Engine 测试播放启动" },
                        new PlaylistItem { Time = 2f, Action = "clear", Url = string.Empty }
                    });
                    response = "[Fold] 测试播放已开始: demo";
                    return true;

                case "image":
                    var imageUrl = GetArg(arguments, 1);
                    if (string.IsNullOrEmpty(imageUrl))
                    {
                        response = "用法: fold image <url>";
                        return false;
                    }
                    if (imageUrl.Length > 2048)
                    {
                        response = "[Fold] URL 过长";
                        return false;
                    }

                    FoldEdgeClient.Instance.RequestMedia("image", url: imageUrl);
                    response = $"[Fold] 已请求 FoldEdge 签名并下发图片: {imageUrl}";
                    return true;
                case "video":
                    var videoUrl = GetArg(arguments, 1);
                    if (string.IsNullOrEmpty(videoUrl))
                    {
                        response = "用法: fold video <url>";
                        return false;
                    }
                    if (videoUrl.Length > 2048)
                    {
                        response = "[Fold] URL 过长";
                        return false;
                    }

                    FoldEdgeClient.Instance.RequestMedia("video", url: videoUrl);
                    response = $"[Fold] 已请求 FoldEdge 签名并下发视频: {videoUrl}";
                    return true;

                case "audio":
                    var audioUrl = GetArg(arguments, 1);
                    if (string.IsNullOrEmpty(audioUrl))
                    {
                        response = "用法: fold audio <url> [volume 0-1]";
                        return false;
                    }
                    if (audioUrl.Length > 2048)
                    {
                        response = "[Fold] URL 过长";
                        return false;
                    }

                    float volume = 1f;
                    var volumeText = GetArg(arguments, 2);
                    if (!string.IsNullOrEmpty(volumeText))
                    {
                        float.TryParse(volumeText, NumberStyles.Float, CultureInfo.InvariantCulture, out volume);
                    }
                    FoldEdgeClient.Instance.RequestMedia("audio", url: audioUrl, volume: volume);
                    response = $"[Fold] 已请求 FoldEdge 签名并下发音频: {audioUrl}";
                    return true;

                case "text":
                    var subtitle = JoinArgs(arguments, 1);
                    if (string.IsNullOrEmpty(subtitle))
                    {
                        response = "用法: fold text <字幕内容>";
                        return false;
                    }
                    if (subtitle.Length > 2048)
                    {
                        response = "[Fold] 字幕过长";
                        return false;
                    }

                    FoldEdgeClient.Instance.RequestMedia("text", text: subtitle);
                    response = "[Fold] 已请求 FoldEdge 签名并下发字幕";
                    return true;

                case "module":
                    var moduleName = GetArg(arguments, 1);
                    if (string.IsNullOrEmpty(moduleName))
                    {
                        response = "用法: fold module <name>（对应 FoldEdge/Modules/<name>.fdll）";
                        return false;
                    }
                    if (!IsSafeModuleName(moduleName))
                    {
                        response = "[Fold] 非法模块名";
                        return false;
                    }

                    FoldEdgeClient.Instance.TriggerModule(moduleName);
                    response = $"[Fold] 已请求主程序加载模块: {moduleName}";
                    return true;

                case "tags":
                    var raw = GetArg(arguments, 1);
                    if (string.IsNullOrEmpty(raw))
                    {
                        response = "用法: fold tags <tag1,tag2>（例如 18+,GORE）";
                        return false;
                    }
                    var tags = new List<string>();
                    foreach (var t in raw.Split(','))
                    {
                        if (t.Trim().Length > 0) tags.Add(t.Trim());
                    }
                    FoldCore.ApplyComplianceTags(tags);
                    response = $"[Fold] 合规标签已处理: {string.Join(",", tags)}";
                    return true;

                default:
                    response = "用法: fold [demo|image <url>|video <url>|audio <url> [vol]|text <字幕>|module <name>|tags <a,b>]";
                    return false;
            }
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

        private static string GetArg(ArraySegment<string> arguments, int index)
        {
            if (arguments.Array == null || index < 0 || index >= arguments.Count) return null;
            return arguments.Array[arguments.Offset + index];
        }

        private static string JoinArgs(ArraySegment<string> arguments, int start)
        {
            if (arguments.Array == null || start >= arguments.Count) return null;
            var parts = new List<string>();
            for (int i = start; i < arguments.Count; i++)
            {
                parts.Add(arguments.Array[arguments.Offset + i]);
            }
            return string.Join(" ", parts);
        }
    }
}
