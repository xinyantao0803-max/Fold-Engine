using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoldEdge
{
    /// <summary>
    /// 媒体指令签名器：HMAC-SHA256(kind + "\n" + timestamp + "\n" + payloadJson)。
    /// 签名在 FoldEdge 生成，FoldEngine 只原样透传，FoldClient 负责验签。
    /// </summary>
    internal static class MediaSigner
    {
        private const string Secret = "Fold-Media-HMAC-Key-2026!";
        private const int MaxAgeSeconds = 5;

        private static readonly JsonSerializerOptions PayloadJson = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static string Sign(string kind, long timestamp, string payloadJson)
        {
            var message = Encoding.UTF8.GetBytes(kind + "\n" + timestamp + "\n" + payloadJson);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            return Convert.ToHexString(hmac.ComputeHash(message)).ToLowerInvariant();
        }

        /// <summary>生成 [FOLD_XXX]&lt;ts&gt;|&lt;sig&gt;|&lt;payloadJson&gt; 标记。</summary>
        public static string BuildMarker(string prefix, string kind, string payloadJson)
        {
            long ts = Now();
            return prefix + ts + "|" + Sign(kind, ts, payloadJson) + "|" + payloadJson;
        }

        /// <summary>
        /// 给 timeline 中的媒体条目补齐 ts / sig / payload 字段。
        /// FoldEngine 不做任何计算，只把这些字段原样拼进广播。
        /// </summary>
        public static string SignTimeline(string timelineJson)
        {
            if (string.IsNullOrWhiteSpace(timelineJson)) return timelineJson;

            try
            {
                var root = JsonNode.Parse(timelineJson);
                if (root is JsonArray array)
                {
                    foreach (var entry in array) SignEntry(entry);
                }
                else if (root is JsonObject obj && obj["entries"] is JsonArray entries)
                {
                    foreach (var entry in entries) SignEntry(entry);
                }
                else
                {
                    return timelineJson;
                }
                return root.ToJsonString(PayloadJson);
            }
            catch (JsonException)
            {
                // 时间轴不是合法 JSON 时保持原样，由 FoldEngine 侧报错
                return timelineJson;
            }
        }

        private static void SignEntry(JsonNode? node)
        {
            if (node is not JsonObject entry) return;

            string action = entry["action"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? string.Empty;
            string url = entry["url"]?.GetValue<string>() ?? string.Empty;
            if (url.Length == 0) url = entry["text"]?.GetValue<string>() ?? string.Empty;

            string kind;
            string payload;
            switch (action)
            {
                case "PLAY_VIDEO":
                    kind = "video";
                    payload = JsonSerializer.Serialize(new { url }, PayloadJson);
                    break;
                case "SHOW_IMAGE":
                    kind = "image";
                    payload = JsonSerializer.Serialize(new { url }, PayloadJson);
                    break;
                case "PLAY_AUDIO":
                    kind = "audio";
                    double volume = entry["volume"]?.GetValue<double>() ?? 1.0;
                    payload = JsonSerializer.Serialize(new { url, volume }, PayloadJson);
                    break;
                case "SHOW_SUBTITLE":
                case "SUBTITLE":
                    kind = "text";
                    double duration = entry["duration"]?.GetValue<double>() ?? 5.0;
                    payload = JsonSerializer.Serialize(new { text = url, duration }, PayloadJson);
                    break;
                default:
                    return;
            }

            long ts = Now();
            entry["ts"] = ts;
            entry["sig"] = Sign(kind, ts, payload);
            entry["payload"] = payload;
        }

        /// <summary>客户端允许的最大时间偏差（秒），与 FoldClient 保持一致。</summary>
        public static int TimestampTolerance => MaxAgeSeconds;
    }
}
