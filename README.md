# Fold

**SCP: Secret Laboratory 的 OS 级服务器统筹框架**

> Orchestrate your server like an operating system — modules, timelines, and media as first-class citizens.

[![Platform](https://img.shields.io/badge/platform-SCP%3ASL-1f1f1f)](#)
[![Server](https://img.shields.io/badge/server-LabAPI%201.1.7-6a5acd)](#)
[![Client](https://img.shields.io/badge/client-BepInEx%205%20%2F%20HarmonyX-4b8bbe)](#)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-black)](LICENSE)

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
│        └─ assets/         ──► 图片解码 / 像素降维 (ImageSharp)        │
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
│                   FoldClient  (客户端插件, BepInEx 5)                │
│                   Harmony 拦截 TMP_Text.text setter                  │
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
| **服务端全局音频** | `PLAY_AUDIO` 用 LabAPI `SpeakerToy` 播放非空间音效，WAV 自动重采样到 48 kHz |
| **真实图片渲染** | 服务端不接触图片：FoldEdge 用 ImageSharp 解码并降维为像素矩阵，插件纯渲染 |
| **全屏视频** | FoldClient 用 `VideoPlayer` + `RenderTexture` 播放 MP4/WebM，带原声，F9 急停 |
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
客户端：<游戏根目录>/BepInEx/plugins/FoldClient.dll
```

> LabAPI 插件目录以 `LabApi-2000.yml` 中的 `plugin_paths` 为准（默认为 `global` 与端口目录）。
> 本地编译 FoldEngine 前，需把游戏 `SCPSL_Data/Managed` 下的 `Assembly-CSharp.dll`、`CommandSystem.Core.dll`、`UnityEngine.CoreModule.dll`、`Mirror.dll` 复制到 `FoldEngine/libs/`（该目录不入库）。

### 3.3 游戏内指令

```text
fold                         内置测试播放（文本 → 图片 → 清除）
fold image <url|test|smiley>  直接下发全屏图片（test / smiley 为零网络本地图）
fold video <url>              直接下发全屏视频（mp4 / webm）
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
| `SHOW_TEXT` | 客户端 | `text`（或 `url`）、`duration` | 文本广播 / 字幕 |
| `SHOW_IMAGE` | 客户端 | `url`（或 `test` / `smiley`） | 全屏图片 |
| `PLAY_VIDEO` | 客户端 | `url` | 全屏视频（MP4 / WebM） |
| `PLAY_AUDIO` | 服务端 | `url`（WAV）、`volume`、`loop` | SpeakerToy 全局非空间音效 |
| `CHANGE_ROLE` | 服务端 | `playerId`、`role` | 强制切换角色，如 `Scp096` |
| `SPAWN_ITEM` | 服务端 | `itemId`，`playerId` 或 `position` | 发放到背包或在地图坐标生成 |
| `CONTROL_DOOR` | 服务端 | `doorName`、`state` | `open` / `close` / `lock` / `unlock` / `toggle` |
| `CLEAR` | 客户端 | 无 | 清除当前覆盖层 |

> `PLAY_AUDIO` 仅支持 WAV（PCM 8/16/24/32 或 float32），内部混为单声道并重采样到 48000 Hz；MP3/OGG 请先转码。`role` / `itemId` 使用游戏枚举名（`PlayerRoles.RoleTypeId` / `InventorySystem.Items.ItemType`），大小写不敏感。

### 4.2 打包（含加密）

```powershell
# 明文 ZIP
.\tools\pack-fdll.ps1 -Manifest .\manifest.json -Timeline .\timeline.json -Output .\FoldEdge\Modules\demo.fdll

# AES-256-CBC 加密（FOLDENC1 容器）
.\tools\pack-fdll.ps1 -Manifest .\manifest.json -Timeline .\timeline.json -Output .\FoldEdge\Modules\demo.fdll -Encrypt
```

加密包由 FoldEdge 自动识别文件头并在内存解密；加密模块的播放指令以 `enc` 字段密文下发，插件在内存解密后再执行。预设密钥随引擎分发，属于**防随手解包**级别的混淆，不是强 DRM。

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
| `POST` | `/api/image` | 图片 URL（或 `test` / `smiley`）→ 像素矩阵 → 推送给插件 |
| `GET` | `/ws` | WebSocket 接入点（FoldEngine 长连接） |

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
│   ├── ImageService.cs# 图片下载 / 解码 / 降维
│   └── Loc.cs         # 多语言
├── FoldEngine/        # 服务端插件（LabAPI 1.1.7 / net48）
│   ├── FoldCore.cs        # 插件入口 + fold 指令 + 合规标签
│   ├── FoldUIManager.cs   # 时间轴调度 + 像素帧渲染 + 动作分发
│   ├── ActionExecutor.cs  # PLAY_AUDIO / CHANGE_ROLE / SPAWN_ITEM / CONTROL_DOOR
│   ├── FoldEdgeClient.cs  # WebSocket 客户端 + 指令处理
│   ├── FoldCrypto.cs      # AES-256-CBC 解密（与主程序同密钥）
│   └── MiniJson.cs        # 零依赖 JSON 解析
├── FoldClient/        # 客户端插件（BepInEx 5 + HarmonyX）
│   ├── FoldPlugin.cs      # 入口
│   ├── HintPatcher.cs     # TMP 文本拦截（[FOLD_IMG] / [FOLD_VIDEO]）
│   └── FoldUIRenderer.cs  # 全屏图片 / 视频渲染 + F9 急停
├── tools/
│   └── pack-fdll.ps1  # 模块打包（明文 / AES 加密）
└── docs/
    └── fdll-spec.md   # .fdll 1.0.0 规范
```

---

## 8. 版权与许可

- **引擎本体**：采用 [PolyForm Noncommercial License 1.0.0](LICENSE) —— 允许个人、服主、非营利组织**非商业**使用与修改；**禁止任何形式的换皮商用**。
- **`.fdll` 模块**：模块是独立作品，**版权 100% 归模块开发者**。Fold 官方**绝不抽成、绝不分成**，开发者可自由定价、销售或开源自己的模块。
- **免责**：本工具按「现状」提供，不对模块内容及使用方式承担责任。
