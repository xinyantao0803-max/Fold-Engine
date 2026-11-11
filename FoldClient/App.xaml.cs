using System;
using System.Windows;

namespace FoldClient
{
    public partial class App : Application
    {
        private OverlayWindow _overlay;
        private FoldEdgeClient _edge;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppLog.Init();
            AppLog.Write("FoldClient starting");

            string edge = GetOption(e.Args, "--edge");
            if (!string.IsNullOrEmpty(edge))
                FvpConfig.EdgeUrl = edge;

            _overlay = new OverlayWindow();
            _overlay.Show();

            _edge = new FoldEdgeClient(FvpConfig.EdgeUrl, _overlay);
            _edge.Start();

            string video = GetOption(e.Args, "--test-video");
            string image = GetOption(e.Args, "--test-image");

            if (!string.IsNullOrEmpty(video))
                _edge.RequestMedia("video", video);
            else if (!string.IsNullOrEmpty(image))
                _edge.RequestMedia("image", image);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _edge?.Dispose();
            AppLog.Write("FoldClient exit");
            base.OnExit(e);
        }

        private static string GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }
    }
}
