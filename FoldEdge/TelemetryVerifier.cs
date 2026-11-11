using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FoldEdge
{
    /// <summary>
    /// 遥测 HMAC 校验：客户端与 FoldEdge 共享预设密钥。
    /// 注意：这是完整性校验（防随意伪造），不是客户端身份证明；
    /// 共享密钥随客户端分发，无法阻止被篡改的客户端伪造遥测。
    /// </summary>
    internal static class TelemetryVerifier
    {
        private const string Secret = "Fold-Telemetry-HMAC-Key-2026!";
        private const int MaxAgeSeconds = 10;

        public static bool Verify(string status, string mediaType, string url,
            double currentTime, long timestamp, string signature)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - timestamp) > MaxAgeSeconds) return false;

            var time = currentTime.ToString("F3", CultureInfo.InvariantCulture);
            var message = Encoding.UTF8.GetBytes(
                $"{status}\n{mediaType}\n{url}\n{time}\n{timestamp}");
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            var expected = Convert.ToHexString(hmac.ComputeHash(message)).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(signature));
        }
    }
}
