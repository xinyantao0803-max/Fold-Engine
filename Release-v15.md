# Fold V15 发布校验 (Release Integrity Verification)

本文件记录 **V15** 定版构建的可复现信息与产物哈希。官方或服主下载后可按第 4 节命令自行复算并比对。

---

## 1. 构建信息

| 项目 | 值 |
| --- | --- |
| 版本 | **V15** |
| 构建时间 | 2026-11-12 (Asia/Shanghai) |
| 构建主机 | Windows 10/11 x64 |
| .NET SDK | `8.0.428` |
| 构建配置 | `Release` |
| 警告策略 | `-warnaserror`（警告即错误） |
| 结果 | **0 警告 / 0 错误**（FoldEdge、FoldEngine、FoldNativeClient、FoldPacker） |

构建命令：

```powershell
dotnet build .\FoldEdge\FoldEdge.csproj           -c Release -warnaserror
dotnet build .\FoldEngine\FoldEngine.csproj        -c Release -warnaserror
dotnet build .\tools\FoldPacker\FoldPacker.csproj  -c Release -warnaserror
dotnet build .\FoldNativeClient\FoldNativeClient.csproj -c Release -warnaserror `
  -p:UNITY_MANAGED_DIR="<Unity 或 SCP:SL 的 Managed 目录>"
```

---

## 2. 核心产物 SHA-256

| 产物 | 大小 (bytes) | SHA-256 |
| --- | --- | --- |
| `FoldEdge/bin/Release/net8.0/FoldEdge.dll` | 165,888 | `a21763767439a614c1f8ddf758c42497ace1d60c8233435b6dab2fb174b90c32` |
| `FoldEdge/bin/Release/net8.0/FoldEdge.exe` | 151,552 | `0f8f828e6537969eaa98c2b292cadcb184ac927f864254fcbd6f0b07e5f55ae9` |
| `FoldEngine/bin/Release/net48/FoldEngine.dll` | 39,936 | `68426fc0090af3582e7854aa72e2db870dc019c7f134ddfad1357f20bdca0d72` |
| `FoldNativeClient/bin/Release/netstandard2.1/FoldNativeClient.dll` | 19,968 | `cfa8411a04f86b1e007db535928f287d2c72fde91c70471bab60b189d277ed4c` |
| `dist/V15/FoldPacker/FoldPacker.exe` | 151,552 | `95eded41118d70d709aedefb41b56157cbb9d1cd1cdfa5d82228b07913f2136c` |

> 哈希随源码或 SDK 版本变化。若你复算结果不同，请先确认 SDK 为 `8.0.428` 且使用 `Release` 配置。

---

## 3. FoldNativeClient 源码哈希

供 Northwood 核对合入的五个文件（`FoldNativeClient/`）：

| 文件 | SHA-256 |
| --- | --- |
| `FoldCommandReceiver.cs` | `c20d3e2ffe7cf9704bcf5e071d35a5f67d4223520ba3015368239ffc7b364b89` |
| `FoldNativeConfig.cs` | `414ce94bccb009327dd0624612e4147b00a89f53d583be710f39a3fae6d2d440` |
| `FoldNativeRuntime.cs` | `8a7b916b54c3221d32367ef190f3fb5bdada9b1acfd81098cff45e972c3e72ee` |
| `FoldNativeUI.cs` | `60c3936d503d28e0edcf6d6867ae96fe48e063f725cb90536816761d6a9f8834` |
| `SignedMediaCommand.cs` | `fe24fa283b9ee414ed4ac65cc3b97f36e2f9f8a1504b0f51362cd3162678d529` |

---

## 4. 目录结构（核心层级）

```
FoldEdge/               FH 服务端主脑（.NET 8 + Web UI + WS + 签名）
FoldEngine/             FE 服务端插件（LabAPI 空壳代理）
FoldNativeClient/       FVP-Native Unity C# 模块（供官方合入）
FoldStudio/ROADMAP.md   FS 独立编辑器规划
docs/                   文档站源文件
docs-site/              MkDocs Material 配置
tools/FoldPacker/       .fdll 打包工具（加密 / 签名）
```

外部 WPF 客户端 `FoldClient/` 与其测试 `tests/FoldClient.Test/` 已在 V15 中移除。

---

## 5. 验证命令

```powershell
# 复算产物哈希
Get-FileHash .\FoldEdge\bin\Release\net8.0\FoldEdge.dll -Algorithm SHA256
Get-FileHash .\FoldEngine\bin\Release\net48\FoldEngine.dll -Algorithm SHA256
Get-FileHash .\FoldNativeClient\bin\Release\netstandard2.1\FoldNativeClient.dll -Algorithm SHA256
Get-FileHash .\dist\V15\FoldPacker\FoldPacker.exe -Algorithm SHA256

# 服务健康检查（启动 FoldEdge 后）
Invoke-RestMethod http://127.0.0.1:9000/api/health
```

`/api/health` 期望返回：

```json
{ "status": "ok", "version": "V15" }
```

---

## 6. 容器镜像

| 项目 | 值 |
| --- | --- |
| 镜像 | `ghcr.io/xinyantao0803-max/fold-engine:v15` |
| Dockerfile | [`FoldEdge/Dockerfile`](FoldEdge/Dockerfile) |
| Compose | [`docker-compose.yml`](docker-compose.yml) |
| 构建方式 | 推送 `v15` 标签后由 GitHub Actions 自动构建；或本地 `docker build` |

```bash
docker build -f FoldEdge/Dockerfile -t ghcr.io/xinyantao0803-max/fold-engine:v15 .
```
