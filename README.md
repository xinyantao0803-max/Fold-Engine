**Commercial License Granted: While this project uses PFN 1.0 (Noncommercial), we officially and irrevocably grant Northwood Studios a free commercial license to integrate this client rendering code into the official SCP:SL source code.**

# SCP:SL Fold Mod

**数据驱动的 SCP:SL 媒体与事件引擎。**

Fold Mod 由三个运行模块组成：**FH（FoldEdge）**、**FE（FoldEngine）**、**FVP（FoldClient）**；同时提供可直接合入官方客户端的 **FVP-Native（FoldNativeClient）** 原生模块。
它不依赖 BepInEx、不使用 C++ 注入器、不修改游戏内存，也不调用任何游戏内 Unity API。

**Not just a media player, but a Zero-Trust network protocol reference for SCP:SL.**

Fold 的签名信封（`[FOLD_*]<ts>|<nonce>|<sig>|<payload>`）不仅保护播放指令，也被整理为一份可供官方借鉴的零信任网络扩展方案：见 [docs/nw-security.md](docs/nw-security.md) 与 [给 NW 的邮件草稿](docs/nw-email-draft.md)。

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](#)
[![Version](https://img.shields.io/badge/version-v1.3-22d3ee)](#)
[![Web UI](https://img.shields.io/badge/web%20ui-%2Fdashboard-8b5cf6)](#)
[![Client](https://img.shields.io/badge/client-WPF%20%2B%20Media%20Foundation-4b8bbe)](#)
[![Native](https://img.shields.io/badge/native-Unity%20C%23%20module-22d3ee)](#)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-blue)](#license)

文档中心：https://xinyantao0803-max.github.io/Fold-Engine/ · 官方审查指南：[NW-REVIEW.md](NW-REVIEW.md)

---

## 🌟 两套接入方案 (Two Integration Solutions)

> **方案 A 现已可用：服主无需修改游戏本体。**  
> **方案 B 已写入本仓库：Northwood 可直接把源码并入官方客户端。**

### 方案 A：外部独立渲染器 (External Renderer — Zero Injection)

> **适用场景**：现阶段服主即刻可用，无需修改游戏本体。  
> **核心特性**：独立的 WPF 进程，通过 Windows MF 原生硬解全屏播放。**SL-AC 绝对安全，绝不碰游戏内存。**  
> **获取方式**：下载我们的 **FoldClient** 即可使用。

- **代码位置**：[`FoldClient/`](FoldClient/)
- **渲染路径**：Media Foundation + DXVA → `WriteableBitmap` → 全屏置顶、鼠标穿透的 WPF 覆盖层
- **边界**：不注入进程、不加载 BepInEx、不调用任何游戏内 Unity API

### 方案 B：原生引擎集成代码 (Native Engine Integration — For NW Studio)

> **适用场景**：专为 Northwood 官方准备的终极形态。**直接随游戏下发与更新。**  
> **核心特性**：这绝对不是外部程序。这是一套**纯正的 C# 原生代码模块**，官方可以直接将源码合并入 SCP:SL 的客户端源码中。  
> **渲染方式**：在游戏引擎内部，直接调用 Unity 的最高层 UI `Canvas`（`sortingOrder = 9999`）进行原生强渲染。不仅性能达到极限，而且能完美与游戏内置语音、音频系统融合。  
> **优势**：玩家不需要开任何外部软件，官方不需要维护额外进程，彻底改变 SCP:SL 的剧情表现力。

- **查看原生集成代码**：[`FoldNativeClient/`](FoldNativeClient/)
- **官方合入指南**：[`NW-REVIEW.md`](NW-REVIEW.md) · [`docs/native-integration.md`](docs/native-integration.md)
- **渲染路径**：`Canvas` → `RawImage` → `VideoPlayer` + `RenderTexture` / `AudioSource`

### 双轨对照 (At a Glance)

| | 方案 A · External Renderer | 方案 B · Native Integration |
| --- | --- | --- |
| 运行形态 | 独立 WPF 进程 | 游戏内 Unity C# 脚本 |
| 是否修改游戏 | 否 | 是（源码级合入） |
| 渲染层 | 外部置顶覆盖层 | 引擎内 `Canvas`（9999） |
| 注入 / Hook | **零注入** | 无（官方自行编译） |
| 音频融合 | 系统混音 | Unity `AudioSource` 原生融合 |
| 适用对象 | 服主 / 社区 | Northwood Studios |
| 代码 | [`FoldClient/`](FoldClient/) | [`FoldNativeClient/`](FoldNativeClient/) |

**两套方案共用同一套 ECDSA 签名信封**（`[FOLD_*]<ts>|<nonce>|<sig>|<payload>`）：服务端只需签名一次，任一客户端实现都可验签执行。

---

## 🛡 v1.3 企业控制平面 (Enterprise Control Plane)

> **FoldEdge 现在不只是黑框框：内置暗黑赛博朋克 Web 管理面板、RBAC、统一日志中心、限时权限与 VPN 凭证生命周期。**

- **Web UI**：`/dashboard`，原生 JS + 内联 CSS，**零 CDN 依赖**；登录、初始化、加载动画、错误 Toast 全链路兜底，不会白屏
- **首次初始化**：强制创建 Owner 账号；密码使用 **PBKDF2-SHA256（120,000 次迭代 + 随机盐）**
- **RBAC**：`Owner`（全部权限）/ `Admin`（触发模块、播放控制、查看日志）/ `Viewer`（只读）
- **会话**：HS256 JWT；服务端校验 `TokenVersion`，吊销或到期后旧令牌立即失效
- **统一日志中心**：按天切割、保留最近 7 天、实时面板、**中英双语**
- **限时权限**：创建账号可设 10 分钟 / 1 小时 / 24 小时 / 自定义；到期自动吊销 JWT、禁用 VPN、通知 FE 收回游戏权限并执行 `RoundRestart`
- **VPN 凭证**：一键生成 L2TP / IKEv2 凭证与 Windows 原生 `Add-VpnConnection` 脚本；网关可通过 `/api/vpn/verify` 实时校验
- **一键部署**：`./start.sh` 构建并启动容器，终端打印 Web 访问地址

> **VPN 说明**：FoldEdge 负责凭证签发、校验与吊销；实际隧道由服主配置的 L2TP / IKEv2 网关承载，FoldEdge 不代理流量。

入口文件：[`FoldEdge/wwwroot/dashboard.html`](FoldEdge/wwwroot/dashboard.html) · 认证与 RBAC：[`FoldEdge/Security/`](FoldEdge/Security/) · 日志：[`FoldEdge/Logging/LogService.cs`](FoldEdge/Logging/LogService.cs)

---

## FoldStudio (FS)

**FoldStudio (FS) 是完全独立、基于 MIT 协议开源的多媒体创作工具，不属于本仓库，也不受 PFN 1.0 约束。**  
它支持导出通用格式（MP4 / PNG 等）以及 `.fdll` 模块；Fold Mod 只把 `.fdll` 当作数据读取。  
完整的产品形态、界面布局与里程碑见 **[FoldStudio/ROADMAP.md](FoldStudio/ROADMAP.md)**。

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

## 2. 核心模块

### 2.1 FH · FoldEdge（主脑与规则引擎）

`FoldEdge/` · .NET 8 控制台服务

- 解包 `.fdll`（ZIP），读取 `manifest.json` 与 `timeline.json`
- 时间轴预下发：签名后的 JSON 由 FE/FVP 本地定时执行，减少网络抖动
- 轮询 FE 上报的游戏状态，进行条件匹配
- 优先级队列：多个条件同时触发时，按服主配置执行最高优先级事件
- 持有 ECDSA P-256 私钥，签名格式见第 4 节
- 防重放：3 秒时间窗 + nonce 去重
- 提供 Web UI、`/api/*` 管理接口与 `/ws` WebSocket
- **Web 管理面板 `/dashboard`**：FE / FVP 在线数、`Modules/*.fdll` 列表与 TRIGGER、限流开关与日志级别
- **企业控制平面**：Owner / Admin / Viewer RBAC、PBKDF2 密码哈希、JWT 会话、实时双语日志、限时权限与 VPN 凭证
- 密钥保存在 `FoldEdge/keys/`，**已被 .gitignore 排除，禁止提交**；用户库与会话密钥在 `FoldEdge/Data/`，同样不进入 Git

默认监听：`http://127.0.0.1:9000`  
WebSocket：`ws://127.0.0.1:9000/ws`

### 2.2 FE · FoldEngine（服务端空壳代理）

`FoldEngine/` · LabAPI 插件

FE 没有任何自主逻辑，是 FH 的提线木偶：

- **持续上报**：存活阵营、玩家状态、回合阶段等
- **被动执行**：接收 FH 指令，执行刷怪、换角色、控门、原生字幕/广播
- **转发媒体**：收到 FH 的媒体指令时，透传给对应客户端
- **稳定性**：4KB 控制包上限、每玩家 5 req/s 令牌桶、64 条主线程队列熔断、全量 try-catch
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

### 2.4 FVP-Native · FoldNativeClient（Unity 原生集成模块）

`FoldNativeClient/` · 纯 Unity C# 脚本集 · **不产生独立可执行文件**

- 官方可直接复制进 SCP:SL 客户端源码，随游戏下发与更新
- `FoldNativeUI`：`ScreenSpaceOverlay` Canvas（`sortingOrder = 9999`）+ 黑色背景 + `RawImage` + `AspectRatioFitter`
- `PlayVideo(string url)`：Unity `VideoPlayer` → `RenderTexture` → `RawImage`，音频走 `AudioSource`
- `PlayImage(string url)`：`UnityWebRequestTexture` 原生加载
- `Stop()`：停止播放并销毁 Canvas、`RenderTexture`、`VideoPlayer` 与 `AudioSource`；F9 同样触发
- 复用同一套 ECDSA P-256 验签、3 秒时间窗、nonce 防重放与固定公钥（fail-closed）
- 在任意 Mirror 包处理回调中调用 `FoldNativeRuntime.HandleMarker(marker)` 即可
- 集成说明：[FoldNativeClient/README.md](FoldNativeClient/README.md) · 官方合入指南：[NW-REVIEW.md](NW-REVIEW.md)

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

### 3.1 使用 FoldPacker 打包

`tools/FoldPacker` 是一个纯 C# 命令行打包工具：输入一个目录，输出标准 `.fdll`。

目录示例：

```
my-module/
├─ manifest.json
├─ timeline.json
└─ media/
   └─ intro.mp4
```

加密并签名：

```powershell
dotnet run --project .\tools\FoldPacker\FoldPacker.csproj -- `
  .\my-module `
  -o .\FoldEdge\Modules\my-module.fdll `
  --encrypt `
  --sign .\FoldEdge\keys\fold-ecdsa-private.pem
```

也可以直接使用发布好的可执行文件：

```powershell
.\tools\FoldPacker\dist\FoldPacker.exe .\my-module `
  -o .\my-module.fdll `
  --encrypt `
  --sign .\FoldEdge\keys\fold-ecdsa-private.pem
```

| 参数 | 说明 |
| --- | --- |
| `-o, --output` | 输出路径，默认当前目录 `<目录名>.fdll` |
| `--encrypt` | 生成 `FOLDENC1` 容器（AES-256-CBC / PKCS7），与 FH 的读取格式完全一致 |
| `--sign` | 对最终 `.fdll` 字节生成 `<输出>.sig`（Base64 DER，ECDSA P-256 + SHA-256） |
| `-h, --help` | 显示帮助 |

打包完成后终端会打印模块名、版本、作者、标签、条目数、加密/签名状态、文件大小和 SHA-256。

> `--encrypt` 使用 Fold 现有的预设密钥，目的是与 FH 的 `FOLDENC1` 容器兼容，**不是**面向攻击者的强保密方案；密钥随程序分发。  
> `.sig` 是对最终文件（加密后的字节）的分离签名，可用 FH 的 `keys/fold-ecdsa-public.pem` 验证。

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

### 4.2 公钥固定（Trust Anchor）

FVP 不信任连接里送来的任意公钥。启动时读取同目录 `FoldClient.pub`（SPKI Base64 或 PEM）作为唯一信任锚：

1. FH 握手后仍会推送 `{"type":"public-key","publicKey":"..."}`，FVP 也会发送 `get_public_key`
2. FVP 只接受与 `FoldClient.pub` **完全一致**的公钥
3. `FoldClient.pub` 缺失或无效时 **fail-closed**：所有指令一律拒绝
4. 轮换密钥必须重新分发 `FoldClient.pub`，不能靠动态下发绕过信任锚

### 4.3 接收层强制验签

所有可执行指令统一进入 `FoldCommandReceiver`，执行前必须完成：

- 解析 `[FOLD_*]<ts>|<nonce>|<sig>|<payload>`
- 校验 ECDSA P-256 / SHA-256 签名
- 校验 3 秒时间窗
- 校验 nonce 未使用（只有验签成功后才登记 nonce）
- 校验本地家长控制标签

任一失败立即丢弃并写入日志，例如 `reason=bad-signature` / `expired` / `replay` / `no-key`。  
FE 不持有私钥，只原样转发 FH 生成的签名信封；未签名或畸形标记会被 FE 直接拒绝。

### 4.4 防中间人（MitM）声明

**Security Against Man-in-the-Middle (MitM): Even in the native integrated version using Mirror UDP, all Fold commands are cryptographically signed with ECDSA. If a hacker intercepts and modifies the packet to render ESP or unauthorized media, the client will reject the payload entirely. It is physically impossible to abuse Fold for cheating.**

**防中间人劫持：即便是在原生集成版的 UDP 通信中，所有 Fold 指令也都有 ECDSA 签名。黑客即便拦截并篡改数据包，客户端也会拒绝执行。物理上不可能利用 Fold 作弊。**

> 适用范围：上述保证针对 **Fold 指令通道**（远程篡改/伪造指令）。玩家如果自行修改客户端二进制、在本地绘制覆盖层或使用其他作弊工具，属于另一个威胁模型，任何客户端插件都无法阻止；本项目不提供也不承诺反作弊绕过。  
> 完整声明见 **[docs/nw-security.md](docs/nw-security.md)**。

### 4.5 边界

- 不注入游戏进程，不加载 BepInEx，不修改游戏内存
- FVP 通过 WebSocket（未来 Mirror UDP）接收 FH 签名的指令并渲染
- 公钥固定文件缺失时拒绝播放，而不是回退到明文或动态信任
- 本项目不提供、也不承诺任何反作弊绕过
- 远程部署请使用 `wss://` 或 VPN；明文 `ws://` 仅建议本机/内网使用

### 4.6 服务器稳定性

**Server Stability Guaranteed: FoldEngine is designed with strict input validation and rate limiting. It is immune to malicious packet flooding and memory overflow exploits that commonly crash other modded servers.**

**保证服务器稳定：FoldEngine 具备严格的输入校验和限流，免疫恶意炸服和内存溢出漏洞。**

已落实的防护：

- **数据大小硬限制**：普通控制包 4KB；加密模块/时间轴 256KB 硬上限，超长包直接静默丢弃
- **严格格式校验**：只接受 `[FOLD_VIDEO] / [FOLD_IMG] / [FOLD_AUDIO] / [FOLD_TEXT] / [FOLD_CMD]`，时间戳、nonce、签名、payload 缺一不可
- **全量 try-catch**：WebSocket 解析、模块解密、时间轴解析、玩家指令执行全部隔离异常，不让游戏主进程崩溃
- **每玩家令牌桶**：入口限制 5 请求/秒，超限直接拒绝；服务器控制台不受限
- **队列熔断**：主线程队列上限 64 条，过载时丢弃最旧指令
- **线程隔离**：JSON/解密/时间轴解析在 WebSocket 后台线程完成，只有游戏 API 动作切回主线程

> 适用范围：上述措施针对超大包、畸形 JSON、洪水请求与主线程阻塞等常见炸服手法；不对游戏本体或零日漏洞作绝对保证。

### 4.7 管理面板、RBAC 与限时权限

- **首次初始化**：`/api/setup/status` 返回未初始化时，所有受保护接口返回 `428`；只有 `POST /api/setup/owner` 能创建唯一的 Owner
- **密码存储**：PBKDF2-SHA256，120,000 次迭代，16 字节随机盐，32 字节哈希，常量时间比较
- **JWT**：HS256；签名密钥自动生成并保存在 `FoldEdge/Data/jwt-secret.b64`；服务端同时校验 `ver` 与用户状态
- **RBAC**：`Owner` 全部权限；`Admin` 可触发模块/控制播放/查看日志；`Viewer` 只读。越权请求返回 `403`
- **限时权限**：`ExpiryService` 每 5 秒检查一次；到期后 `TokenVersion++`（旧 JWT 立即失效）、`Vpn.Enabled=false`、写入安全警告日志；若绑定 SteamID，则向 FE 下发 `revoke-access` 并执行 `RoundRestart`
- **日志**：`Data/Logs/foldedge-YYYY-MM-DD.log`，保留 7 天；每条日志同时包含中英文文本，UI 切换语言时无需重新请求
- **VPN**：凭证与账号生命周期由 FH 管理；`/api/vpn/verify` 供网关校验；实际隧道由服主配置的 L2TP / IKEv2 网关承载

> 管理面板已内置登录与 RBAC，但**不包含 TLS、MFA 或反向代理**。公网部署请务必放在 HTTPS 反向代理之后，并限制 9000 端口来源。

---

## 5. 三种使用场景 (Three Deployment Modes)

Fold 的服务端主脑（FH）、游戏内插件（FE）与客户端渲染器（FVP）完全解耦。按你的环境选择一种即可，三种场景可以混合使用。

### 5.1 场景一：本地一键体验（All-in-One 镜像）

适合：想先看到完整 Web 管理面板、不想配置 .NET 环境的服主。

前提：安装 Docker Desktop（Windows / macOS）或 Docker Engine（Linux）。

```bash
git clone https://github.com/xinyantao0803-max/Fold-Engine.git
cd Fold-Engine
./start.sh
```

`start.sh` 会检查 Docker、执行 `docker compose up -d --build`、等待 `/api/health`，最后在终端打印醒目的 Web 访问地址。

也可以手动执行：

```bash
docker compose up -d --build
```

打开 **http://127.0.0.1:9000/dashboard**。首次访问会强制进入 **Owner 初始化页**。面板包含：

- FE / FVP 在线数量、模块数量、账号数量、运行时长与最近遥测
- `Modules/*.fdll` 实时列表，点击 **TRIGGER** 即通过 WebSocket 下发播放指令
- 用户管理：Owner / Admin / Viewer、SteamID 绑定、限时有效期、VPN 凭证与 Windows 脚本下载
- 统一日志中心：实时、按天切割、保留 7 天、中英双语
- 配置中心：限流开关与日志级别，保存后持久化并广播给在线 FE

也可以直接拉取已构建镜像：

```bash
docker run -d --name foldedge --network host --restart always \
  -v "$PWD/FoldEdge/keys:/app/keys" \
  -v "$PWD/FoldEdge/Modules:/app/Modules" \
  -v "$PWD/FoldEdge/Assets:/app/Assets" \
  -v "$PWD/FoldEdge/Media:/app/Media" \
  -v "$PWD/FoldEdge/Data:/app/Data" \
  ghcr.io/xinyantao0803-max/fold-engine:v1.3.0
```

> **镜像只包含服务器端 FH。** FVP 是 Windows 原生 WPF 程序，不能放进 Docker。  
> `network_mode: "host"` 会直接占用宿主 9000 端口；面板已内置 JWT 登录与 RBAC，但不含 TLS/MFA，公网部署请加 HTTPS 反向代理与防火墙。  
> `network_mode: "host"` 推荐用于 Linux 宿主；在 Docker Desktop（Windows/macOS）上请改为 `ports: ["9000:9000"]`，并让 FE 连接 `ws://<宿主IP>:9000/ws`。

### 5.2 场景二：纯核心模块（核心引擎版）

适合：已经用宝塔、Windows 服务、systemd 或自己的 .NET 环境管理服务端，只需要 FH + FE 的服主。

**服务端主脑 FH**（需要 .NET 8 Runtime）：

```powershell
dotnet run --project .\FoldEdge\FoldEdge.csproj -c Release
```

或发布后运行：

```powershell
dotnet publish .\FoldEdge\FoldEdge.csproj -c Release -o .\dist\FoldEdge
.\dist\FoldEdge\FoldEdge.exe
```

**游戏内插件 FE**（LabAPI）：

```powershell
dotnet build .\FoldEngine\FoldEngine.csproj -c Release
```

将 `FoldEngine.dll` 及依赖放入 LabAPI 插件目录，配置 `FoldEdgeUrl`（默认 `ws://127.0.0.1:9000/ws`），重启服务器。

这一场景只使用 `FoldEdge` 与 `FoldEngine` 两个目录；FVP 由玩家各自运行，`FoldNativeClient` 仅供 Northwood 原生合入。

### 5.3 场景三：客户端 FVP（Windows 本地）

**FVP 必须由玩家在本地 Windows 电脑上运行，不能放进 Docker，也不能部署在服务器上。**

原因：FVP 是 WPF 桌面覆盖层，使用 Windows Media Foundation / DXVA 在本机显卡上硬解，并需要置顶、鼠标穿透与 F9 热键。

1. 从 Releases 下载 `FoldClient`（或自行编译）。
2. 将服务器 `FoldEdge/keys/fold-ecdsa-public.pem` 转成 SPKI Base64，保存为 `FoldClient.pub`，放在 `FoldClient.exe` 同目录。
3. 启动：

```powershell
.\FoldClient.exe --edge ws://<服务器IP>:9000/ws
```

4. 进入游戏后使用窗口化或无边框全屏；按 **F9** 立即停止并销毁画面。

```powershell
# 本机自测播放
.\FoldClient.exe --edge ws://127.0.0.1:9000/ws `
  --test-video http://127.0.0.1:9000/Assets/your-video.mp4
```

`FoldEdge/Assets/` 与 `FoldEdge/Media/` 默认被 Git 忽略，请通过 `/api/media/upload` 或手动放置素材。

### 5.4 服务器控制台快捷指令

```
fold video http://127.0.0.1:9000/Assets/your-video.mp4
fold image http://127.0.0.1:9000/Assets/your-image.png
```

---

## 6. 一键部署（One-Click Deployment via Docker）

根目录的 `docker-compose.yml` 与 [FoldEdge/Dockerfile](FoldEdge/Dockerfile) 可把 FH 作为常驻服务运行：

```bash
docker compose up -d --build
docker compose logs -f foldedge
```

镜像标签：`ghcr.io/xinyantao0803-max/fold-engine:v1.3.0`

关键配置：

- **`network_mode: "host"`**：不做额外端口映射，避免 NAT/UDP 跳数；同机游戏服务端可直接访问 `ws://127.0.0.1:9000/ws`
- **`restart: always`**：进程崩溃或宿主重启后自动恢复
- **卷挂载**：`./FoldEdge/keys`、`./FoldEdge/Modules`、`./FoldEdge/Assets`、`./FoldEdge/Media`、`./FoldEdge/Data`
- **`ListenUrl=http://0.0.0.0:9000`**：容器内必须绑定 `0.0.0.0`；宿主侧访问 `http://127.0.0.1:9000/dashboard`

> 首次启动会在挂载的 `./FoldEdge/keys` 中生成 ECDSA 密钥对；该目录已被 `.gitignore` 排除，不会进入镜像或仓库。  
> 管理面板配置持久化在 `./FoldEdge/Data/dashboard-config.json`。  
> `docker-compose.yml` 只包含 FH 服务。SCP:SL 游戏服务端镜像取决于你的服务端发行版与 LabAPI 版本，请按文件内注释自行加入，并保持 `network_mode: "host"`。  
> 镜像基于 `mcr.microsoft.com/dotnet/aspnet:8.0`，内置 FFmpeg；图片处理使用 `SkiaSharp.NativeAssets.Linux.NoDependencies`，无需额外系统库。

手动构建并推送 GHCR：

```bash
docker build -f FoldEdge/Dockerfile -t ghcr.io/xinyantao0803-max/fold-engine:v1.3.0 .
docker push ghcr.io/xinyantao0803-max/fold-engine:v1.3.0
```

> 推送任意 `v*` 标签时，[`.github/workflows/docker.yml`](.github/workflows/docker.yml) 会自动构建并推送同标签镜像与 `latest`。`v1.3.0` 镜像通过该流程发布。

详细说明见 [docs/docker.md](docs/docker.md)。

---

## 7. 构建

> **编译前置**：`FoldEngine` 需要服务端提供的引用程序集：  
> - 将 `LabAPI.dll`、`Assembly-CSharp.dll`、`CommandSystem.Core.dll`、`Mirror.dll`、`UnityEngine.CoreModule.dll` 放到 `FoldEngine/libs/`  
> 以上文件均被 `.gitignore` 排除，不随仓库分发。

```powershell
dotnet build .\FoldEdge\FoldEdge.csproj   -c Release
dotnet build .\FoldEngine\FoldEngine.csproj -c Release
dotnet build .\FoldClient\FoldClient.csproj -c Release
dotnet build .\tools\FoldPacker\FoldPacker.csproj -c Release

# 原生模块语法检查（UNITY_MANAGED_DIR 指向 Unity 或 SCP:SL 的 Managed 目录）
dotnet build .\FoldNativeClient\FoldNativeClient.csproj -c Release `
  -p:UNITY_MANAGED_DIR="D:\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed"
```

客户端解码器独立测试：

```powershell
dotnet run --project .\tests\FoldClient.Test\FoldClient.Test.csproj -c Release
```

---

## 8. 仓库结构

```
FoldEdge/               FH 主程序（规则引擎、解包、ECDSA 签名、Web UI）
├─ Dockerfile           FH 容器构建（.NET 8，多阶段，内置 FFmpeg）
├─ wwwroot/             /dashboard 企业控制平面（SPA、双语、无 CDN）
├─ Security/            用户库、PBKDF2、JWT、VPN 凭证、限时权限
└─ Logging/             统一日志中心（按天切割、保留 7 天）
FoldEngine/             FE 服务端插件（LabAPI 空壳代理 + revoke-access）
FoldClient/             FVP 客户端（WPF + Media Foundation，方案 A）
└─ Core/                解码与验签核心库
FoldNativeClient/       FVP-Native Unity C# 模块（方案 B，供官方合入）
└─ README.md            原生集成说明与构建检查
FoldStudio/
└─ ROADMAP.md           FS 独立编辑器规划（MIT）
docs/
├─ index.md             文档站首页（双轨制）
├─ native-integration.md 方案 B 合入说明
├─ docker.md            Docker 部署说明
├─ fdll-spec.md         .fdll 规范（时间轴、优先级、标签）
├─ nw-security.md       Northwood Studios 安全声明（防 MitM / 零信任协议）
├─ nw-email-draft.md    给 Northwood Studios 的邮件草稿
└─ stylesheets/         文档站主题样式
docs-site/              MkDocs Material 配置（GitHub Pages）
tests/
└─ FoldClient.Test/     解码器与验签独立测试
tools/
├─ pack-fdll.ps1        旧版 PowerShell 打包脚本
└─ FoldPacker/          C# CLI 打包工具（加密 / 签名）
start.sh                一键检查 Docker、构建、启动并打印 Web 地址
docker-compose.yml      FH 一键部署（host 网络、自动重启）
NW-REVIEW.md            给 Northwood 的审查与原生合入指南
README.md
LICENSE                 PolyForm Noncommercial 1.0.0（PFN 1.0）
```

---

## 9. 社区规范与合规

- **强制标签**：包含 18+ 或 GORE 内容的 `.fdll` 必须在 `manifest.json` 中声明对应标签；FH 会把警告前缀注入服务器名。
- **本地家长控制**：FVP 默认拒绝 `18+` / `GORE` 标签内容，玩家可在本地关闭。
- **版权归属**：开发者 100% 拥有其 `.fdll` 的版权，官方 0 抽成。
- **内容责任**：开发者与服主对其 `.fdll` 中的媒体、文字与事件内容承担全部责任，包括素材授权。
- **禁止内容**：不得包含违法、仇恨、骚扰、未经授权的版权素材或任何作弊功能。
- **协议**：项目采用 **PFN 1.0（PolyForm Noncommercial 1.0.0）**，开源但禁止商业使用，详见 [LICENSE](LICENSE)。

---

## 10. 免责声明

- 本项目与 Northwood Studios 无隶属关系，未获得官方背书。
- 项目按白名单审查标准设计；在收到 Northwood 书面许可前，请勿将其描述为“官方组件”，也不要用于公共服务器分发。
- FVP 是独立外部覆盖层，不注入游戏进程，但**不保证**在所有显示模式、分辨率或反作弊环境下可用。
- 作者不对因使用本项目导致的封禁、数据丢失、内容纠纷或任何间接损失负责。
- 使用本项目即表示你已阅读并同意 [LICENSE](LICENSE) 与本免责声明。

---

## 11. License

**PolyForm Noncommercial License 1.0.0 (PFN 1.0)**  
https://polyformproject.org/licenses/noncommercial/1.0.0

完整文本见 [LICENSE](LICENSE)。

> **例外**：Northwood Studios 获得本 README 顶部所述的免费商业授权，可将本项目的客户端渲染代码整合进官方 SCP:SL 源码；该授权不可撤销。
