using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FoldClient.IL2CPP
{
    /// <summary>
    /// 播放状态遥测：POST 到 FoldEdge /api/telemetry，HMAC-SHA256 签名。
    /// 只上报 mediaType / url / currentTime / status，不包含任何游戏数据。
    /// 注意：客户端与 FoldEdge 同一台机器时使用 127.0.0.1；远程客户端需自行改 Endpoint。
    /// </summary>
    internal static class TelemetryReporter
    {
        // 与 FoldEdge/TelemetryVerifier.cs 保持一致
        private const string Secret = "Fold-Telemetry-HMAC-Key-2026!";
        private const string Endpoint = "http://127.0.0.1:9000/api/telemetry";

        private static readonly HttpClient Http = new HttpClient();

        public static void Report(string status, string mediaType, string url, float currentTime)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string time = currentTime.ToString("F3", CultureInfo.InvariantCulture);
            string message = $"{status}\n{mediaType}\n{url}\n{time}\n{timestamp}";

            string signature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret)))
            {
                signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)))
                    .ToLowerInvariant();
            }

            var json = JsonSerializer.Serialize(new
            {
                status,
                mediaType,
                url,
                currentTime,
                timestamp,
                signature
            });

            _ = PostAsync(json);
        }

        private static async Task PostAsync(string json)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await Http.PostAsync(Endpoint, content).ConfigureAwait(false);
            }
            catch
            {
                // FoldEdge 不在本机或未启动时忽略，不影响播放
            }
        }
    }
}
