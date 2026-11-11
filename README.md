# Fold

**SCP: Secret Laboratory 的 OS 级服务器统筹框架**

> Orchestrate your server like an operating system — modules, timelines, and media as first-class citizens.

[![Platform](https://img.shields.io/badge/platform-SCP%3ASL-1f1f1f)](#)
[![Server](https://img.shields.io/badge/server-LabAPI%201.1.7-6a5acd)](#)
[![Client](https://img.shields.io/badge/client-BepInEx%205%20%2F%20HarmonyX-4b8bbe)](#)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-black)](LICENSE)

> **安全认证（ECDSA）**：所有面向客户端的媒体指令必须由 FoldEdge 持有的 ECDSA P-256 私钥签名；FoldClient 内置对应公钥验签，并拒绝签名不匹配、超过 3 秒或 nonce 重复的指令。必须明确：这能阻止第三方伪造“官方签发”的媒体指令，但**不能**阻止被篡改的客户端读取游戏内存。本项目不宣称“物理上不可能”或“绝对无法用于 ESP”。

---

## Security & Anti-Cheat

FoldClient is a **passive, ECDSA-authenticated media renderer**:

- Only media commands signed by FoldEdge's ECDSA P-256 private key are rendered; forged, expired (>3s) or replayed commands are dropped.
- It does **not** read game memory, entity coordinates, player data or gameplay logic. Every image/video/audio/subtitle is a string explicitly provided by the server.
- It does **not** modify the game executable and does **not** bypass any anti-cheat mechanism.

**Honest scope:** this makes unauthorized media injection impractical, but it does **not** make a modified client incapable of ESP or other cheats — no client-side plugin can guarantee that, and we do not claim it. What we guarantee is that **this plugin's own attack surface is UI rendering only**.

**EULA:** FoldClient is a UI-only renderer that does not alter gameplay. Server owners are solely responsible for the content they upload, distribute or display.

### Whitelisting & Distribution Transparency

**What exists today.** `FoldClient.IL2CPP` is a BepInEx 6 (IL2CPP) plugin. The current development build is loaded by the standard BepInEx 6 loader. There is **no custom "Fold Launcher" in this repository yet**, and we will not describe the development build as a closed sandbox.

**What we are proposing for whitelisted distribution.** If Northwood approves a dedicated component, we intend to ship a minimal loader that embeds only the signed FoldClient assembly and exposes no plugin directory or third-party DLL loading path. The loader would verify the embedded assembly's ECDSA signature before loading. This is a **design proposal, not a shipped capability**; it must be reviewed before it is claimed.

**What this can and cannot do.** ECDSA verification ensures that only media commands signed by FoldEdge are rendered by FoldClient. It does **not**, and cannot, make a modified game client incapable of ESP or other cheats. A custom loader can restrict what *our* loader loads, not what a determined attacker does to their own client. We do not claim "physically impossible".

**Review request.** We invite Northwood's security team to review the source in this repository, especially the ECDSA verification path (`FoldClient.IL2CPP/MediaVerifier.cs`) and the media-only renderer (`FoldClient.IL2CPP/FoldOverlay.cs`). We are happy to answer questions or adjust the design before distribution.

---

## 1. 架构

Fold 不是一个插件，而是**由三个进程/层组成的一套运行时**。它把「内容（.fdll 模块）」「调度（时间轴）」与「渲染（客户端）」彻底解耦。

```text
┌──────────────────────────────────────────────────────────────────────┐
│                        FoldEdge  (外部主程序, .NET 8)                 │
│                        ASP.NET Core · 127.0.0.1:9000                 │
│                                                                      │
│   Modules/*.fdll  (ZIP)                                              │
│        │                                                             │
│        ├─ manifest.json   ──► tags → 合规标签                        │
│        ├─ timeline.json   ──► 事件时间轴                             │
│        └─ assets/         ──► 模块解析 / 媒体签名 (ECDSA)             │
│                                                                      │
│   HTTP API  ── /api/status · /api/trigger/{module} · /api/image       │
└───────────────────────────────┬──────────────────────────────────────┘
                                │  WebSocket (ws://127.0.0.1:9000/ws)
                                │  JSON 帧：image-frame / command
┌───────────────────────────────▼──────────────────────────────────────┐
│                    FoldEngine  (游戏内服务端插件, net48)              │
│                    LabAPI 1.1.7 · 纯服务端 · 零 Unity 依赖            │
│                                                                      │
│   manifest.tags ──► [18+] / [GORE] / [FLASH] / [POLITICS] 服务器名前缀│
│   timeline      ──► 时间轴调度 (async / Task.Delay)                  │
│   动作          ──► 文本广播 / [FOLD_IMG]url / [FOLD_VIDEO]url        │
└───────────────────────────────┬──────────────────────────────────────┘
                                │  游戏网络广播 (TMP 文本)
┌───────────────────────────────▼──────────────────────────────────────┐
│                   FoldClient  (客户端插件, BepInEx 5 Mono / 6 IL2CPP) │
│                   Harmony 拦截 TMP_Text.text / Broadcast.RpcAddElement│
│                                                                      │
│   [FOLD_IMG]url   ──► UnityWebRequestTexture → RawImage 全屏          │
│   [FOLD_VIDEO]url ──► VideoPlayer(RenderTexture) + AudioSource 原声   │
│   F9                ──► 立即停止播放并销毁全屏画布                    │
└──────────────────────────────────────────────────────────────────────┘
```

**为什么客户端要参与？** 原生 SCP:SL 服务端只能下发纯文本网络消息，无法传输贴图/视频。Fold 的做法是：服务端只负责发**标记文本**（`[FOLD_VIDEO]https://...`），装了 FoldClient 的玩家在本地把它还原成真正的全屏画面；未装模组的玩家只会看到一行标记文本，不影响游戏公平性。

---

## 2. 特性

| 特性 | 说明 |
| --- | --- |
| **模块化内容** | `.fdll` 是自包含 ZIP 包（manifest / timeline / subtitles / assets），可独立分发 |
| **完整时间轴动作** | `SHOW_TEXT` / `SHOW_IMAGE` / `PLAY_VIDEO` / `PLAY_AUDIO` / `CHANGE_ROLE` / `SPAWN_ITEM` / `CONTROL_DOOR` / `CLEAR` |
| **服务端游戏动作** | `CHANGE_ROLE` / `SPAWN_ITEM` / `CONTROL_DOOR` 直接调用 LabAPI 原生包装器，无需客户端装模组 |
| **服务端全局音频** | `PLAY_AUDIO_SERVER` 用 LabAPI `SpeakerToy` 播放非空间音效，WAV 自动重采样到 48 kHz |
| **真实图片渲染** | 服务端只下发 `[FOLD_IMG]` 标记，客户端全屏渲染；服务端不再拼接任何字符画 |
| **全屏视频** | FoldClient 用 `VideoPlayer` + `RenderTexture` 播放 MP4/WebM，带原声，F9 急停（Mono 与 IL2CPP 双版本） |
| **客户端音频 / 字幕** | `[FOLD_AUDIO]` 走 `UnityWebRequestMultimedia` 2D 播放，`[FOLD_TEXT]` 用 TMP 在媒体下方渲染字幕 |
| **媒体指令验签** | FoldEdge 用 ECDSA P-256 私钥签名，FoldClient 内置公钥验签，拒绝伪造、超过 3 秒或重放的指令 |
| **定向播放** | timeline 支持 `target: all / role:Scp3114 / player:<SteamID64>`，只让目标玩家看到媒体标记 |
| **媒体预处理** | `POST /api/media/upload` 上传视频，FFmpeg 自动转 1080p / ~3Mbps MP4，生成可访问 URL |
| **一键发布** | `POST /api/module/publish` 把视频 URL、海报与时间轴打包成 `.fdll` |
| **存储策略** | `MediaStorageStrategy = Local / CDN`，Local 内置 `/Assets` 静态文件服务，CDN 使用外链 |
| **遥测与控制** | FoldClient 每秒上报播放状态；`/api/control/stop`、`/api/control/jump/{sec}` 下发 `[FOLD_CMD]` |
| **合规标签强制注入** | manifest 含 `18+`/`GORE` 时，服务器名自动加前缀，重载后依然生效 |
| **AES 加密模块** | `.fdll` 支持 `FOLDENC1`（AES-256-CBC）加密，主程序与插件均在内存解密，不落盘 |
| **零第三方依赖（服务端）** | 插件仅依赖 LabAPI，手写 JSON 解析，不引入 `System.Web.Extensions` 等易崩程序集 |
| **i18n** | FoldEdge 内置 `locales/zh-CN.json` / `en-US.json` |
| **热更新感知** | FoldEdge 用 `FileSystemWatcher` 监控 `Modules/*.fdll` |

---

## 3. 快速开始

### 3.1 启动主程序 FoldEdge

```bash
cd FoldEdge
dotnet run
# [FoldEdge] 服务已启动
# [FoldEdge] 已开始监控模块目录: .../FoldEdge/Modules
```

### 3.2 安装插件

```text
服务端：<游戏根目录>/SCP Secret Laboratory/LabAPI/plugins/global/FoldEngine.dll
客户端：<游戏根目录>/BepInEx/plugins/FoldClient.dll（旧版 Mono）或 FoldClient.IL2CPP.dll（Unity 6 IL2CPP）
```

> LabAPI 插件目录以 `LabApi-2000.yml` 中的 `plugin_paths` 为准（默认为 `global` 与端口目录）。
> 本地编译 FoldEngine 前，需把游戏 `SCPSL_Data/Managed` 下的 `Assembly-CSharp.dll`、`CommandSystem.Core.dll`、`UnityEngine.CoreModule.dll`、`Mirror.dll` 复制到 `FoldEngine/libs/`（该目录不入库）。

### 3.3 游戏内指令

```text
fold                         内置文本测试播放
fold image <url|test|smiley>  全屏图片（FoldEdge 签名）
fold video <url>              全屏视频（mp4 / webm）
fold audio <url> [volume]     全屏 2D 音频（ogg / mp3）
fold text <字幕>              底部字幕
fold module <name>            让 FoldEdge 读取 Modules/<name>.fdll 并播放
fold tags <18+,GORE>          手动注入合规标签前缀（验证注入逻辑）
```

---

## 4. `.fdll` 模块

完整规范见 [docs/fdll-spec.md](docs/fdll-spec.md)。一个最小模块：

```text
demo.fdll  (ZIP)
├── manifest.json
└── timeline.json
```

`manifest.json`

```json
{
  "id": "com.fold.demo",
  "name": "demo",
  "version": "1.0.0",
  "author": "Fold Team",
  "engineVersion": ">=1.0.0",
  "tags": ["18+", "GORE", "INTENSE"]
}
```

`timeline.json`（裸数组或 `{ "entries": [...] }` 均可）

```json
[
  { "time": 0, "action": "SHOW_TEXT",  "text": "Fold Edge 模块加载成功" },
  { "time": 2, "action": "SHOW_IMAGE", "url": "smiley" },
  { "time": 4, "action": "CLEAR" }
]
```

### 4.1 时间轴动作（与实现一致）

| 动作 | 目标 | 参数 | 说明 |
| --- | --- | --- | --- |
| `SHOW_TEXT` | 服务端 | `text`（或 `url`）、`duration` | 普通文本广播 |
| `SHOW_SUBTITLE` | 客户端 | `text`、`duration` | 媒体下方的 TMP 字幕（`[FOLD_TEXT]`） |
| `SHOW_IMAGE` | 客户端 | `url`（或 `test` / `smiley`） | 全屏图片（`[FOLD_IMG]`，签名） |
| `PLAY_VIDEO` | 客户端 | `url` | 全屏视频 MP4 / WebM（`[FOLD_VIDEO]`，签名） |
| `PLAY_AUDIO` | 客户端 | `url`（OGG / MP3）、`volume` | 2D 音频播放（`[FOLD_AUDIO]`，签名） |
| `PLAY_AUDIO_SERVER` | 服务端 | `url`（WAV）、`volume`、`loop` | SpeakerToy 全局非空间音效 |
| `CHANGE_ROLE` | 服务端 | `playerId`、`role` | 强制切换角色，如 `Scp096` |
| `SPAWN_ITEM` | 服务端 | `itemId`，`playerId` 或 `position` | 发放到背包或在地图坐标生成 |
| `CONTROL_DOOR` | 服务端 | `doorName`、`state` | `open` / `close` / `lock` / `unlock` / `toggle` |
| `CLEAR` | 客户端 | 无 | 清除当前覆盖层 |

> 客户端媒体指令由 FoldEdge 用 ECDSA P-256 签名，格式为 `[FOLD_XXX]<unix毫秒>|<nonce>|<签名>|<payloadJson>`；FoldEngine 原样透传，FoldClient 验签并拒绝超过 3 秒或 nonce 重放的指令。`role` / `itemId` 使用游戏枚举名（`PlayerRoles.RoleTypeId` / `InventorySystem.Items.ItemType`），大小写不敏感。

### 4.2 打包（含加密）

```powershell
# 明文 ZIP
.\tools\pack-fdll.ps1 -Manifest .\manifest.json -Timeline .\timeline.json -Output .\FoldEdge\Modules\demo.fdll

# AES-256-CBC 加密（FOLDENC1 容器）
.\tools\pack-fdll.ps1 -Manifest .\manifest.json -Timeline .\timeline.json -Output .\FoldEdge\Modules\demo.fdll -Encrypt
```

加密包由 FoldEdge 自动识别文件头并在内存解密；加密模块的播放指令以 `enc` 字段密文下发，插件在内存解密后再执行。预设密钥随引擎分发，属于**防随手解包**级别的混淆，不是强 DRM。

### 4.3 媒体预处理与分发

FoldEdge 自带 Web 面板（`http://127.0.0.1:9000/`）与以下 API：

| Method | Route | 说明 |
| --- | --- | --- |
| `POST` | `/api/media/upload` | 上传原始视频 → FFmpeg 转码 1080p / ~3Mbps MP4 → 返回 URL |
| `POST` | `/api/module/publish` | 视频 URL + 海报 + 时间轴 → 一键生成 `.fdll` |
| `POST` | `/api/control/stop` | 下发 `[FOLD_CMD] STOP`，客户端立即停止播放 |
| `POST` | `/api/control/jump/{seconds}` | 下发 `[FOLD_CMD] JUMP`，跳转到指定秒数 |
| `POST` | `/api/telemetry` | FoldClient 播放状态上报（HMAC 校验） |

存储策略在 [appsettings.json](file:///D:/SL-Fold-Server%20Mod/FoldEdge/appsettings.json) 中配置：

```json
{
  "ListenUrl": "http://127.0.0.1:9000",
  "MediaStorageStrategy": "Local",
  "PublicBaseUrl": "http://127.0.0.1:9000",
  "CdnBaseUrl": ""
}
```

- `Local`：FoldEdge 挂载 `/Assets` 静态文件服务，客户端直接从 `PublicBaseUrl/Assets/...` 下载。
- `CDN`：生成的 URL 使用 `CdnBaseUrl/Assets/...`；实际对象存储上传需由服主的外部同步工具完成（本项目不内置 OSS/S3 SDK）。

> **Flexible Media Distribution:** Server owners can choose between local hosting (zero cost, for small servers) or external CDN (for zero-lag, large scale servers).

### 4.4 定向播放

时间轴条目支持 `target`：

```json
{ "time": 5, "action": "SHOW_IMAGE", "url": "https://cdn/x.png", "target": "role:Scp3114" }
```

- `all`（默认）：全服广播
- `role:Scp3114`：只发给当前该角色的玩家
- `player:76561198000000000`：只发给指定玩家

> **Supports Targeted Media Playback** (e.g., playing a cinematic only for the player who spawned as SCP-3114, without interrupting other players' gameplay).

---

## 5. 合规标签（Disclosure, not censorship）

| 标签 | 注入前缀 | 含义 |
| --- | --- | --- |
| `18+` / `GORE` | `[18+]` | 成人主题、血腥或强烈视觉冲击 |
| `FLASH` | `[FLASH]` | 高频闪光（光敏性癫痫警告） |
| `POLITICS` | `[POLITICS]` | 争议性政治主题 |

注入点在 **服务端**：FoldEngine 收到 `manifest.json` 后解析 `tags` 并写入服务器列表名，玩家在服务器浏览器中**先看到标签再进入**。这是知情权披露，不做内容审查，也不为玩家无视标签后的行为承担责任。

---

## 6. HTTP API

| Method | Route | 说明 |
| --- | --- | --- |
| `GET` | `/api/status` | 运行状态、在线客户端数、已加载模块列表 |
| `POST` | `/api/trigger/{module}` | 读取 `Modules/{module}.fdll`，下发 manifest + timeline |
| `POST` | `/api/image` | 图片 URL（或 `test` / `smiley`）→ 像素矩阵（调试 API，客户端不再消费） |
| `GET` | `/ws` | WebSocket 接入点（FoldEngine 长连接） |

### 6.1 遥测与控制

- FoldClient 播放视频/图片/音频时，每秒向 `/api/telemetry` 上报 `{status, mediaType, url, currentTime}`；停止或按 F9 时上报 `{"status":"stopped"}`。
- FoldEdge 用 HMAC-SHA256 校验遥测，并拒绝超过 10 秒的包。
- `/api/control/stop` 与 `/api/control/jump/{seconds}` 会生成 ECDSA 签名的 `[FOLD_CMD]` 标记，经 FoldEngine 透传给客户端执行。

> 遥测目前直连 `127.0.0.1:9000`，适用于 FoldEdge 与客户端同机部署；远程客户端需要改 `TelemetryReporter.Endpoint` 或增加中继。HMAC 共享密钥只能防随意伪造，**不能证明客户端二进制身份**，README 不把它描述成客户端证明。

```bash
# 查询状态
curl http://127.0.0.1:9000/api/status

# 触发模块
curl -X POST http://127.0.0.1:9000/api/trigger/demo

# 推送一张真实图片（自动下载 → 8x6 降维）
curl -X POST http://127.0.0.1:9000/api/image ^
  -H "Content-Type: application/json" ^
  -d "{\"url\":\"https://example.com/avatar.png\"}"
```

---

## 7. 目录结构

```text
.
├── FoldEdge/          # 外部主程序（.NET 8 / ASP.NET Core / ImageSharp / i18n）
│   ├── locales/       # zh-CN.json / en-US.json
│   ├── Modules/       # .fdll 模块目录（运行时自动创建，不入库）
│   ├── Program.cs     # API + WebSocket + 模块读取 + AES 解密
│   ├── MediaSigner.cs # ECDSA P-256 签名 + 时间轴签名
│   ├── MediaPipeline.cs   # FFmpeg 转码 + 一键打包 .fdll
│   ├── TelemetryVerifier.cs # 遥测 HMAC 校验
│   ├── appsettings.json   # ListenUrl / MediaStorageStrategy / PublicBaseUrl / CdnBaseUrl
│   ├── wwwroot/        # Web 面板（上传 / 发布 / STOP / JUMP）
│   ├── Assets/         # Local 模式静态媒体（不入库）
│   ├── Media/Uploads/  # 上传原始文件（不入库）
│   ├── keys/          # 私钥/公钥（首次启动生成，不入库）
│   ├── ImageService.cs# 图片下载 / 解码 / 降维
│   └── Loc.cs         # 多语言
├── FoldEngine/        # 服务端插件（LabAPI 1.1.7 / net48）
│   ├── FoldCore.cs        # 插件入口 + fold 指令 + 合规标签
│   ├── FoldUIManager.cs   # 时间轴调度 + 媒体标记下发（无字符画）
│   ├── ActionExecutor.cs  # PLAY_AUDIO / CHANGE_ROLE / SPAWN_ITEM / CONTROL_DOOR
│   ├── FoldEdgeClient.cs  # WebSocket 客户端 + 指令处理
│   ├── FoldCrypto.cs      # AES-256-CBC 解密（与主程序同密钥）
│   └── MiniJson.cs        # 零依赖 JSON 解析
├── FoldClient/        # 客户端插件（BepInEx 5 + HarmonyX，旧版 Mono 客户端）
│   ├── FoldPlugin.cs      # 入口
│   ├── HintPatcher.cs     # TMP 文本拦截（[FOLD_IMG] / [FOLD_VIDEO]）
│   └── FoldUIRenderer.cs  # 全屏图片 / 视频渲染 + F9 急停
├── FoldClient.IL2CPP/ # 客户端插件（BepInEx 6 IL2CPP，Unity 6 客户端）
│   ├── FoldPlugin.cs      # BasePlugin 入口 + ClassInjector
│   ├── BroadcastPatcher.cs# 拦截 Broadcast.RpcAddElement（不依赖 TMP 劫持）
│   ├── MediaVerifier.cs   # ECDSA P-256 验签 + 3 秒时间窗 + nonce 防重放
│   ├── TelemetryReporter.cs # 播放状态遥测（HMAC）
│   └── FoldOverlay.cs     # 图片 / 视频 / 音频 / 字幕 + F9 急停
├── tools/
│   └── pack-fdll.ps1  # 模块打包（明文 / AES 加密）
└── docs/
    └── fdll-spec.md   # .fdll 1.0.0 规范
```

---

## 8. 客户端安全边界与免责声明

**定位**：FoldClient 是纯被动 UI 渲染器。它不读取玩家坐标、实体位置、背包或角色数据，不修改游戏内存与玩法逻辑；只接收服务端下发的字符串（`[FOLD_IMG]` / `[FOLD_VIDEO]` / `[FOLD_AUDIO]` / `[FOLD_TEXT]`），并调用 `VideoPlayer` / `RawImage` / `AudioSource` / `TextMeshPro` 完成展示。

**数据隔离**：雷达、玩家位置显示等能力必须由服务端或 FoldEdge 先生成图片/文字，再以 `[FOLD_IMG]` / `[FOLD_TEXT]` 下发。客户端只负责“像浏览器一样画出来”，不参与数据生成，也不接触实体数据。

**签名机制**：媒体指令使用 ECDSA P-256（SHA-256）+ Unix 毫秒时间戳 + 一次性 nonce。FoldEdge 私钥保存在 `FoldEdge/keys/fold-ecdsa-private.pem`（不入库），FoldClient 只内置公钥；签名不匹配、时间偏差超过 3 秒或 nonce 重复都会被拒绝。需要明确：公钥在客户端中，因此这套机制用于阻止第三方伪造“官方签发”的媒体指令和简单重放，**不是不可破解的强安全边界**，也无法阻止被篡改的客户端读取游戏内存。任何“physically impossible”或“绝对无法用于 ESP”的绝对化表述都不准确，本项目不这样宣称。

**内容责任**：FoldClient 仅提供渲染能力。服务器所有者对其上传、分发和展示的所有内容（包括图片、音频、视频、文字）承担全部法律责任与合规责任。Fold 开发团队不提供、不审核任何内容。

**反作弊**：本项目不修改游戏可执行文件，不绕过任何反作弊检测。客户端模组是否被允许由 Northwood 的政策决定，请在使用前自行确认。

---

## 9. Installation & Player Experience（安装与玩家体验）

### 9.1 分发模式

FoldClient 是专为官方白名单审查准备的**纯净媒体组件**，不包含任何玩法逻辑。白名单通过后，它的目标分发形态是**官方认可的独立组件**：玩家无需手动配置 BepInEx，也无需修改任何游戏文件。

> **当前真实状态**：开发版仍需要 BepInEx 6（IL2CPP），尚未获得 Northwood 官方认可。**目前不存在名为 “Fold Launcher” 的定制启动器**；它只是白名单通过后的分发提案。在收到正式许可前，请勿把本插件对外描述为“官方组件”或“封闭沙盒”。

### 9.2 玩家端安装

**目标形态（白名单通过后）**：通过官方认可的独立组件安装，或随游戏启动器更新自动获得；启用后无需任何配置。

**当前开发版**：

1. 安装 BepInEx 6（IL2CPP，win-x64）。
2. 把 `FoldClient.IL2CPP.dll` 放入 `BepInEx/plugins/`。
3. 启动游戏，确认 BepInEx 日志出现 `[FoldClient] IL2CPP 插件已加载`。

### 9.3 无插件降级体验（关键）

**没有安装 FoldClient 的玩家依然可以正常加入服务器并完整游玩。**

- 服务端的所有玩法动作（角色、物品、门禁、音频）都在服务端执行，与客户端插件无关。
- 未安装插件的玩家不会看到全屏 CG，只会看到服务器下发的一行普通文本标记（当前格式为 `[FOLD_VIDEO]...`），不会弹窗、不会卡顿、不会改变任何游戏状态。
- FoldClient 不读取游戏内存、不参与输入与判定，**不影响游戏公平性和可玩性**。

### 9.4 前置依赖

| 对象 | 需求 |
| --- | --- |
| 玩家客户端 | Windows 版 SCP:SL（当前为 Unity 6 / IL2CPP） |
| 当前开发版插件 | BepInEx 6 IL2CPP（自带 .NET 6 运行时） |
| 目标官方组件 | 无额外依赖，随组件/启动器安装 |
| 网络 | 能访问媒体 URL（Local `/Assets` 或 CDN） |
| 服务端 | FoldEdge + FoldEngine（用于签名与下发媒体指令） |

---

## 10. 版权与许可

- **引擎本体**：采用 [PolyForm Noncommercial License 1.0.0](LICENSE) —— 允许个人、服主、非营利组织**非商业**使用与修改；**禁止任何形式的换皮商用**。
- **`.fdll` 模块**：模块是独立作品，**版权 100% 归模块开发者**。Fold 官方**绝不抽成、绝不分成**，开发者可自由定价、销售或开源自己的模块。
- **免责**：本工具按「现状」提供，不对模块内容及使用方式承担责任。
