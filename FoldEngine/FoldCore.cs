using System;
using System.Collections.Generic;
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

            response = "[Fold] 测试播放已开始: demo";
            return true;
        }
    }
}
