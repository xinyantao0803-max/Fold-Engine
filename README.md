# SCP:SL Fold Mod

**数据驱动的 SCP:SL 媒体与事件引擎。**

Fold Mod 由三个独立模块组成：**FH（FoldEdge）**、**FE（FoldEngine）**、**FVP（FoldClient）**。
它不依赖 BepInEx、不使用 C++ 注入器、不修改游戏内存，也不调用任何游戏内 Unity API。

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](#)
[![Client](https://img.shields.io/badge/client-WPF%20%2B%20Media%20Foundation-4b8bbe)](#)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-blue)](#license)

---

## 1. 架构总览

```
                    .fdll (ZIP)
        manifest.json + timeline.json + media
                          │
                          ▼
        ┌──────────────────────────────────┐
        │  FH · FoldEdge（外部主脑）        │
        │  解包 / 条件匹配 / 优先级队列      │
        │  ECDSA P-256 签名 / Web UI / WS   │
        └───────────────┬──────────────────┘
                        │ ws://127.0.0.1:9000/ws
          ┌─────────────┴─────────────┐
          ▼                           ▼
┌──────────────────────┐   ┌──────────────────────────┐
│ FE · FoldEngine      │   │ FVP · FoldClient         │
│ 服务端 LabAPI 空壳   │   │ 外部 WPF 覆盖层          │
│ 上报状态 / 执行动作  │   │ WS 收令 / 验签 / 硬解播放 │
│ 广播原生字幕与提示   │   │ 置顶 / 鼠标穿透 / F9     │
└──────────────────────┘   └──────────────────────────┘
```

- **FH 是唯一持有私钥的模块。** 所有媒体指令离开 FH 前都已完成签名。
- **FE 不持有任何密钥。** 它只上报状态、执行游戏内原生动作、把已签名的标记原样广播。
- **FVP 是独立进程。** 它直接连接 FH 的 WebSocket，验签后播放，不进入游戏进程。

---

## 2. 三个核心模块

### 2.1 FH · FoldEdge（主脑与规则引擎）

`FoldEdge/` · .NET 8 控制台服务

- 解包 `.fdll`（ZIP），读取 `manifest.json` 与 `timeline.json`
- 时间轴预下发：签名后的 JSON 由 FE/FVP 本地定时执行，减少网络抖动
- 轮询 FE 上报的游戏状态，进行条件匹配
- 优先级队列：多个条件同时触发时，按服主配置执行最高优先级事件
- 持有 ECDSA P-256 私钥，签名格式见第 4 节
- 防重放：3 秒时间窗 + nonce 去重
- 提供 Web UI、`/api/*` 管理接口与 `/ws` WebSocket
- 密钥保存在 `FoldEdge/keys/`，**已被 .gitignore 排除，禁止提交**

默认监听：`http://127.0.0.1:9000`  
WebSocket：`ws://127.0.0.1:9000/ws`

### 2.2 FE · FoldEngine（服务端空壳代理）

`FoldEngine/` · LabAPI 插件

FE 没有任何自主逻辑，是 FH 的提线木偶：

- **持续上报**：存活阵营、玩家状态、回合阶段等
- **被动执行**：接收 FH 指令，执行刷怪、换角色、控门、原生字幕/广播
- **转发媒体**：收到 FH 的媒体指令时，透传给对应客户端
- **零密钥**：不签名、不验签、不保存私钥

配置项 `FoldEdgeUrl` 指向 FH 的 `/ws`，默认 `ws://127.0.0.1:9000/ws`。

### 2.3 FVP · FoldClient（客户端渲染器）

`FoldClient/` · 纯 C# WPF 桌面程序

- 保留 `MfVideoDecoder`：Windows Media Foundation + DXVA 硬件解码
- 后台线程解码，UI 线程只做一次 `WriteableBitmap` 上传
- 全屏无边框、`Topmost`、鼠标穿透（`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`）
- 只接受 FH 签名的媒体 URL；公钥由 FH 在 WebSocket 握手时动态下发
- 本地家长控制：命中 `18+` / `GORE` 标签的内容直接拒绝
- **F9** 立即停止并销毁画面；**Ctrl+Shift+F9** 退出客户端
- 不处理字幕、不处理复杂逻辑

> 当前版本提供视频与图片渲染；音频播放尚未接入。  
> 游戏需使用**窗口化**或**无边框全屏**，独占全屏下任何覆盖层都无法显示。

---

## 3. 引擎与数据分离：`.fdll`

开发者不需要写代码。一个 `.fdll` 就是一个 ZIP：

```
my-module.fdll
├─ manifest.json     名称、版本、作者、优先级、强制标签
├─ timeline.json     时间轴与动作
└─ media/            视频、图片等素材
```

`timeline.json` 支持的动作：

| 动作 | 说明 |
| --- | --- |
| `PLAY_VIDEO` | 播放视频 URL |
| `SHOW_IMAGE` | 显示图片 URL |
| `PLAY_AUDIO` | 音频（预留） |
| `SHOW_SUBTITLE` | 原生字幕/广播 |
| `CONTROL` | 停止、跳转等控制 |

完整字段、时间轴格式、标签与优先级规则见 **[docs/fdll-spec.md](docs/fdll-spec.md)**。

---

## 4. 安全模型

### 4.1 签名格式

FH 生成的媒体标记：

```
[FOLD_VIDEO]<tsMs>|<nonce>|<base64url(DER签名)>|<payloadJson>
```

签名内容：

```
kind + "\n" + payloadJson + "\n" + timestampMs + "\n" + nonce
```

- 算法：ECDSA P-256 + SHA-256
- 时间窗：3 秒
- nonce：单次使用，重复即拒绝

### 4.2 动态公钥

FVP 不再硬编码公钥。连接 FH 后：

1. FH 在握手后主动推送 `{"type":"public-key","publicKey":"..."}`
2. FVP 也可发送 `{"type":"get_public_key"}` 主动索取
3. FVP 清理空白、校验 Base64 长度为 4 的倍数，动态创建 `ECDsa`

FH 换钥后，客户端无需重新编译。

### 4.3 边界

- 不注入游戏进程，不加载 BepInEx，不修改游戏内存
- FVP 只接收 FH 的 WebSocket 指令并渲染
- 本项目不提供、也不承诺任何反作弊绕过
- 远程部署请使用 `wss://` 或 VPN；明文 `ws://` 仅建议本机/内网使用

---

## 5. 安装与运行

### 前置条件

- Windows 10/11 x64
- .NET 8 Desktop Runtime
- SCP:SL 服务端（LabAPI 环境）
- 游戏使用窗口化或无边框全屏

### 5.1 启动 FH

```powershell
dotnet run --project .\FoldEdge\FoldEdge.csproj -c Release
```

首次启动会在 `FoldEdge/keys/` 生成 ECDSA 密钥对。私钥不会进入 Git。

### 5.2 安装 FE

```powershell
dotnet build .\FoldEngine\FoldEngine.csproj -c Release
```

将 `FoldEngine.dll` 及依赖放入 LabAPI 插件目录，配置 `FoldEdgeUrl` 后重启服务器。

### 5.3 启动 FVP

```powershell
dotnet build .\FoldClient\FoldClient.csproj -c Release
.\FoldClient\bin\Release\net8.0-windows\FoldClient.exe --edge ws://127.0.0.1:9000/ws
```

测试播放（客户端会向 FH 请求签名并立即播放）：

```powershell
.\FoldClient\bin\Release\net8.0-windows\FoldClient.exe `
  --edge ws://127.0.0.1:9000/ws `
  --test-video http://127.0.0.1:9000/Assets/your-video.mp4
```

也可以在服务器控制台触发：

```
fold video http://127.0.0.1:9000/Assets/your-video.mp4
fold image http://127.0.0.1:9000/Assets/your-image.png
```

`FoldEdge/Assets/` 与 `FoldEdge/Media/` 默认被 Git 忽略，请通过 FH 上传接口或手动放置素材。

---

## 6. 构建

> **编译前置**：`FoldEngine` 需要服务端提供的引用程序集：  
> - 将 `LabAPI.dll` 放到仓库根目录  
> - 将 `Assembly-CSharp.dll`、`CommandSystem.Core.dll`、`Mirror.dll`、`UnityEngine.CoreModule.dll` 放到 `FoldEngine/libs/`  
> 以上文件均被 `.gitignore` 排除，不随仓库分发。

```powershell
dotnet build .\FoldEdge\FoldEdge.csproj   -c Release
dotnet build .\FoldEngine\FoldEngine.csproj -c Release
dotnet build .\FoldClient\FoldClient.csproj -c Release
```

客户端解码器独立测试：

```powershell
dotnet run --project .\tests\FoldClient.Test\FoldClient.Test.csproj -c Release
```

---

## 7. 仓库结构

```
FoldEdge/               FH 主程序（规则引擎、解包、ECDSA 签名）
FoldEngine/             FE 服务端插件（LabAPI 空壳代理）
FoldClient/             FVP 客户端（WPF + Media Foundation）
└─ Core/                解码与验签核心库
docs/
└─ fdll-spec.md         .fdll 规范（时间轴、优先级、标签）
tests/
└─ FoldClient.Test/     解码器独立测试
tools/
└─ pack-fdll.ps1        .fdll 打包脚本
README.md
LICENSE                 PolyForm Noncommercial 1.0.0（PFN 1.0）
```

---

## 8. 社区规范与合规

- **强制标签**：包含 18+ 或 GORE 内容的 `.fdll` 必须在 `manifest.json` 中声明对应标签；FH 会把警告前缀注入服务器名。
- **本地家长控制**：FVP 默认拒绝 `18+` / `GORE` 标签内容，玩家可在本地关闭。
- **版权归属**：开发者 100% 拥有其 `.fdll` 的版权，官方 0 抽成。
- **内容责任**：开发者与服主对其 `.fdll` 中的媒体、文字与事件内容承担全部责任，包括素材授权。
- **禁止内容**：不得包含违法、仇恨、骚扰、未经授权的版权素材或任何作弊功能。
- **协议**：项目采用 **PFN 1.0（PolyForm Noncommercial 1.0.0）**，开源但禁止商业使用，详见 [LICENSE](LICENSE)。

---

## 9. 免责声明

- 本项目与 Northwood Studios 无隶属关系，未获得官方背书。
- 项目按白名单审查标准设计；在收到 Northwood 书面许可前，请勿将其描述为“官方组件”，也不要用于公共服务器分发。
- FVP 是独立外部覆盖层，不注入游戏进程，但**不保证**在所有显示模式、分辨率或反作弊环境下可用。
- 作者不对因使用本项目导致的封禁、数据丢失、内容纠纷或任何间接损失负责。
- 使用本项目即表示你已阅读并同意 [LICENSE](LICENSE) 与本免责声明。

---

## 10. License

**PolyForm Noncommercial License 1.0.0 (PFN 1.0)**  
https://polyformproject.org/licenses/noncommercial/1.0.0

完整文本见 [LICENSE](LICENSE)。
