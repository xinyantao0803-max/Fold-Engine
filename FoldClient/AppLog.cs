using System;
using System.IO;

namespace FoldClient
{
    internal static class AppLog
    {
        private static readonly object Gate = new object();
        private static string _file;

        public static void Init()
        {
            _file = Path.Combine(AppContext.BaseDirectory, "FoldClient.log");
        }

        public static void Write(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(_file))
                    Init();

                lock (Gate)
                {
                    File.AppendAllText(
                        _file,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不影响渲染
            }
        }
    }
}
