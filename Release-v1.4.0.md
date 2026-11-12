# Fold Mod v1.4.0 — Release Integrity Verification

本文件记录 **v1.4.0** 最终构建产物的 SHA-256 校验值，供官方审查与下载后自行复核。

## 构建信息

| 项目 | 值 |
|---|---|
| 版本 | `v1.4.0` |
| 构建提交 | `5f53480` (`release: v1.4.0 silent SCP:SL server lifecycle control`) |
| 构建时间 | 2026-11-12 |
| 操作系统 | Windows |
| .NET SDK | `8.0.428` |
| 构建配置 | `Release` |
| 警告策略 | `-warnaserror` |
| 构建结果 | **0 警告 / 0 错误** |

> 仓库未包含 `.sln`，本次按 README 的逐项目命令完成全量构建。`FoldNativeClient` 使用本机 SCP:SL `Managed` 目录作为 Unity 引用程序集，仅做语法检查，不产出交付 DLL。

## 构建命令

```powershell
dotnet build .\FoldEdge\FoldEdge.csproj         -c Release -warnaserror
dotnet build .\FoldEngine\FoldEngine.csproj     -c Release -warnaserror
dotnet build .\FoldClient\FoldClient.csproj     -c Release -warnaserror
dotnet build .\tools\FoldPacker\FoldPacker.csproj -c Release -warnaserror
dotnet build .\tests\FoldClient.Test\FoldClient.Test.csproj -c Release -warnaserror

dotnet build .\FoldNativeClient\FoldNativeClient.csproj -c Release -warnaserror `
  -p:UNITY_MANAGED_DIR="F:\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed"
```

## 核心产物 SHA-256

| 组件 | 文件 | 大小（字节） | SHA-256 |
|---|---|---:|---|
| FH 主程序 | `FoldEdge/bin/Release/net8.0/FoldEdge.exe` | 151552 | `afb532aa9b644d9651c2178dc0b1d9203c225c080231496d64f1b5af12aa3509` |
| FH 主程序 | `FoldEdge/bin/Release/net8.0/FoldEdge.dll` | 165376 | `78ea9c8f0fe8986af5f76eb86d39b36d8362fd0121ccc496b92dab1187cca010` |
| FE 服务端插件 | `FoldEngine/bin/Release/net48/FoldEngine.dll` | 39936 | `f1f33b788f81cf2b5c0ca9522b8c31b5c3e1142d71d90d6e3a0352059fba2f82` |
| FVP 客户端 | `FoldClient/bin/Release/net8.0-windows/FoldClient.exe` | 151552 | `e3020407470033f587a67e8ac07155b7357dfb04eee0fa44df3ce6dbe2ac446b` |
| FVP 客户端 | `FoldClient/bin/Release/net8.0-windows/FoldClient.dll` | 24576 | `d5b49ec83dffc911e99f8e5494e82277cae51fd050ed25c44f89959e723573c2` |
| FVP 核心库 | `FoldClient/bin/Release/net8.0-windows/FoldClient.Core.dll` | 16896 | `16404bf9ea4f85db405b73fca07d045df1ba4de0fe49b994630b7f550474f855` |
| Packer 打包工具 | `tools/FoldPacker/bin/Release/net8.0/FoldPacker.exe` | 151552 | `2252bbf6b6c782c8d401ea56608f73cdcddf486a31fd4797f38156d8071a25fd` |
| 原生模块语法检查 | `FoldNativeClient/bin/Release/netstandard2.1/FoldNativeClient.dll` | 19968 | `91ad4db66f2a3d707fc2db04b0439d4db7e47dda33ff73114ebf358caf4220a9` |

## FoldNativeClient 源码校验

`FoldNativeClient` 以源码合入官方 Unity 工程，不依赖上表中的 CI 检查 DLL。以下为五个运行时脚本的 SHA-256：

| 文件 | 大小（字节） | SHA-256 |
|---|---:|---|
| `FoldNativeClient/FoldNativeRuntime.cs` | 3159 | `837ae1b32b1cd1734c9b09b8d04ed50f2e4fa1f6eb4c8800d36e3d5ca8109922` |
| `FoldNativeClient/FoldNativeUI.cs` | 8734 | `28771b2394a6237752f045b6d3922b50ccb0ffcbd5bca9388061a53b55feb21c` |
| `FoldNativeClient/SignedMediaCommand.cs` | 13502 | `d52b58174953de018fa34fc3ccc192c3d1e8cb1a84cebb827dbde8ae18b785db` |
| `FoldNativeClient/FoldCommandReceiver.cs` | 3681 | `1a5e2d2f8941f3f621cc64c381a5b2b3b891603682ca00dbb9125646d575777f` |
| `FoldNativeClient/FoldNativeConfig.cs` | 1914 | `ce64c6b720adf63f1ea5db7558427f93091599e823d7bd14ffa428e8a6d5de94` |

> `FoldNativeClient` 的五个运行时 `.cs`、`FoldNativeClient.asmdef` 与 `FoldNativeClient.csproj` 均已纳入 Git；仅 `bin/`、`obj/` 被 `.gitignore` 排除。

## 核心目录结构

```text
SL-Fold-Server Mod/
├─ FoldEdge/                  FH 主程序（规则引擎、Web UI、签名）
│  ├─ Security/               用户库、PBKDF2、JWT、VPN 凭证
│  ├─ Logging/                统一日志中心
│  ├─ Server/                 服务端生命周期
│  └─ wwwroot/                /dashboard 控制平面
├─ FoldEngine/                FE 服务端插件
│  └─ libs/                   本地编译引用（不入库）
├─ FoldClient/                FVP 客户端
│  └─ Core/                   解码与验签核心库
├─ FoldNativeClient/          FVP-Native 原生合入代码（供官方）
├─ tools/
│  └─ FoldPacker/             .fdll 打包工具
├─ tests/
│  └─ FoldClient.Test/        解码器独立测试
├─ docs/                      文档
├─ docs-site/                 MkDocs 文档站
├─ FoldStudio/                独立编辑器规划
├─ .github/workflows/         CI
├─ README.md
├─ NW-REVIEW.md
├─ Release-v1.4.0.md
└─ LICENSE
```

> 以上仅显示核心层级；各项目下的 `bin/`、`obj/` 为构建产物，不纳入版本控制。

## 下载后如何验证

Windows PowerShell：

```powershell
Get-FileHash .\FoldClient.exe -Algorithm SHA256
Get-FileHash .\FoldEngine.dll -Algorithm SHA256
Get-FileHash .\FoldEdge.exe -Algorithm SHA256
Get-FileHash .\FoldPacker.exe -Algorithm SHA256
```

Linux / macOS：

```bash
sha256sum FoldClient.exe FoldEngine.dll FoldEdge.exe FoldPacker.exe
```

将输出与上表逐项比对，即可确认文件未被篡改。
