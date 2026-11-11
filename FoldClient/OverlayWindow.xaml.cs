using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FoldClient.MediaFoundation;

namespace FoldClient
{
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const int HotkeyStop = 1;
        private const int HotkeyExit = 2;
        private const int WM_HOTKEY = 0x0312;
        private const uint VK_F9 = 0x78;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly HttpClient _http = new HttpClient();
        private MfVideoDecoder _decoder;
        private WriteableBitmap _bitmap;
        private DateTime _lastFrameUtc;
        private DispatcherTimer _imageTimer;
        private IntPtr _hwnd;
        private bool _firstFrameLogged;

        public OverlayWindow()
        {
            InitializeComponent();

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            SourceInitialized += OnSourceInitialized;
            Closed += OnClosed;
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            long style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            style |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(style));

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            if (!RegisterHotKey(_hwnd, HotkeyStop, 0, VK_F9))
                AppLog.Write("RegisterHotKey(F9) failed");
            if (!RegisterHotKey(_hwnd, HotkeyExit, MOD_CONTROL | MOD_SHIFT, VK_F9))
                AppLog.Write("RegisterHotKey(Ctrl+Shift+F9) failed");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HotkeyStop)
                {
                    StopPlayback(true);
                    handled = true;
                }
                else if (id == HotkeyExit)
                {
                    Application.Current.Shutdown();
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        public void PlayVideo(string url)
        {
            try
            {
                StopPlayback(true);
                AppLog.Write("play video: " + url);

                var decoder = new MfVideoDecoder();
                decoder.Start(url);
                _decoder = decoder;
                _firstFrameLogged = false;

                Surface.Stretch = FvpConfig.KeepAspectRatio ? Stretch.Uniform : Stretch.Fill;
                Surface.Source = null;
                Root.Background = Brushes.Black;
                _lastFrameUtc = DateTime.UtcNow;

                AppLog.Write("video decoder starting");
            }
            catch (Exception ex)
            {
                AppLog.Write("play video failed: " + ex);
                StopPlayback(true);
            }
        }

        public async void PlayImage(string url)
        {
            try
            {
                StopPlayback(true);
                AppLog.Write("play image: " + url);

                byte[] bytes = await _http.GetByteArrayAsync(url);
                var image = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                }
                image.Freeze();

                Surface.Stretch = FvpConfig.KeepAspectRatio ? Stretch.Uniform : Stretch.Fill;
                Surface.Source = image;
                Root.Background = Brushes.Black;

                _imageTimer?.Stop();
                _imageTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(FvpConfig.ImageHoldSeconds)
                };
                _imageTimer.Tick += (s, e) =>
                {
                    _imageTimer.Stop();
                    StopPlayback(true);
                };
                _imageTimer.Start();
            }
            catch (Exception ex)
            {
                AppLog.Write("play image failed: " + ex);
                StopPlayback(true);
            }
        }

        public void StopPlayback(bool clear)
        {
            _imageTimer?.Stop();
            _imageTimer = null;

            var decoder = _decoder;
            _decoder = null;
            if (decoder != null)
            {
                try { decoder.Stop(false); }
                catch { /* 已在退出路径上 */ }
            }

            if (clear)
            {
                Surface.Source = null;
                _bitmap = null;
                Root.Background = Brushes.Transparent;
                AppLog.Write("playback stopped");
            }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            var decoder = _decoder;
            if (decoder == null)
                return;

            if (!string.IsNullOrEmpty(decoder.Error))
            {
                AppLog.Write("decoder error: " + decoder.Error);
                StopPlayback(true);
                return;
            }

            if (decoder.TryTakeFrame(out byte[] pixels, out int width, out int height))
            {
                if (_bitmap == null ||
                    _bitmap.PixelWidth != width ||
                    _bitmap.PixelHeight != height)
                {
                    _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                }

                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    AppLog.Write("first frame " + width + "x" + height +
                                 ", hardware=" + decoder.IsHardwareAccelerated);
                }

                _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
                Surface.Source = _bitmap;
                _lastFrameUtc = DateTime.UtcNow;
            }

            if (decoder.HasEnded &&
                (DateTime.UtcNow - _lastFrameUtc).TotalMilliseconds > 500)
            {
                StopPlayback(true);
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;

            if (_hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hwnd, HotkeyStop);
                UnregisterHotKey(_hwnd, HotkeyExit);
            }

            StopPlayback(true);
            _http.Dispose();
            Application.Current.Shutdown();
        }
    }
}
