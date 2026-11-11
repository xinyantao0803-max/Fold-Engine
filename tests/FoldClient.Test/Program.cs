using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FoldClient.MediaFoundation;

Console.WriteLine("========================================");
Console.WriteLine(" FoldClient MfVideoDecoder 独立测试");
Console.WriteLine("========================================");

string path = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "FoldEdge", "Assets", "785e5f86a53f49eab99994b6f3fd156d.mp4"));

Console.WriteLine("media      = " + path);
Console.WriteLine("hardwareReq= " + FoldClient.FvpConfig.RequireHardwareDecoding);

if (!File.Exists(path))
{
    Console.WriteLine("[FAIL] 测试视频不存在");
    return 1;
}

var decoder = new MfVideoDecoder();
decoder.Start(path);

byte[] frame = null;
int width = 0;
int height = 0;

Stopwatch clock = Stopwatch.StartNew();
while (clock.Elapsed < TimeSpan.FromSeconds(20))
{
    byte[] pixels;
    int w;
    int h;

    if (decoder.TryTakeFrame(out pixels, out w, out h))
    {
        // 在持锁期间复制，释放后解码线程可安全复用缓冲。
        frame = (byte[])pixels.Clone();
        width = w;
        height = h;
        decoder.ReleaseFrame();
        break;
    }

    if (decoder.HasEnded)
        break;

    Thread.Sleep(20);
}

if (frame == null)
{
    Console.WriteLine("[FAIL] 未取到帧");
    Console.WriteLine("error = " + (decoder.Error ?? "(none)"));
    decoder.Stop(true);
    return 2;
}

long nonZero = 0;
foreach (byte b in frame)
{
    if (b != 0)
        nonZero++;
}

Console.WriteLine("hardware   = " + decoder.IsHardwareAccelerated);
Console.WriteLine("width      = " + width);
Console.WriteLine("height     = " + height);
Console.WriteLine("stride     = " + (width * 4) + " bytes");
Console.WriteLine("buffer     = " + frame.Length + " bytes");
Console.WriteLine("expected   = " + (width * height * 4) + " bytes");
Console.WriteLine("nonZero    = " + nonZero + " bytes");
Console.WriteLine("elapsed    = " + clock.ElapsedMilliseconds + " ms");

string output = Path.GetFullPath("first_frame.bmp");
SaveBmp(output, frame, width, height);

Console.WriteLine("bmp        = " + output);
Console.WriteLine("bmp size   = " + new FileInfo(output).Length + " bytes");
Console.WriteLine("[PASS] 解码器成功输出首帧");

decoder.Stop(true);
return 0;

static void SaveBmp(string file, byte[] bgra, int width, int height)
{
    int rowBytes = width * 4;
    int imageSize = rowBytes * height;

    using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write))
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + imageSize);
        writer.Write(0);
        writer.Write(54);

        writer.Write(40);
        writer.Write(width);
        writer.Write(-height); // 负高度 = 自上而下
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(imageSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        writer.Write(bgra, 0, imageSize);
    }
}
