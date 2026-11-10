using HarmonyLib;
using TMPro;

namespace FoldClient
{
    /// <summary>
    /// 拦截所有 TMP 文本赋值（hint 与广播最终都渲染为 TMP 文本）：
    /// [FOLD_IMG] → 全屏图片；[FOLD_VIDEO] → 全屏视频。命中后抹掉原始标记文本。
    /// </summary>
    [HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.text), MethodType.Setter)]
    public static class HintPatcher
    {
        public const string ImageMarker = "[FOLD_IMG]";
        public const string VideoMarker = "[FOLD_VIDEO]";

        [HarmonyPrefix]
        public static void Prefix(ref string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            string url;
            bool isImage;
            if (value.StartsWith(ImageMarker))
            {
                isImage = true;
                url = value.Substring(ImageMarker.Length).Trim();
            }
            else if (value.StartsWith(VideoMarker))
            {
                isImage = false;
                url = value.Substring(VideoMarker.Length).Trim();
            }
            else
            {
                return;
            }

            if (url.Length == 0) return;

            var renderer = FoldUIRenderer.Instance;
            if (renderer == null) return;

            if (isImage) renderer.ShowImage(url);
            else renderer.PlayVideo(url);

            value = string.Empty; // 抹掉原始标记文本，避免客户端看到裸 URL
        }
    }
}
