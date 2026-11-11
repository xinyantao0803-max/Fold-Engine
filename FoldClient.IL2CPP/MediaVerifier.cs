using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FoldClient.IL2CPP
{
    /// <summary>
    /// ECDSA P-256 验签器（非对称）。
    /// 标记格式：[FOLD_XXX]&lt;unix毫秒&gt;|&lt;nonce&gt;|&lt;base64url签名&gt;|&lt;payloadJson&gt;
    /// 签名内容：kind + "\n" + payloadJson + "\n" + timestampMs + "\n" + nonce。
    /// 只接受 3 秒内且 nonce 未使用过的指令，拒绝伪造与重放。
    /// </summary>
    internal static class MediaVerifier
    {
        // 与 FoldEdge/keys/fold-ecdsa-public.pem 对应（SubjectPublicKeyInfo DER 的 Base64）；
        // 私钥只保存在 FoldEdge 侧。这里用 DER 而不是 PEM，避免换行/编码差异。
        private const string PublicKeyBase64 =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEy5YIc6FCHRLkii4hTmYAl7Cj+WFY" +
            "I9TWIOXhZP6cUWA01/IuWaKH16fFFfQk/uv4XE7OrzlAxDtYD5ZPFITVtg==";

        private const int MaxAgeMilliseconds = 3000;
        private const int NonceCacheLimit = 2048;

        private static readonly ECDsa VerificationKey = CreateKey();
        private static readonly ConcurrentDictionary<string, long> SeenNonces =
            new ConcurrentDictionary<string, long>();
        private static readonly object VerifyGate = new object();

        private static ECDsa CreateKey()
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
            return key;
        }

        public static bool TryVerify(string kind, string body, out string payload)
        {
            payload = null;

            int first = body.IndexOf('|');
            if (first <= 0) return false;
            int second = body.IndexOf('|', first + 1);
            if (second <= first + 1) return false;
            int third = body.IndexOf('|', second + 1);
            if (third <= second + 1) return false;

            var timestampText = body.Substring(0, first);
            var nonce = body.Substring(first + 1, second - first - 1);
            var signatureText = body.Substring(second + 1, third - second - 1);
            payload = body.Substring(third + 1);

            if (!long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var timestamp))
            {
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Math.Abs(now - timestamp) > MaxAgeMilliseconds) return false;

            // 防重放：同一个 nonce 只接受一次
            if (!SeenNonces.TryAdd(nonce, timestamp)) return false;
            PruneNonces(now);

            byte[] signature;
            try
            {
                signature = Base64UrlDecode(signatureText);
            }
            catch (FormatException)
            {
                return false;
            }

            var message = Encoding.UTF8.GetBytes(
                kind + "\n" + payload + "\n" + timestamp + "\n" + nonce);

            lock (VerifyGate)
            {
                return VerificationKey.VerifyData(
                    message, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
            }
        }

        private static void PruneNonces(long now)
        {
            if (SeenNonces.Count <= NonceCacheLimit) return;
            foreach (var pair in SeenNonces)
            {
                if (now - pair.Value > MaxAgeMilliseconds * 4)
                {
                    SeenNonces.TryRemove(pair.Key, out _);
                }
            }
        }

        private static byte[] Base64UrlDecode(string value)
        {
            var text = value.Replace('-', '+').Replace('_', '/');
            switch (text.Length % 4)
            {
                case 2: text += "=="; break;
                case 3: text += "="; break;
            }
            return Convert.FromBase64String(text);
        }
    }
}
