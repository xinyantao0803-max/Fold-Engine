using System;

namespace FoldClient
{
    /// <summary>
    /// FVP 唯一指令接收层：先验签，再执行；任何失败直接丢弃并记录原因。
    /// 未来 Mirror UDP 原生版也必须复用同一个 Receive 入口。
    /// </summary>
    internal sealed class FoldCommandReceiver
    {
        private readonly OverlayWindow _overlay;

        public FoldCommandReceiver(OverlayWindow overlay)
        {
            _overlay = overlay;
        }

        public bool Receive(string marker)
        {
            if (string.IsNullOrEmpty(marker))
            {
                AppLog.Write("command rejected: empty marker");
                return false;
            }

            int end = marker.IndexOf(']');
            if (end <= 0)
            {
                AppLog.Write("command rejected: malformed marker");
                return false;
            }

            string prefix = marker.Substring(0, end + 1);
            string body = marker.Substring(end + 1);
            string kind = KindFor(prefix);
            if (kind == null)
            {
                AppLog.Write("command rejected: unknown marker " + prefix);
                return false;
            }

            if (!SignedMediaCommand.TryVerify(kind, body, out string payload, out string reason))
            {
                AppLog.Write("command rejected: " + prefix + " reason=" + reason);
                return false;
            }

            if (kind != "video" && kind != "image")
            {
                AppLog.Write("command ignored: unsupported kind " + kind);
                return false;
            }

            if (!FvpConfig.Allows(payload))
            {
                AppLog.Write("command rejected: parental control");
                return false;
            }

            string url = SignedMediaCommand.GetString(payload, "url");
            if (string.IsNullOrEmpty(url))
            {
                AppLog.Write("command rejected: missing url");
                return false;
            }

            try
            {
                if (kind == "video")
                    _overlay.Dispatcher.BeginInvoke(new Action(() => _overlay.PlayVideo(url)));
                else
                    _overlay.Dispatcher.BeginInvoke(new Action(() => _overlay.PlayImage(url)));
            }
            catch (Exception ex)
            {
                AppLog.Write("command dispatch failed: " + ex.Message);
                return false;
            }

            AppLog.Write("command accepted: " + prefix);
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
