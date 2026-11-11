using System;
using UnityEngine;

namespace FoldNative
{
    /// <summary>
    /// Native Unity command receive layer.
    ///
    /// This is a native Unity integration module. NW can copy these scripts
    /// directly into their client source code. It does not require any external
    /// launchers.
    ///
    /// Every executable command is verified here before any UI or media API is
    /// touched. The caller should be the Mirror packet handler (Unity main thread);
    /// the actual render call is queued through FoldNativeUI so it is always safe.
    /// </summary>
    internal sealed class FoldCommandReceiver
    {
        private const int MaxMarkerLength = 4096;

        private readonly FoldNativeUI _ui;

        public FoldCommandReceiver(FoldNativeUI ui)
        {
            _ui = ui;
        }

        public bool Receive(string marker)
        {
            if (string.IsNullOrEmpty(marker) || marker.Length > MaxMarkerLength)
            {
                Debug.LogWarning("[FoldNative] rejected: empty or oversized marker");
                return false;
            }

            int end = marker.IndexOf(']');
            if (end <= 0 || end > 32)
            {
                Debug.LogWarning("[FoldNative] rejected: malformed marker");
                return false;
            }

            string prefix = marker.Substring(0, end + 1);
            string body = marker.Substring(end + 1);

            string kind = KindFor(prefix);
            if (kind == null)
            {
                Debug.LogWarning("[FoldNative] rejected: unknown marker " + prefix);
                return false;
            }

            if (!SignedMediaCommand.TryVerify(kind, body, out string payload, out string reason))
            {
                Debug.LogWarning("[FoldNative] rejected: " + prefix + " reason=" + reason);
                return false;
            }

            if (!FoldNativeConfig.Allows(payload))
            {
                Debug.LogWarning("[FoldNative] rejected: parental control");
                return false;
            }

            if (kind == "cmd")
            {
                string command = SignedMediaCommand.GetString(payload, "command");
                if (string.Equals(command, "STOP", StringComparison.OrdinalIgnoreCase))
                {
                    _ui.Enqueue(_ui.Stop);
                    return true;
                }

                Debug.Log("[FoldNative] command ignored: " + command);
                return false;
            }

            if (kind != "video" && kind != "image")
            {
                Debug.Log("[FoldNative] unsupported media kind: " + kind);
                return false;
            }

            string url = SignedMediaCommand.GetString(payload, "url");
            if (string.IsNullOrEmpty(url) || url.Length > 2048)
            {
                Debug.LogWarning("[FoldNative] rejected: missing or oversized url");
                return false;
            }

            if (kind == "video")
                _ui.Enqueue(() => _ui.PlayVideo(url));
            else
                _ui.Enqueue(() => _ui.PlayImage(url));

            Debug.Log("[FoldNative] accepted: " + prefix);
            return true;
        }

        private static string KindFor(string prefix)
        {
            switch (prefix)
            {
                case "[FOLD_VIDEO]": return "video";
                case "[FOLD_IMG]": return "image";
                case "[FOLD_AUDIO]": return "audio";
                case "[FOLD_TEXT]": return "text";
                case "[FOLD_CMD]": return "cmd";
                default: return null;
            }
        }
    }
}
