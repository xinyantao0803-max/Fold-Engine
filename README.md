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
| **时间轴调度** | 按秒驱动的动作序列：`SHOW_TEXT` / `SHOW_IMAGE` / `PLAY_VIDEO` / `CLEAR` |
| **真实图片渲染** | 服务端不接触图片：FoldEdge 用 ImageSharp 解码并降维为像素矩阵，插件纯渲染 |
| **全屏视频** | FoldClient 用 `VideoPlayer` + `RenderTexture` 播放 MP4/WebM，带原声 |
| **合规标签强制注入** | manifest 含 `18+`/`GORE` 时，服务端名自动加前缀，重载后依然生效 |
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

`timeline.json`

```json
[
  { "time": 0, "action": "SHOW_TEXT",  "url": "Fold Edge 模块加载成功" },
  { "time": 2, "action": "SHOW_IMAGE", "url": "smiley" },
  { "time": 4, "action": "CLEAR",      "url": "" }
]
```

打包（Windows PowerShell）：

```powershell
Compress-Archive -Path manifest.json,timeline.json -DestinationPath demo.zip -Force
Move-Item demo.zip "FoldEdge\Modules\demo.fdll" -Force
```

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
│   ├── Program.cs     # API + WebSocket + 模块读取
│   ├── ImageService.cs# 图片下载 / 解码 / 降维
│   └── Loc.cs         # 多语言
├── FoldEngine/        # 服务端插件（LabAPI 1.1.7 / net48）
│   ├── FoldCore.cs        # 插件入口 + fold 指令 + 合规标签
│   ├── FoldUIManager.cs   # 时间轴调度 + 像素帧渲染 + 动作分发
│   ├── FoldEdgeClient.cs  # WebSocket 客户端 + 指令处理
│   └── MiniJson.cs        # 零依赖 JSON 解析
├── FoldClient/        # 客户端插件（BepInEx 5 + HarmonyX）
│   ├── FoldPlugin.cs      # 入口
│   ├── HintPatcher.cs     # TMP 文本拦截（[FOLD_IMG] / [FOLD_VIDEO]）
│   └── FoldUIRenderer.cs  # 全屏图片 / 视频渲染 + F9 急停
└── docs/
    └── fdll-spec.md   # .fdll 1.0.0 规范
```

---

## 8. 版权与许可

- **引擎本体**：采用 [PolyForm Noncommercial License 1.0.0](LICENSE) —— 允许个人、服主、非营利组织**非商业**使用与修改；**禁止任何形式的换皮商用**。
- **`.fdll` 模块**：模块是独立作品，**版权 100% 归模块开发者**。Fold 官方**绝不抽成、绝不分成**，开发者可自由定价、销售或开源自己的模块。
- **免责**：本工具按「现状」提供，不对模块内容及使用方式承担责任。
