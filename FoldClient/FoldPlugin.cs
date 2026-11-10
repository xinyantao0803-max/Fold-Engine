using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace FoldClient
{
    /// <summary>
    /// FoldClient：SCP:SL 客户端插件（BepInEx 5 入口）。
    /// 与 FoldEngine 服务端插件配合：拦截带 [FOLD_IMG] 标记的文本并渲染真实图片。
    /// </summary>
    [BepInPlugin("fold.client", "FoldClient", "1.0.0")]
    public class FoldPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private GameObject _uiHost;

        // BepInEx 5 入口：Unity 魔术方法（非虚方法，勿加 override；基类的私有 Awake 仍会被 Unity 调用）
        private void Awake()
        {
            Logger.LogInfo("[FoldClient] 插件启动");

            // 安装 Harmony 补丁（拦截 TMP 文本中的 [FOLD_IMG] 标记）
            _harmony = new Harmony("fold.client");
            _harmony.PatchAll(typeof(FoldPlugin).Assembly);

            // 全屏图片渲染器宿主
            _uiHost = new GameObject("FoldUI");
            Object.DontDestroyOnLoad(_uiHost);
            _uiHost.AddComponent<FoldUIRenderer>();
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            if (_uiHost != null)
            {
                Object.Destroy(_uiHost);
                _uiHost = null;
            }
            Logger.LogInfo("[FoldClient] 插件卸载");
        }
    }
}
