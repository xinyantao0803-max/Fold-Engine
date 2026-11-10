using System;
using System.Security.Cryptography;
using System.Text;

namespace FoldEngine
{
    /// <summary>
    /// 预设密钥 AES-256-CBC（PKCS7）解密：
    ///   · 加密 .fdll 包（FOLDENC1 容器）由 FoldEdge 解密后读取；
    ///   · 加密模块的指令负载（enc 字段）由插件在内存中解密后再解析。
    /// 密钥与 IV 必须与 FoldEdge/Program.cs 中的 FoldCrypto 完全一致。
    /// </summary>
    public static class FoldCrypto
    {
        /// <summary>加密 .fdll 文件头魔数。</summary>
        public const string Magic = "FOLDENC1";

        private static readonly byte[] Key = Encoding.UTF8.GetBytes("Fold-Engine-Preset-AES-Key-2026!");
        private static readonly byte[] Iv = Encoding.UTF8.GetBytes("FoldEngine-IV-01");

        /// <summary>解密 Base64 密文，返回 UTF-8 JSON 文本。</summary>
        public static string DecryptToJson(string base64)
        {
            var cipher = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(Decrypt(cipher));
        }

        /// <summary>解密字节数组（内存中完成，不落盘）。</summary>
        public static byte[] Decrypt(byte[] cipher)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Key;
                aes.IV = Iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                }
            }
        }
    }
}
