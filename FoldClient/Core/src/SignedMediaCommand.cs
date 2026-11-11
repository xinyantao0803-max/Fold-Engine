using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FoldClient
{
    /// <summary>
    /// 验签与指令解析。与 FH 的 MediaSigner / FE 的 FoldVideoPlayer 共享同一签名格式：
    ///   body = &lt;unix毫秒&gt;|&lt;nonce&gt;|&lt;base64url(DER 签名)&gt;|&lt;payloadJson&gt;
    ///   签名内容 = kind + "\n" + payload + "\n" + timestamp + "\n" + nonce
    /// 只接受 3 秒内、nonce 未使用过的指令。
    /// </summary>
    internal static class SignedMediaCommand
    {
        private static readonly object Gate = new object();
        private static ECDsa _verificationKey;
        private static string _pinnedKeyBase64 = string.Empty;
        private static readonly Dictionary<string, long> SeenNonces =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// 固定公钥（trust anchor）。必须在接收任何动态公钥之前调用。
        /// 未设置时 fail-closed：所有指令都会被拒绝。
        /// </summary>
        public static bool SetPinnedKey(string base64)
        {
            string clean = CleanBase64(base64);
            if (clean.Length == 0)
            {
                _pinnedKeyBase64 = string.Empty;
                LogKey("pinned key is empty; all commands will be rejected");
                return false;
            }

            _pinnedKeyBase64 = clean;
            LogKey("pinned key set: length=" + clean.Length);
            return SetPublicKey(clean);
        }

        /// <summary>从 FoldClient.pub（Base64 或 PEM）加载固定公钥；缺失即 fail-closed。</summary>
        public static bool LoadPinnedKeyFromFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    _pinnedKeyBase64 = string.Empty;
                    LogKey("pinned key missing: " + path + "; all commands will be rejected");
                    return false;
                }

                return SetPinnedKey(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                _pinnedKeyBase64 = string.Empty;
                LogKeyError(ex, path, string.Empty);
                return false;
            }
        }

        /// <summary>
        /// 由 FH 通过 WebSocket 动态下发的 SPKI Base64 公钥。
        /// 只接受与固定公钥完全一致的值，否则拒绝。
        /// </summary>
        public static bool SetPublicKey(string base64)
        {
            string clean = CleanBase64(base64);
            if (clean.Length == 0)
            {
                LogKey("public key is empty");
                return false;
            }

            if (string.IsNullOrEmpty(_pinnedKeyBase64))
            {
                LogKey("no pinned public key; rejecting dynamic key");
                return false;
            }

            bool matches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(clean),
                Encoding.UTF8.GetBytes(_pinnedKeyBase64));

            if (!matches)
            {
                LogKey("dynamic public key does not match pinned key; rejected");
                return false;
            }

            LogKey("public key length=" + clean.Length +
                   ", multipleOf4=" + (clean.Length % 4 == 0));

            if (clean.Length % 4 != 0)
            {
                LogKeyError(
                    new FormatException("public key length is not a multiple of 4"),
                    base64, clean);
                return false;
            }

            try
            {
                byte[] spki = Convert.FromBase64String(clean);

                var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(spki, out _);

                Volatile.Write(ref _verificationKey, key);
                LogKey("public key accepted");
                return true;
            }
            catch (Exception ex)
            {
                LogKeyError(ex, base64, clean);
                return false;
            }
        }

        private static string CleanBase64(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string clean = Regex.Replace(value, @"-----[^-]+-----", string.Empty);
            return Regex.Replace(clean, @"\s", string.Empty);
        }

        private static void LogKey(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "FoldClientKey.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch
            {
                // 日志失败不影响验签
            }
        }

        private static void LogKeyError(Exception ex, string rawKey, string cleanBase64)
        {
            try
            {
                string detail =
                    "rawLength=" + rawKey.Length +
                    ", cleanLength=" + cleanBase64.Length +
                    ", raw=[" + rawKey + "]" + Environment.NewLine +
                    ex + Environment.NewLine;

                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "FoldClientKeyError.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + detail);
            }
            catch
            {
                // 日志失败不应导致类型初始化崩溃
            }
        }

        public static bool TryVerify(string kind, string body, out string payload)
        {
            return TryVerify(kind, body, out payload, out _);
        }

        /// <summary>
        /// 验签入口：失败时 reason 给出具体原因（malformed/expired/replay/bad-signature/no-key）。
        /// 只有完整验签通过后才会登记 nonce。
        /// </summary>
        public static bool TryVerify(string kind, string body, out string payload, out string reason)
        {
            payload = null;
            reason = "malformed";

            if (string.IsNullOrEmpty(body))
                return false;

            int first = body.IndexOf('|');
            if (first <= 0)
                return false;
            int second = body.IndexOf('|', first + 1);
            if (second <= first + 1)
                return false;
            int third = body.IndexOf('|', second + 1);
            if (third <= second + 1)
                return false;

            string timestampText = body.Substring(0, first);
            string nonce = body.Substring(first + 1, second - first - 1);
            string signatureText = body.Substring(second + 1, third - second - 1);
            string payloadText = body.Substring(third + 1);

            reason = "bad-timestamp";
            long timestamp;
            if (!long.TryParse(timestampText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out timestamp))
                return false;

            reason = "expired";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Math.Abs(now - timestamp) > FvpConfig.MaxAgeMilliseconds)
                return false;

            reason = "replay";
            lock (Gate)
            {
                if (SeenNonces.ContainsKey(nonce))
                    return false;
            }

            reason = "bad-signature-encoding";
            byte[] signature = Base64UrlDecode(signatureText);
            if (signature == null)
                return false;

            byte[] rawSignature = DerToRaw(signature);
            if (rawSignature == null)
                return false;

            string message = kind + "\n" + payloadText + "\n" +
                             timestamp.ToString(CultureInfo.InvariantCulture) + "\n" + nonce;
            byte[] data = Encoding.UTF8.GetBytes(message);

            ECDsa key = Volatile.Read(ref _verificationKey);
            if (key == null)
            {
                reason = "no-key";
                return false;
            }

            reason = "bad-signature";
            lock (Gate)
            {
                if (!key.VerifyData(data, rawSignature, HashAlgorithmName.SHA256))
                    return false;
            }

            // 只有签名验证通过后才登记 nonce，避免无效包消耗 nonce。
            lock (Gate)
            {
                if (SeenNonces.ContainsKey(nonce))
                {
                    reason = "replay";
                    return false;
                }

                SeenNonces[nonce] = timestamp;
                if (SeenNonces.Count > 2048)
                    PruneNonces(now);
            }

            payload = payloadText;
            reason = "ok";
            return true;
        }

        public static string GetString(string json, string key)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            string needle = "\"" + key + "\"";
            int position = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (position < 0)
                return null;

            position = json.IndexOf(':', position + needle.Length);
            if (position < 0)
                return null;
            position++;

            while (position < json.Length && char.IsWhiteSpace(json[position]))
                position++;
            if (position >= json.Length || json[position] != '"')
                return null;
            position++;

            var builder = new StringBuilder();
            while (position < json.Length)
            {
                char c = json[position];
                if (c == '\\' && position + 1 < json.Length)
                {
                    char escape = json[position + 1];
                    position += 2;
                    switch (escape)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        default: builder.Append(escape); break;
                    }
                    continue;
                }
                if (c == '"')
                    break;
                builder.Append(c);
                position++;
            }

            return builder.ToString();
        }

        public static float GetNumber(string json, string key, float fallback)
        {
            if (string.IsNullOrEmpty(json))
                return fallback;

            string needle = "\"" + key + "\"";
            int position = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (position < 0)
                return fallback;

            position = json.IndexOf(':', position + needle.Length);
            if (position < 0)
                return fallback;
            position++;

            while (position < json.Length && char.IsWhiteSpace(json[position]))
                position++;

            int start = position;
            while (position < json.Length &&
                   (char.IsDigit(json[position]) || json[position] == '-' || json[position] == '.'))
                position++;

            float value;
            return float.TryParse(json.Substring(start, position - start),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        private static void PruneNonces(long now)
        {
            var expired = new List<string>();
            foreach (KeyValuePair<string, long> pair in SeenNonces)
            {
                if (now - pair.Value > FvpConfig.MaxAgeMilliseconds * 4)
                    expired.Add(pair.Key);
            }
            foreach (string nonce in expired)
                SeenNonces.Remove(nonce);
        }

        private static byte[] Base64UrlDecode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            string text = value.Replace('-', '+').Replace('_', '/');
            switch (text.Length % 4)
            {
                case 2: text += "=="; break;
                case 3: text += "="; break;
            }

            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // .NET Framework 的 ECDsa 只接受 IEEE P1363（r||s），FH 签出的是 DER SEQUENCE。
        private static byte[] DerToRaw(byte[] der)
        {
            int offset = 0;
            if (der.Length < 8 || der[offset++] != 0x30)
                return null;

            int sequenceLength;
            if (!ReadDerLength(der, ref offset, out sequenceLength))
                return null;
            if (offset + sequenceLength > der.Length)
                return null;

            var raw = new byte[64];
            for (int part = 0; part < 2; part++)
            {
                if (offset >= der.Length || der[offset++] != 0x02)
                    return null;

                int length;
                if (!ReadDerLength(der, ref offset, out length) || length <= 0)
                    return null;
                if (offset + length > der.Length)
                    return null;

                int begin = offset;
                offset += length;

                while (length > 1 && der[begin] == 0x00)
                {
                    begin++;
                    length--;
                }
                if (length > 32)
                    return null;

                Buffer.BlockCopy(der, begin, raw, part * 32 + (32 - length), length);
            }

            return raw;
        }

        private static bool ReadDerLength(byte[] der, ref int offset, out int length)
        {
            length = 0;
            if (offset >= der.Length)
                return false;

            int first = der[offset++];
            if ((first & 0x80) == 0)
            {
                length = first;
                return true;
            }

            int count = first & 0x7F;
            if (count == 0 || count > 4 || offset + count > der.Length)
                return false;

            for (int i = 0; i < count; i++)
                length = (length << 8) | der[offset++];
            return true;
        }
    }
}
