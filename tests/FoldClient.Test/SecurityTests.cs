using System;
using System.Security.Cryptography;
using System.Text;
using FoldClient;

namespace FoldClient.Test
{
    internal static class SecurityTests
    {
        public static bool Run()
        {
            Console.WriteLine("========================================");
            Console.WriteLine(" SignedMediaCommand 安全测试");
            Console.WriteLine("========================================");

            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

            if (!SignedMediaCommand.SetPinnedKey(publicKey))
            {
                Console.WriteLine("[FAIL] pinned key load");
                return false;
            }

            bool passed = true;

            (bool ok, string reason) valid = Verify(key, "video", "http://example/test.mp4", 0);
            passed &= Check("valid-signature", valid.ok, true, valid.reason, "ok");

            (string tamperedBody, _) = Build(key, "video", "http://example/test.mp4", 0);
            string tampered = tamperedBody.Replace("test.mp4", "evil.mp4");
            bool tamperedOk = SignedMediaCommand.TryVerify(
                "video", tampered, out _, out string tamperedReason);
            passed &= Check("tampered-payload", tamperedOk, false, tamperedReason, "bad-signature");

            (bool expiredOk, string expiredReason) =
                Verify(key, "video", "http://example/test.mp4", -5000);
            passed &= Check("expired-timestamp", expiredOk, false, expiredReason, "expired");

            using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            (bool wrongKeyOk, string wrongKeyReason) =
                Verify(otherKey, "video", "http://example/test.mp4", 0);
            passed &= Check("wrong-key", wrongKeyOk, false, wrongKeyReason, "bad-signature");

            (string replayBody, _) = Build(key, "video", "http://example/test.mp4", 0);
            bool first = SignedMediaCommand.TryVerify(
                "video", replayBody, out _, out string firstReason);
            bool second = SignedMediaCommand.TryVerify(
                "video", replayBody, out _, out string secondReason);
            passed &= Check("replay-first", first, true, firstReason, "ok");
            passed &= Check("replay-second", second, false, secondReason, "replay");

            Console.WriteLine(passed ? "[PASS] 安全测试全部通过" : "[FAIL] 安全测试存在失败项");
            return passed;
        }

        private static (bool ok, string reason) Verify(
            ECDsa key, string kind, string payload, long skewMs)
        {
            (string body, _) = Build(key, kind, payload, skewMs);
            bool ok = SignedMediaCommand.TryVerify(kind, body, out _, out string reason);
            return (ok, reason);
        }

        private static (string Body, string Nonce) Build(
            ECDsa key, string kind, string payload, long skewMs)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + skewMs;
            string nonce = Guid.NewGuid().ToString("N");
            string message = kind + "\n" + payload + "\n" + timestamp + "\n" + nonce;

            byte[] signature = key.SignData(
                Encoding.UTF8.GetBytes(message),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

            string body = timestamp + "|" + nonce + "|" + Base64Url(signature) + "|" + payload;
            return (body, nonce);
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static bool Check(
            string name, bool actual, bool expected, string reason, string expectedReason)
        {
            bool ok = actual == expected &&
                      (expected || string.Equals(reason, expectedReason, StringComparison.Ordinal));

            Console.WriteLine(
                (ok ? "[PASS] " : "[FAIL] ") + name +
                " actual=" + actual + " reason=" + reason);

            return ok;
        }
    }
}
