using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FoldClient.IL2CPP
{
    /// <summary>
    /// 媒体标记验签器：HMAC-SHA256(kind + "\n" + timestamp + "\n" + payloadJson)。
    /// 标记格式：[FOLD_XXX]&lt;unix秒&gt;|&lt;64位hex签名&gt;|&lt;payloadJson&gt;
    /// 只接受 5 秒内的标记，降低重放风险。
    /// </summary>
    internal static class MediaVerifier
    {
        // 与 FoldEdge/MediaSigner.cs 保持一致
        private const string Secret = "Fold-Media-HMAC-Key-2026!";
        private const int MaxAgeSeconds = 5;

        public static bool TryVerify(string kind, string body, out string payload)
        {
            payload = null;

            int first = body.IndexOf('|');
            if (first <= 0) return false;
            int second = body.IndexOf('|', first + 1);
            if (second <= first + 1) return false;

            var timestampText = body.Substring(0, first);
            var signature = body.Substring(first + 1, second - first - 1);
            payload = body.Substring(second + 1);

            if (!long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var timestamp))
            {
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - timestamp) > MaxAgeSeconds) return false;

            var message = Encoding.UTF8.GetBytes(kind + "\n" + timestamp + "\n" + payload);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            var expected = Convert.ToHexString(hmac.ComputeHash(message)).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(signature));
        }
    }
}
