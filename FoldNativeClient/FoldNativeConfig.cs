using System;

namespace FoldNative
{
    /// <summary>
    /// Native integration configuration.
    ///
    /// This file is part of the native Unity module that Northwood Studios can
    /// copy directly into the official client source code. It has no dependency
    /// on WPF, external launchers, injected DLLs, or BepInEx.
    /// </summary>
    public static class FoldNativeConfig
    {
        /// <summary>Maximum accepted clock skew for a signed command (milliseconds).</summary>
        public const int MaxAgeMilliseconds = 3000;

        /// <summary>Local parental control. Disabled only by an explicit local decision.</summary>
        public static bool ParentalControlEnabled = true;

        /// <summary>Content tags rejected before any rendering happens.</summary>
        public static string[] BlockedTags = { "18+", "GORE" };

        /// <summary>
        /// Returns false when the signed payload carries a blocked content tag.
        /// This is a local policy check, separate from signature verification.
        /// </summary>
        public static bool Allows(string payloadJson)
        {
            if (!ParentalControlEnabled || string.IsNullOrEmpty(payloadJson))
                return true;

            int marker = payloadJson.IndexOf("\"tags\"", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return true;

            int start = payloadJson.IndexOf('[', marker);
            int end = start < 0 ? -1 : payloadJson.IndexOf(']', start);
            if (start < 0 || end <= start)
                return true;

            string tags = payloadJson.Substring(start, end - start + 1);
            foreach (string blocked in BlockedTags)
            {
                if (tags.IndexOf("\"" + blocked + "\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            return true;
        }
    }
}
