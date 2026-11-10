using System;
using System.Collections.Generic;
using System.Text;
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
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        /// <summary>当前 UI 管理器实例（供 fold 指令调用）。</summary>
        internal static FoldUIManager UI { get; private set; }

        private readonly FoldCommand _foldCommand = new FoldCommand();

        public override void Enable()
        {
            Logger.Info("[Fold] 引擎加载成功");

            // 注入纯文本广播实现：服务端全局文字广播（网络包下发，不创建任何游戏对象）
            FoldUIManager.HintSink = (message, duration) =>
            {
                Server.SendBroadcast(message, (ushort)Math.Ceiling(duration), Broadcast.BroadcastFlags.Normal, false);
                Logger.Info($"[Fold] {message}");
            };

            UI = new FoldUIManager();

            // 连接 FoldEdge 主程序：插件端只接收数据、纯渲染
            FoldEdgeClient.Instance.ImageFrameReceived += OnImageFrame;
            FoldEdgeClient.Instance.Start();

            // 注册名为 "fold" 的游戏控制台指令
            Server.GameConsoleCommandHandler.RegisterCommand(_foldCommand);
        }

        public override void Disable()
        {
            // 清理资源：断开主程序连接、停止播放、注销指令
            FoldEdgeClient.Instance.ImageFrameReceived -= OnImageFrame;
            FoldEdgeClient.Instance.Stop();
            UI?.Stop();
            UI = null;
            Server.GameConsoleCommandHandler.UnregisterCommand(_foldCommand);

            Logger.Info("[Fold] 引擎已卸载");
        }

        /// <summary>接收主程序下发的图片帧并渲染。</summary>
        private void OnImageFrame(ImageFrameData frame)
        {
            UI?.RenderImageFrame(frame.Width, frame.Height, frame.Pixels, frame.Duration);
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

    /// <summary>fold 控制台指令：执行一次内置测试播放。</summary>
    public class FoldCommand : ICommand
    {
        public string Command => "fold";
        public string[] Aliases => new string[0];
        public string Description => "Run a Fold Engine test playlist.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            var ui = FoldCore.UI;
            if (ui == null)
            {
                response = "[Fold] 引擎未加载";
                return false;
            }

            var items = new List<PlaylistItem>
            {
                new PlaylistItem { Time = 0f, Action = "text",  Url = "Fold Engine 测试播放启动" },
                new PlaylistItem { Time = 2f, Action = "image", Url = "smiley" },
                new PlaylistItem { Time = 5f, Action = "clear", Url = string.Empty }
            };
            _ = ui.Play("demo", items);

            // 模拟读取 .fdll manifest.json 并应用合规标签（规范 v1.0.0）
            const string demoManifest =
                "{\"id\":\"com.fold.demo\",\"name\":\"demo\",\"version\":\"1.0.0\",\"author\":\"Fold\"," +
                "\"engineVersion\":\">=1.0.0\",\"description\":\"Fold demo module\"," +
                "\"tags\":[\"18+\",\"GORE\",\"INTENSE\"]}";
            FoldCore.ApplyComplianceTags(PlaylistTransport.ParseTags(demoManifest));

            response = "[Fold] 测试播放已开始: demo";
            return true;
        }
    }
}
