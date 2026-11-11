using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FoldEdge
{
    /// <summary>一键发布模块的请求体。</summary>
    public sealed record ModulePublishRequest(
        string Name,
        string VideoUrl,
        string? Author = null,
        string? Description = null,
        string[]? Tags = null,
        string? PosterUrl = null,
        float VideoTime = 1f);

    /// <summary>
    /// 媒体预处理管线：FFmpeg 转码 + 一键打包 .fdll。
    /// 只依赖系统 ffmpeg（如果存在），不引入额外 NuGet。
    /// </summary>
    internal static class MediaPipeline
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        private static bool? _hasFfmpeg;

        public static async Task<bool> HasFfmpegAsync()
        {
            if (_hasFfmpeg.HasValue) return _hasFfmpeg.Value;
            try
            {
                var psi = new ProcessStartInfo("ffmpeg")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-version");
                using var process = Process.Start(psi);
                if (process == null)
                {
                    _hasFfmpeg = false;
                    return false;
                }
                await process.WaitForExitAsync();
                _hasFfmpeg = process.ExitCode == 0;
            }
            catch
            {
                _hasFfmpeg = false;
            }
            return _hasFfmpeg.Value;
        }

        /// <summary>转码为 1080p 以内、约 3 Mbps 的 MP4（H.264 + AAC）。</summary>
        public static async Task TranscodeAsync(string input, string output, CancellationToken ct)
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in new[]
                     {
                         "-y", "-i", input,
                         "-vf", "scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease",
                         "-c:v", "libx264", "-preset", "veryfast",
                         "-b:v", "3M", "-maxrate", "4M", "-bufsize", "6M",
                         "-c:a", "aac", "-b:a", "128k",
                         "-movflags", "+faststart",
                         "-loglevel", "error",
                         output
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("无法启动 ffmpeg");
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                var detail = stderr.Length > 500 ? stderr.Substring(0, 500) : stderr;
                throw new InvalidOperationException($"ffmpeg 转码失败: {detail}");
            }
        }

        /// <summary>把视频 URL / 海报 / 时间轴打包成 .fdll，返回生成的文件路径。</summary>
        public static string BuildModule(string modulesDir, ModulePublishRequest request)
        {
            var name = Slug(request.Name);
            var manifest = JsonSerializer.Serialize(new
            {
                id = "com.fold." + name,
                name,
                version = "1.0.0",
                author = request.Author ?? "Fold",
                engineVersion = ">=1.0.0",
                description = request.Description ?? string.Empty,
                tags = request.Tags ?? Array.Empty<string>()
            }, JsonOpts);

            var timeline = new List<object>();
            if (!string.IsNullOrEmpty(request.PosterUrl))
            {
                timeline.Add(new { time = 0.0, action = "SHOW_IMAGE", url = request.PosterUrl });
            }
            timeline.Add(new
            {
                time = Math.Max(0f, request.VideoTime),
                action = "PLAY_VIDEO",
                url = request.VideoUrl
            });
            var timelineJson = JsonSerializer.Serialize(timeline, JsonOpts);

            Directory.CreateDirectory(modulesDir);
            var path = Path.Combine(modulesDir, name + ".fdll");
            if (File.Exists(path)) File.Delete(path);

            using var stream = File.Create(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            WriteEntry(zip, "manifest.json", manifest);
            WriteEntry(zip, "timeline.json", timelineJson);
            return path;
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        private static string Slug(string value)
        {
            var sb = new StringBuilder();
            foreach (var ch in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                    sb.Append(ch);
                else if (ch == ' ')
                    sb.Append('_');
            }
            var result = sb.ToString().Trim('_');
            return result.Length == 0 ? "module" : result;
        }
    }
}
