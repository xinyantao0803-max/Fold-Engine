using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FoldPacker
{
    /// <summary>
    /// FoldPacker：把包含 manifest.json / timeline.json / 媒体文件的目录打包为 .fdll。
    /// .fdll 本质是 ZIP；--encrypt 生成 FH 可读取的 FOLDENC1 容器；
    /// --sign 对最终 .fdll 字节生成 ECDSA P-256 分离签名 .sig。
    /// </summary>
    internal static class Program
    {
        private const string Magic = "FOLDENC1";

        // 必须与 FoldEdge/Program.cs 和 FoldEngine/FoldCrypto.cs 保持一致。
        private static readonly byte[] PresetKey =
            Encoding.UTF8.GetBytes("Fold-Engine-Preset-AES-Key-2026!");
        private static readonly byte[] PresetIv =
            Encoding.UTF8.GetBytes("FoldEngine-IV-01");

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FoldPacker] " + ex.Message);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            string input = null;
            string output = null;
            string signKey = null;
            bool encrypt = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h":
                    case "--help":
                        PrintUsage();
                        return 0;

                    case "-o":
                    case "--output":
                        output = NextValue(args, ref i, args[i]);
                        break;

                    case "--encrypt":
                        encrypt = true;
                        break;

                    case "--sign":
                        signKey = NextValue(args, ref i, args[i]);
                        break;

                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException("未知参数: " + args[i]);
                        if (input != null)
                            throw new ArgumentException("只能指定一个输入目录");
                        input = args[i];
                        break;
                }
            }

            if (string.IsNullOrEmpty(input))
                throw new ArgumentException("缺少输入目录");

            input = Path.GetFullPath(input);
            if (!Directory.Exists(input))
                throw new DirectoryNotFoundException("目录不存在: " + input);

            string manifestPath = Path.Combine(input, "manifest.json");
            string timelinePath = Path.Combine(input, "timeline.json");

            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("缺少 manifest.json", manifestPath);
            if (!File.Exists(timelinePath))
                throw new FileNotFoundException("缺少 timeline.json", timelinePath);

            ManifestInfo manifest = ReadManifest(manifestPath);

            // 提前校验 timeline.json，避免把损坏的 JSON 打进包里。
            using (JsonDocument.Parse(File.ReadAllText(timelinePath)))
            {
            }

            if (string.IsNullOrEmpty(output))
            {
                string folderName = new DirectoryInfo(
                    input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
                output = Path.Combine(Directory.GetCurrentDirectory(), folderName + ".fdll");
            }

            output = Path.GetFullPath(output);

            (byte[] zipBytes, int entries) = BuildZip(input, output);
            byte[] payload = encrypt ? Encrypt(zipBytes) : zipBytes;

            string outputDirectory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            File.WriteAllBytes(output, payload);

            string sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

            string signaturePath = null;
            if (!string.IsNullOrEmpty(signKey))
            {
                signaturePath = output + ".sig";
                byte[] signature = SignFile(payload, Path.GetFullPath(signKey));
                File.WriteAllText(signaturePath, Convert.ToBase64String(signature) + Environment.NewLine);
            }

            Console.WriteLine("[FoldPacker] " + output);
            Console.WriteLine("  module    : " +
                              (string.IsNullOrEmpty(manifest.Name)
                                  ? Path.GetFileNameWithoutExtension(output)
                                  : manifest.Name) +
                              (string.IsNullOrEmpty(manifest.Version) ? "" : " v" + manifest.Version));
            if (!string.IsNullOrEmpty(manifest.Author))
                Console.WriteLine("  author    : " + manifest.Author);
            if (!string.IsNullOrEmpty(manifest.Tags))
                Console.WriteLine("  tags      : " + manifest.Tags);
            Console.WriteLine("  entries   : " + entries);
            Console.WriteLine("  encrypted : " + (encrypt ? "yes (FOLDENC1 / AES-256-CBC)" : "no"));
            Console.WriteLine("  signed    : " +
                              (signaturePath != null
                                  ? "yes -> " + Path.GetFileName(signaturePath)
                                  : "no"));
            Console.WriteLine("  size      : " + payload.Length + " bytes");
            Console.WriteLine("  sha256    : " + sha256);

            return 0;
        }

        private static (byte[] Bytes, int Entries) BuildZip(string input, string outputPath)
        {
            string normalizedOutput = Path.GetFullPath(outputPath);

            List<string> files = Directory
                .EnumerateFiles(input, "*", SearchOption.AllDirectories)
                .Where(file => !string.Equals(
                    Path.GetFullPath(file), normalizedOutput, StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".fdll", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var memory = new MemoryStream();

            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (string file in files)
                {
                    string entryName = Path.GetRelativePath(input, file).Replace('\\', '/');
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = ClampTimestamp(File.GetLastWriteTime(file));

                    using Stream target = entry.Open();
                    using FileStream source = File.OpenRead(file);
                    source.CopyTo(target);
                }
            }

            return (memory.ToArray(), files.Count);
        }

        private static DateTimeOffset ClampTimestamp(DateTime value)
        {
            if (value.Year < 1980)
                return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if (value.Year > 2107)
                return new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

            return new DateTimeOffset(value);
        }

        private static byte[] Encrypt(byte[] plain)
        {
            using Aes aes = Aes.Create();
            aes.Key = PresetKey;
            aes.IV = PresetIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using ICryptoTransform encryptor = aes.CreateEncryptor();
            byte[] cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);

            byte[] magic = Encoding.ASCII.GetBytes(Magic);
            byte[] result = new byte[magic.Length + cipher.Length];
            Buffer.BlockCopy(magic, 0, result, 0, magic.Length);
            Buffer.BlockCopy(cipher, 0, result, magic.Length, cipher.Length);
            return result;
        }

        private static byte[] SignFile(byte[] data, string keyPath)
        {
            if (!File.Exists(keyPath))
                throw new FileNotFoundException("私钥不存在", keyPath);

            string pem = File.ReadAllText(keyPath);
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(pem);

            return key.SignData(
                data,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }

        private static ManifestInfo ReadManifest(string path)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            var info = new ManifestInfo
            {
                Name = GetString(root, "name"),
                Version = GetString(root, "version"),
                Author = GetString(root, "author")
            };

            if (root.TryGetProperty("tags", out JsonElement tags) &&
                tags.ValueKind == JsonValueKind.Array)
            {
                var values = new List<string>();
                foreach (JsonElement tag in tags.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String)
                        values.Add(tag.GetString());
                }

                info.Tags = string.Join(", ", values);
            }

            return info;
        }

        private static string GetString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string NextValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException(option + " 缺少参数");

            return args[++index];
        }

        private static void PrintUsage()
        {
            Console.WriteLine("FoldPacker V15 — pack a folder into .fdll (ZIP)");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  FoldPacker <module-dir> [-o output.fdll] [--encrypt] [--sign private-key.pem]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -o, --output   Output path (default: <folder>.fdll in current directory)");
            Console.WriteLine("  --encrypt      Wrap ZIP in FOLDENC1 (AES-256-CBC, FH-compatible preset key)");
            Console.WriteLine("  --sign         Write <output>.sig (Base64 DER ECDSA P-256 SHA-256 signature)");
            Console.WriteLine("  -h, --help     Show this help");
            Console.WriteLine();
            Console.WriteLine("The input folder must contain manifest.json and timeline.json.");
            Console.WriteLine("Encryption uses the existing Fold preset key, not a user password.");
        }

        private sealed class ManifestInfo
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public string Author { get; set; }
            public string Tags { get; set; }
        }
    }
}
