using System.Runtime.CompilerServices;

// 解码器/验签器保持 internal，只对 WPF 客户端与独立测试项目开放。
[assembly: InternalsVisibleTo("FoldClient")]
[assembly: InternalsVisibleTo("FoldClient.Test")]
