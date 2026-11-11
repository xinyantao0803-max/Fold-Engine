using System.Text;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace FoldEdge;

/// <summary>图片帧：低分辨率颜色矩阵（推送给游戏内插件）。</summary>
public sealed class ImageFrame
{
    public int Width { get; set; }
    public int Height { get; set; }
    public List<PixelJson> Frames { get; set; } = new();
}

/// <summary>单个像素颜色（RGBA）。键名固定小写，与插件端 MiniJson 解析约定一致。</summary>
public sealed class PixelJson
{
    [JsonPropertyName("r")] public int R { get; set; }
    [JsonPropertyName("g")] public int G { get; set; }
    [JsonPropertyName("b")] public int B { get; set; }
    [JsonPropertyName("a")] public int A { get; set; } = 255;
}

/// <summary>
/// 图片服务：从 CDN 下载全格式图片（PNG/JPG/WebP/GIF 等），
/// 解码并等比缩小为低分辨率颜色矩阵（.NET 8 + SkiaSharp，MIT）。
/// </summary>
public static class ImageService
{
    public static async Task<ImageFrame> DownloadAndConvertAsync(string url, int maxWidth, int maxHeight)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "(无 Content-Type)";
        byte[] data = await resp.Content.ReadAsByteArrayAsync();

        // 防盗链 / 被墙 / 404 页面防护：非图片响应直接报错，并暴露前 50 字符用于诊断
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            string snippet = data.Length == 0
                ? "(空响应)"
                : Encoding.UTF8.GetString(data, 0, Math.Min(50, data.Length))
                    .Replace("\r", " ").Replace("\n", " ");
            throw new InvalidDataException($"响应不是图片 (Content-Type: {contentType})，前 50 字符: {snippet}");
        }

        using var bitmap = SKBitmap.Decode(data)
            ?? throw new InvalidDataException("图片解码失败");

        // 等比缩小（只缩小不放大）
        double scale = Math.Min((double)maxWidth / bitmap.Width, (double)maxHeight / bitmap.Height);
        if (scale > 1.0) scale = 1.0;
        int w = Math.Max(1, (int)(bitmap.Width * scale));
        int h = Math.Max(1, (int)(bitmap.Height * scale));

        using var resized = bitmap.Resize(
            new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            SKFilterQuality.High) ?? throw new InvalidDataException("图片缩放失败");

        var frame = new ImageFrame { Width = w, Height = h };
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SKColor p = resized.GetPixel(x, y);
                frame.Frames.Add(new PixelJson { R = p.Red, G = p.Green, B = p.Blue, A = p.Alpha });
            }
        }

        return frame;
    }

    /// <summary>本地生成渐变测试图（纯内存生成，零网络依赖，绝对稳定）。</summary>
    public static ImageFrame GenerateTestFrame(int width = 8, int height = 6)
    {
        var frame = new ImageFrame { Width = width, Height = height };
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                frame.Frames.Add(new PixelJson
                {
                    R = x * 255 / Math.Max(1, width - 1),
                    G = y * 255 / Math.Max(1, height - 1),
                    B = (x + y) * 255 / Math.Max(1, width + height - 2),
                    A = 255
                });
            }
        }
        return frame;
    }

    /// <summary>
    /// 本地生成 8x6 彩色笑脸测试图：彩虹渐变背景 + 黄脸 + 黑眼 + 红嘴。
    /// 字符地图：Y=黄 K=黑 R=红 .=彩虹渐变背景。零网络依赖。
    /// </summary>
    public static ImageFrame GenerateSmileyFrame()
    {
        string[] map =
        {
            "........",
            ".YYYYYY.",
            ".YKYYKY.",
            ".YKYYKY.",
            ".YRRRRY.",
            "..YYYY.."
        };
        const int w = 8, h = 6;
        var frame = new ImageFrame { Width = w, Height = h };
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int r, g, b;
                switch (map[y][x])
                {
                    case 'Y': r = 255; g = 213; b = 79; break;
                    case 'K': r = 40; g = 40; b = 40; break;
                    case 'R': r = 229; g = 57; b = 53; break;
                    default:
                        r = x * 255 / (w - 1);
                        g = y * 255 / (h - 1);
                        b = 255 - (x + y) * 255 / (w + h - 2);
                        break;
                }
                frame.Frames.Add(new PixelJson { R = r, G = g, B = b, A = 255 });
            }
        }
        return frame;
    }
}
