namespace FoldClient
{
    /// <summary>
    /// FVP 的唯一配置入口。验签公钥由 FH（FoldEdge）在 WebSocket 握手时动态下发，
    /// 客户端不再硬编码公钥，主程序换钥后无需重新编译。
    /// </summary>
    public static class FvpConfig
    {
        /// <summary>指令最大时间偏差（毫秒）。</summary>
        public const int MaxAgeMilliseconds = 3000;

        /// <summary>强制 DXVA：为 true 时，硬件解码不可用将拒绝播放而不是回退软件解码。</summary>
        public static bool RequireHardwareDecoding = true;

        /// <summary>true = 保持比例留黑边；false = 拉伸铺满全屏。</summary>
        public static bool KeepAspectRatio = true;

        /// <summary>图片自动隐藏时间（秒）。</summary>
        public const float ImageHoldSeconds = 8f;

        /// <summary>本地家长控制：开启后拒绝带受限标签的媒体。</summary>
        public static bool ParentalControlEnabled = true;

        /// <summary>受限制标签（命中任意一个即拒绝渲染）。</summary>
        public static string[] BlockedTags = { "18+", "GORE" };

        /// <summary>FoldEdge WebSocket 地址，可由 --edge 覆盖。</summary>
        public static string EdgeUrl = "ws://127.0.0.1:9000/ws";

        /// <summary>载荷中的 tags 是否被本地家长控制拦截。</summary>
        public static bool Allows(string payloadJson)
        {
            if (!ParentalControlEnabled || string.IsNullOrEmpty(payloadJson))
                return true;

            int marker = payloadJson.IndexOf("\"tags\"", System.StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return true;

            int start = payloadJson.IndexOf('[', marker);
            int end = start < 0 ? -1 : payloadJson.IndexOf(']', start);
            if (start < 0 || end <= start)
                return true;

            string tags = payloadJson.Substring(start, end - start + 1);
            foreach (string blocked in BlockedTags)
            {
                if (tags.IndexOf("\"" + blocked + "\"", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            return true;
        }
    }
}
