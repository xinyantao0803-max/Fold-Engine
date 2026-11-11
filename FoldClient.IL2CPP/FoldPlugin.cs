// Strict Security Boundary: This client plugin ONLY reports media playback states
// (current time, status). It strictly has ZERO access to game memory, player
// positions, entity states, or gameplay logic. It cannot be abused for ESP or Radar hacks.
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace FoldClient.IL2CPP
{
    /// <summary>
    /// FoldClient 的 IL2CPP 入口（BepInEx 6 / Unity 6）。
    /// 职责：注入全屏渲染组件 + 安装广播拦截补丁。
    /// 不修改游戏文件，不触碰反作弊，完全是标准 BepInEx 插件。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class FoldPlugin : BasePlugin
    {
        public const string PluginGuid = "fold.client.il2cpp";
        public const string PluginName = "FoldClient (IL2CPP)";
        public const string PluginVersion = "2.0.0";

        internal static ManualLogSource LogSource;

        private Harmony _harmony;

        public override void Load()
        {
            LogSource = Log;

            // 注入托管 MonoBehaviour：必须先注册类型，AddComponent 才能找到 IL2CPP 类
            ClassInjector.RegisterTypeInIl2Cpp<FoldOverlay>();
            var overlay = AddComponent<FoldOverlay>();
            Object.DontDestroyOnLoad(overlay.gameObject);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(FoldPlugin).Assembly);

            Log.LogInfo("[FoldClient] IL2CPP 插件已加载，等待 [FOLD_IMG] / [FOLD_VIDEO] 广播");
        }

        public override bool Unload()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            Log.LogInfo("[FoldClient] 插件已卸载");
            return true;
        }
    }
}
