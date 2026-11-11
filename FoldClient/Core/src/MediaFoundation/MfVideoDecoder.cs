using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace FoldClient.MediaFoundation
{
    /// <summary>
    /// Media Foundation 解码器（Vortice.Windows 封装）：
    ///   1. 独立 D3D11 设备 + IMFDXGIDeviceManager，强制 DXVA 硬件解码；
    ///   2. 输出 RGB32，解码线程按时间戳节流并写入双缓冲；
    ///   3. 主线程只负责一次纹理上传。
    ///
    /// 纯 C# 无法拿到 Unity 的 D3D11 设备，因此这里不做“GPU 零拷贝”，
    /// 但保证解码完全在后台线程，且默认拒绝软件回退。
    /// </summary>
    internal sealed class MfVideoDecoder
    {
        private const int StopTimeoutMilliseconds = 3000;

        private readonly object _frameGate = new object();

        private Thread _thread;
        private volatile bool _running;
        private volatile bool _ended;
        private volatile bool _hardware;
        private string _url;
        private string _error;

        private byte[] _pending;
        private byte[] _write;
        private int _pendingWidth;
        private int _pendingHeight;
        private bool _hasFrame;

        private int _width;
        private int _height;
        private int _stride;
        private int _droppedFrames;

        public bool IsHardwareAccelerated { get { return _hardware; } }
        public bool HasEnded { get { return _ended; } }
        public string Error { get { return _error; } }

        /// <summary>渲染速度跟不上解码时被丢弃的旧帧数量（只保留最新帧）。</summary>
        public int DroppedFrames { get { return _droppedFrames; } }

        public void Start(string url)
        {
            Stop(false);

            _url = url;
            _error = null;
            _ended = false;
            _hardware = false;
            _running = true;
            _droppedFrames = 0;

            _thread = new Thread(DecodeThread)
            {
                IsBackground = true,
                Name = "FoldClient.MediaFoundation"
            };
            _thread.Start();
        }

        public void Stop(bool waitForExit)
        {
            _running = false;

            Thread thread = _thread;
            _thread = null;
            if (waitForExit && thread != null && thread.IsAlive)
                thread.Join(StopTimeoutMilliseconds);

            lock (_frameGate)
            {
                _hasFrame = false;
                _pending = null;
                _write = null;
            }

            _ended = true;
        }

        /// <summary>
        /// 取一帧。返回 true 时调用方必须在使用完 pixels 后调用 ReleaseFrame()。
        /// 期间解码线程会被阻塞，保证像素数组不会被并发覆盖。
        /// </summary>
        public bool TryTakeFrame(out byte[] pixels, out int width, out int height)
        {
            Monitor.Enter(_frameGate);

            if (!_hasFrame || _pending == null)
            {
                Monitor.Exit(_frameGate);
                pixels = null;
                width = 0;
                height = 0;
                return false;
            }

            pixels = _pending;
            width = _pendingWidth;
            height = _pendingHeight;
            _hasFrame = false;
            return true;
        }

        public void ReleaseFrame()
        {
            Monitor.Exit(_frameGate);
        }

        private void DecodeThread()
        {
            bool comInitialized = false;
            IMFSourceReader reader = null;

            try
            {
                if (CoInitializeEx(IntPtr.Zero, 0) >= 0)
                    comInitialized = true;

                if (MediaFactory.MFStartup(false).Code < 0)
                {
                    _error = "MFStartup 失败";
                    return;
                }

                reader = CreateReader(true);
                if (reader == null)
                {
                    if (FvpConfig.RequireHardwareDecoding)
                    {
                        _error = "DXVA 硬件解码不可用；已按配置拒绝软件回退" +
                                 (string.IsNullOrEmpty(_error) ? string.Empty : "：" + _error);
                        return;
                    }

                    reader = CreateReader(false);
                }

                if (reader == null)
                {
                    _error = "无法创建 Media Foundation SourceReader";
                    return;
                }

                ReadLoop(reader);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();

                MediaFactory.MFShutdown();

                if (comInitialized)
                    CoUninitialize();

                _ended = true;
                _running = false;
            }
        }

        private IMFSourceReader CreateReader(bool hardware)
        {
            IMFAttributes attributes = null;
            IMFDXGIDeviceManager manager = null;
            ID3D11Device device = null;

            try
            {
                attributes = MediaFactory.MFCreateAttributes(4);

                if (hardware)
                {
                    // 注意：不能设置 SourceReaderAttributeKeys.EnableVideoProcessing。
                    // 它与 D3D 管理器组合会让 MFCreateSourceReaderFromURL 返回 E_INVALIDARG；
                    // 硬件路径直接输出 RGB32，由 MF 完成格式转换。
                    device = D3D11.D3D11CreateDevice(
                        DriverType.Hardware,
                        DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                        new[] { FeatureLevel.Level_11_0 });

                    using (ID3D11Multithread multithread = device.QueryInterface<ID3D11Multithread>())
                        multithread.SetMultithreadProtected(true);

                    manager = MediaFactory.MFCreateDXGIDeviceManager();
                    manager.ResetDevice(device);

                    attributes.Set(SourceReaderAttributeKeys.D3DManager, manager);
                    attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
                }
                else
                {
                    // 软件回退才需要显式开启 YUV → RGB32 转换。
                    attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
                }

                IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(_url, attributes);

                reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

                using (IMFMediaType outputType = MediaFactory.MFCreateMediaType())
                {
                    outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                    reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
                }

                using (IMFMediaType currentType =
                       reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
                {
                    ulong frameSize = currentType.GetUInt64(MediaTypeAttributeKeys.FrameSize);

                    // DXVA + RGB32 的输出类型可能不带 MF_MT_DEFAULT_STRIDE，缺失时按 width*4 处理。
                    uint stride = 0;
                    try
                    {
                        stride = currentType.GetUInt32(MediaTypeAttributeKeys.DefaultStride);
                    }
                    catch (Exception)
                    {
                        stride = 0;
                    }

                    int width = (int)(frameSize >> 32);
                    int height = (int)(frameSize & 0xFFFFFFFF);
                    if (width <= 0 || height <= 0 || width > 7680 || height > 4320)
                        return null;

                    if (stride == 0)
                        stride = (uint)(width * 4);

                    _width = width;
                    _height = height;
                    _stride = unchecked((int)stride);

                    int size = width * height * 4;
                    _pending = new byte[size];
                    _write = new byte[size];
                }

                _hardware = hardware;
                return reader;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                return null;
            }
            finally
            {
                if (manager != null)
                    manager.Dispose();
                if (attributes != null)
                    attributes.Dispose();
                if (device != null)
                    device.Dispose();
            }
        }

        private void ReadLoop(IMFSourceReader reader)
        {
            Stopwatch clock = Stopwatch.StartNew();

            while (_running)
            {
                IMFSample sample = reader.ReadSample(
                    SourceReaderIndex.FirstVideoStream,
                    (SourceReaderControlFlag)0,
                    out int actualStreamIndex,
                    out SourceReaderFlag flags,
                    out long timestamp);

                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                    break;

                if (sample == null)
                    continue;

                // 按媒体时间戳节流，避免一次性解码整段视频。
                long dueMilliseconds = timestamp / 10000;
                long elapsed = clock.ElapsedMilliseconds;
                if (dueMilliseconds > elapsed)
                {
                    long wait = dueMilliseconds - elapsed;
                    if (wait > 200)
                        wait = 200;
                    Thread.Sleep((int)wait);
                }

                using (sample)
                using (IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer())
                {
                    buffer.Lock(out IntPtr data, out int maxLength, out int currentLength);

                    try
                    {
                        int rowBytes = _width * 4;
                        if (currentLength >= rowBytes * _height && _write != null)
                        {
                            for (int y = 0; y < _height; y++)
                            {
                                IntPtr sourceRow = _stride >= 0
                                    ? IntPtr.Add(data, y * _stride)
                                    : IntPtr.Add(data, (_height - 1 - y) * (-_stride));
                                Marshal.Copy(sourceRow, _write, y * rowBytes, rowBytes);
                            }

                            PublishFrame();
                        }
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }
            }
        }

        private void PublishFrame()
        {
            lock (_frameGate)
            {
                if (_pending == null || _write == null)
                    return;

                // 主线程还没取走上一帧时，直接丢弃旧帧，永远只保留最新一帧。
                if (_hasFrame)
                    _droppedFrames++;

                byte[] swap = _pending;
                _pending = _write;
                _write = swap;
                _pendingWidth = _width;
                _pendingHeight = _height;
                _hasFrame = true;
            }
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();
    }
}
