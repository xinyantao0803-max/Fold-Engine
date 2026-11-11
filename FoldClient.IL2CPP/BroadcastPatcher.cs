// This plugin is strictly a passive UI renderer. It does not access game entity
// coordinates or gameplay data. All media and text are explicitly provided by the server.
using System.Text.Json;
using HarmonyLib;

namespace FoldClient.IL2CPP
{
    /// <summary>
    /// 拦截游戏底层广播 Broadcast.RpcAddElement(string data, ushort time, BroadcastFlags flags)。
    /// 只处理 Fold 媒体标记；先验签，签名或时间戳不合法就拒绝渲染并打印警告。
    /// 不读取任何玩家/实体坐标，不参与游戏逻辑。
    /// </summary>
    [HarmonyPatch(typeof(Broadcast), nameof(Broadcast.RpcAddElement))]
    public static class BroadcastPatcher
    {
        private const string VideoMarker = "[FOLD_VIDEO]";
        private const string ImageMarker = "[FOLD_IMG]";
        private const string AudioMarker = "[FOLD_AUDIO]";
        private const string TextMarker = "[FOLD_TEXT]";

        [HarmonyPrefix]
        public static bool Prefix(string data)
        {
            if (string.IsNullOrEmpty(data)) return true;

            if (TryHandle(data, VideoMarker, "video")) return false;
            if (TryHandle(data, ImageMarker, "image")) return false;
            if (TryHandle(data, AudioMarker, "audio")) return false;
            if (TryHandle(data, TextMarker, "text")) return false;

            return true;
        }

        private static bool TryHandle(string data, string marker, string kind)
        {
            if (!data.StartsWith(marker)) return false;

            var body = data.Substring(marker.Length);
            if (!MediaVerifier.TryVerify(kind, body, out var payload))
            {
                FoldPlugin.LogSource?.LogWarning(
                    "[FoldClient] Security Alert: Invalid Signature! 已丢弃该媒体指令");
                return true;
            }

            var overlay = FoldOverlay.Instance;
            if (overlay == null)
            {
                FoldPlugin.LogSource?.LogWarning("[FoldClient] 渲染器尚未就绪，忽略媒体指令");
                return true;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                switch (kind)
                {
                    case "video":
                        overlay.PlayVideo(GetString(root, "url"));
                        break;
                    case "image":
                        overlay.ShowImage(GetString(root, "url"));
                        break;
                    case "audio":
                        overlay.PlayAudio(GetString(root, "url"), GetFloat(root, "volume", 1f));
                        break;
                    case "text":
                        overlay.ShowSubtitle(GetString(root, "text"), GetFloat(root, "duration", 5f));
                        break;
                }
            }
            catch (JsonException ex)
            {
                FoldPlugin.LogSource?.LogWarning($"[FoldClient] 媒体负载解析失败: {ex.Message}");
            }

            return true;
        }

        private static string GetString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }

        private static float GetFloat(JsonElement root, string name, float fallback)
        {
            return root.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed)
                ? (float)parsed
                : fallback;
        }
    }
}
