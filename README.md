# 🚀 Fold —— SCP:SL 服务器统筹主程序（OS 级别）

> The OS-level orchestrator for SCP:SL servers.

## 核心定位

**Fold 是统筹管理 SL 服务器的主程序（OS 级别）**，由三大组件构成：

- **FoldEdge**（外部主程序，.NET 8）——控制中枢与转换器：监听 `127.0.0.1:9000`，负责 .fdll 模块监控、图片→颜色矩阵转换、指令下发。
- **FoldEngine**（游戏内服务端插件，LabAPI / .NET 4.8）——服务器内执行器：播放时间轴、向客户端广播内容。
- **FoldClient**（客户端插件，BepInEx 5）——客户端渲染终端：拦截 `[FOLD_IMG]` 标记并全屏渲染真实图片。

## .fdll 模块与版权

- 开发者使用工具生成的 `.fdll` 模块，**版权 100% 归开发者所有**。
- **官方绝不抽成、绝不分成**，仅提供运行时与分发基础设施。
- 模块规范见 [docs/fdll-spec.md](docs/fdll-spec.md)。

## 强制标签机制（18+ 等）

- 强制标签（如 18+）**仅用于「告知知情权」**：让玩家在进入内容前知晓其属性。
- 开发者与服主**不承担**玩家明知标签后强行游玩违规内容所产生的法律责任。

## 许可证

本项目采用 **[PolyForm Noncommercial License 1.0.0](LICENSE)**：

- ❌ **严禁任何换皮商用**。
- ✅ 允许个人 / 服主**非商业**使用（含个人研究、兴趣项目、非营利组织使用）。

## 目录结构

```
├── FoldEdge/     # 外部主程序（.NET 8 / ASP.NET Core + ImageSharp + i18n）
│   ├── locales/  # 多语言资源（zh-CN.json / en-US.json）
│   └── Modules/  # .fdll 模块包监控目录（运行时自动创建）
├── FoldEngine/   # 服务端插件（LabAPI，net48，纯文本/标记广播）
├── FoldClient/   # 客户端插件（BepInEx 5 + Harmony，全屏图片渲染）
└── docs/         # 规范文档
```

## 快速开始

```bash
# FoldEdge（主程序）
cd FoldEdge
dotnet run            # 监听 http://127.0.0.1:9000
```

游戏内：FoldEngine.dll 放入服务端 LabAPI 插件目录；FoldClient.dll 放入客户端 `BepInEx/plugins/`。控制台指令 `fold` 执行测试播放。

## API

| Method | Route                   | Description                       |
| ------ | ----------------------- | --------------------------------- |
| GET    | `/api/status`           | 运行状态 + 已加载模块             |
| POST   | `/api/trigger/{module}` | 向插件下发播放指令               |
| POST   | `/api/image`            | 图片 URL → 颜色矩阵 → 推送给插件 |
| GET    | `/ws`                   | WebSocket 接入点（插件长连接）   |
