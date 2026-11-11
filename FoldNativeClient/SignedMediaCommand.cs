using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace FoldNative
{
    /// <summary>
    /// Native Unity port of the Fold command verifier.
    ///
    /// This is a native Unity integration module. NW can copy these scripts
    /// directly into their client source code. It does not require any external
    /// launchers.
    ///
    /// Envelope:
    ///   [FOLD_*]&lt;unix-ms&gt;|&lt;nonce&gt;|&lt;base64url(DER signature)&gt;|&lt;payloadJson&gt;
    ///
    /// Signature input:
    ///   kind + "\n" + payloadJson + "\n" + timestampMs + "\n" + nonce
    ///
    /// Security properties:
    ///   - ECDSA P-256 + SHA-256
    ///   - 3-second acceptance window
    ///   - single-use nonce (replay rejected)
    ///   - pinned public key; dynamic keys are rejected unless they match the pin
    ///   - fail-closed when no key is configured
    /// </summary>
    internal static class SignedMediaCommand
    {
        private static readonly object Gate = new object();
        private static ECDsa _verificationKey;
        private static string _pinnedKeyBase64 = string.Empty;
        private static readonly Dictionary<string, long> SeenNonces =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// Installs the trust anchor. Must be called before any command is accepted.
        /// Without a pinned key all commands are rejected (fail-closed).
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

        /// <summary>
        /// Loads the pinned key from a file. For a native build the recommended
        /// location is StreamingAssets/FoldClient.pub.
        /// </summary>
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
                LogKeyError("pinned key load failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Accepts a key delivered over the network only when it is byte-for-byte
        /// identical to the pinned key. This blocks public-key substitution MitM.
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

            if (!FixedTimeEquals(clean, _pinnedKeyBase64))
            {
                LogKey("dynamic public key does not match pinned key; rejected");
                return false;
            }

            if (clean.Length % 4 != 0)
            {
                LogKeyError("public key length is not a multiple of 4", null);
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
                LogKeyError("public key import failed", ex);
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

        public static bool TryVerify(string kind, string body, out string payload)
        {
            return TryVerify(kind, body, out payload, out _);
        }

        /// <summary>
        /// Verifies a signed body and returns a machine-readable failure reason:
        /// malformed / bad-timestamp / expired / replay / bad-signature-encoding /
        /// no-key / bad-signature.
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
            if (Math.Abs(now - timestamp) > FoldNativeConfig.MaxAgeMilliseconds)
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

            // Only register the nonce after a successful signature check, so an
            // invalid packet cannot consume a legitimate nonce.
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

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
                diff |= left[i] ^ right[i];

            return diff == 0;
        }

        private static void PruneNonces(long now)
        {
            var expired = new List<string>();
            foreach (KeyValuePair<string, long> pair in SeenNonces)
            {
                if (now - pair.Value > FoldNativeConfig.MaxAgeMilliseconds * 4)
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

        private static void LogKey(string message)
        {
            Debug.Log("[FoldNative] " + message);
        }

        private static void LogKeyError(string stage, Exception ex)
        {
            Debug.LogError("[FoldNative] " + stage + (ex == null ? string.Empty : ": " + ex));
        }
    }
}
