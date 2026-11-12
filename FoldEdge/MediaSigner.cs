using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoldEdge
{
    /// <summary>
    /// 媒体指令签名器（非对称）：ECDSA P-256 + SHA-256。
    /// 私钥只保存在 FoldEdge 的 keys/ 目录，公钥硬编码在原生客户端。
    /// 签名内容：kind + "\n" + payloadJson + "\n" + timestampMs + "\n" + nonce。
    /// FoldEngine 不持有任何密钥，只原样透传签名标记。
    /// </summary>
    internal static class MediaSigner
    {
        private static readonly string KeyDirectory =
            Path.Combine(Directory.GetCurrentDirectory(), "keys");

        private static readonly string PrivateKeyPath =
            Path.Combine(KeyDirectory, "fold-ecdsa-private.pem");

        private static readonly string PublicKeyPath =
            Path.Combine(KeyDirectory, "fold-ecdsa-public.pem");

        private static readonly ECDsa SigningKey = LoadOrCreateKey();

        private static readonly JsonSerializerOptions PayloadJson = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>当前公钥（SPKI Base64），通过 WebSocket 动态下发给客户端。</summary>
        public static string PublicKeyBase64 =>
            Convert.ToBase64String(SigningKey.ExportSubjectPublicKeyInfo());

        /// <summary>当前公钥（SPKI PEM），便于人工核对。</summary>
        public static string PublicKeyPem => SigningKey.ExportSubjectPublicKeyInfoPem();

        /// <summary>启动时调用，确保密钥已生成并输出公钥。</summary>
        public static void Initialize()
        {
            _ = SigningKey;
        }

        private static ECDsa LoadOrCreateKey()
        {
            Directory.CreateDirectory(KeyDirectory);

            if (File.Exists(PrivateKeyPath))
            {
                var existing = ECDsa.Create();
                existing.ImportFromPem(File.ReadAllText(PrivateKeyPath));
                File.WriteAllText(PublicKeyPath, existing.ExportSubjectPublicKeyInfoPem());
                return existing;
            }

            var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(PrivateKeyPath, created.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(PublicKeyPath, created.ExportSubjectPublicKeyInfoPem());
            return created;
        }

        private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string CreateNonce() => Guid.NewGuid().ToString("N");

        public static string Sign(string kind, string payloadJson, long timestampMs, string nonce)
        {
            var data = Encoding.UTF8.GetBytes(
                kind + "\n" + payloadJson + "\n" + timestampMs + "\n" + nonce);
            var signature = SigningKey.SignData(
                data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return Base64UrlEncode(signature);
        }

        /// <summary>校验 .fdll 的分离签名（Base64 DER，ECDSA P-256 + SHA-256）。</summary>
        public static bool VerifyDetached(byte[] data, byte[] signature)
        {
            return SigningKey.VerifyData(
                data, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }

        /// <summary>生成 [FOLD_XXX]&lt;tsMs&gt;|&lt;nonce&gt;|&lt;sig&gt;|&lt;payloadJson&gt; 标记。</summary>
        public static string BuildMarker(string prefix, string kind, string payloadJson)
        {
            long ts = NowMilliseconds();
            string nonce = CreateNonce();
            return prefix + ts + "|" + nonce + "|" + Sign(kind, payloadJson, ts, nonce) +
                   "|" + payloadJson;
        }

        /// <summary>
        /// 给 timeline 中的媒体条目补齐 ts / nonce / sig / payload。
        /// FoldEngine 不计算任何签名，只把这些字段原样拼进广播。
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

            long ts = NowMilliseconds();
            string nonce = CreateNonce();
            entry["ts"] = ts;
            entry["nonce"] = nonce;
            entry["sig"] = Sign(kind, payload, ts, nonce);
            entry["payload"] = payload;
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
