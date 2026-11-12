# NW-REVIEW — Northwood Studios Review Guide

**A review map for SCP:SL Fold Mod: what it is, where the code is, how to integrate it natively,
and what we are asking for.**

Repository: https://github.com/xinyantao0803-max/Fold-Engine  
Documentation site: https://xinyantao0803-max.github.io/Fold-Engine/  
Version reviewed: **V15**

---

## 1. Summary

Fold Mod is a data-driven media and event framework for SCP:SL. The client-side rendering is
delivered as **one native Unity C# module**, ready to be merged into the official client source:

| Track | Code | Integration | Status |
| --- | --- | --- | --- |
| **Server · FH + FE** | [`FoldEdge/`](FoldEdge/) · [`FoldEngine/`](FoldEngine/) | .NET 8 control plane + LabAPI plugin | Running, tested |
| **Native engine integration** | [`FoldNativeClient/`](FoldNativeClient/) | Pure Unity C# scripts copied into the client source tree | Source-complete, compile-verified |

The server signs every command once with an ECDSA envelope; the native module verifies it. The
transport can be WebSocket today and Mirror UDP after integration without changing the verification
code. The external WPF renderer (Plan A) has been removed from this repository.

**Core barriers**

- **Zero injection** — no `LoadLibrary`, no `CreateRemoteThread`, no BepInEx, no memory patching.
- **Multi-core isolation** — decoding and JSON/timeline parsing run off the main thread; only the
  final UI action is marshalled to the Unity main thread.
- **ECDSA anti-hijack** — every executable command is signed (P-256 / SHA-256), 3-second window,
  single-use nonce, pinned public key, fail-closed.
- **Anti-flood rate limiting** — 4 KB control-packet cap, 256 KB payload cap, per-player token
  bucket (5 req/s), 64-entry main-thread queue circuit breaker, full try-catch isolation.

---

## 2. What to review first

| Goal | File |
| --- | --- |
| Understand the whole system | [`README.md`](README.md) |
| Native Unity module, entry point | [`FoldNativeClient/FoldNativeRuntime.cs`](FoldNativeClient/FoldNativeRuntime.cs) |
| Native Unity module, renderer | [`FoldNativeClient/FoldNativeUI.cs`](FoldNativeClient/FoldNativeUI.cs) |
| Signature verification (Unity port) | [`FoldNativeClient/SignedMediaCommand.cs`](FoldNativeClient/SignedMediaCommand.cs) |
| Command gate (Unity port) | [`FoldNativeClient/FoldCommandReceiver.cs`](FoldNativeClient/FoldCommandReceiver.cs) |
| Server-side forwarding and limits | [`FoldEngine/FoldEdgeClient.cs`](FoldEngine/FoldEdgeClient.cs) |
| Server control plane and signing | [`FoldEdge/Program.cs`](FoldEdge/Program.cs) · [`FoldEdge/MediaSigner.cs`](FoldEdge/MediaSigner.cs) |
| Security declaration and zero-trust proposal | [`docs/nw-security.md`](docs/nw-security.md) |
| `.fdll` format | [`docs/fdll-spec.md`](docs/fdll-spec.md) |

---

## 3. Native integration guide

### 3.1 Files to extract

Copy the whole `FoldNativeClient/` folder into the Unity project (for example
`Assets/FoldNativeClient/`). Only these five scripts are runtime code:

```
FoldNativeClient/
├─ FoldNativeRuntime.cs        public entry point
├─ FoldNativeUI.cs             Canvas + RawImage + VideoPlayer renderer
├─ FoldCommandReceiver.cs      verification gate before any UI call
├─ SignedMediaCommand.cs       ECDSA / timestamp / nonce / pinned key
├─ FoldNativeConfig.cs         local policy (window, parental control)
├─ FoldNativeClient.asmdef     assembly definition
└─ FoldNativeClient.csproj     CI-only syntax check project — do NOT ship to Unity
```

No third-party packages, no native plugins, no `DllImport`, no reflection into game assemblies.

### 3.2 Mount into the existing Canvas hierarchy

By default `FoldNativeUI` creates its own root Canvas:

```
FoldNativeCanvas            Canvas (ScreenSpaceOverlay, sortingOrder 9999, overrideSorting)
├─ FoldNativeBackdrop       Image (black, raycastTarget = false)
└─ FoldNativeSurface        RawImage + AspectRatioFitter (FitInParent)
```

If you want the game's UI stack to own its lifetime, parent it under your existing UI root before
the first playback. The generated Canvas keeps `sortingOrder = 9999` and sets `overrideSorting`,
so it still renders above normal HUD layers inside that hierarchy:

```csharp
using FoldNative;

FoldNativeRuntime.Initialize();

// Optional: let the game's UI stack own the generated Canvas.
FoldNativeRuntime.UI.SetCanvasParent(existingUiRoot.transform);
```

The Canvas is created lazily on the first verified media command and destroyed by `Stop()`, so an
idle client allocates nothing.

### 3.3 Initialise once, then forward packets

```csharp
// Once, on the Unity main thread, after client boot:
FoldNativeRuntime.Initialize();
FoldNativeRuntime.LoadPinnedKey();          // StreamingAssets/FoldClient.pub

// In your existing Mirror handler, for every received Fold marker:
bool accepted = FoldNativeRuntime.HandleMarker(marker);
// or, for byte payloads (4 KB cap enforced):
bool accepted = FoldNativeRuntime.HandlePacket(payloadBytes);
```

`HandleMarker` accepts `[FOLD_VIDEO]`, `[FOLD_IMG]`, `[FOLD_AUDIO]`, `[FOLD_TEXT]` and
`[FOLD_CMD]`. Video and image are rendered; `[FOLD_CMD]STOP` calls `Stop()`. The payload is
expected to be the exact string the server signed — do not normalise or re-serialise it.

### 3.4 Key distribution

- Ship `FoldClient.pub` (SPKI Base64 or PEM) in `StreamingAssets/`, or embed the key and call
  `SignedMediaCommand.SetPinnedKey(base64)`.
- A key received over the network is accepted **only if it is byte-for-byte identical** to the pin.
- Missing or invalid pin means every command is rejected. This is intentional.
- Rotation is an explicit client update, not a runtime handshake.

### 3.5 Player settings

Enable the Unity modules the renderer uses:

- **Video** (`UnityEngine.VideoModule`) — `VideoPlayer`
- **Unity Web Request Texture** — image download
- **UI** (`UnityEngine.UI`, `UnityEngine.UIModule`)
- **Audio** — the video's `AudioSource`

The module targets Unity's .NET Standard 2.1 profile and is IL2CPP-safe: no dynamic code, no
runtime code generation, no reflection emit.

### 3.6 Threading contract

`HandleMarker` may be called from a network callback. Rendering is queued through a
`ConcurrentQueue<Action>` and drained in `Update()` on the main thread. The only requirement is that
`Initialize()` itself runs on the main thread, because it creates a GameObject.

### 3.7 Verification

```powershell
dotnet build .\FoldNativeClient\FoldNativeClient.csproj -c Release `
  -p:UNITY_MANAGED_DIR="<path to your Unity managed assemblies>"
```

Current result in this repository: **0 warnings, 0 errors**.

---

## 4. Ecosystem completeness

| Area | Deliverable |
| --- | --- |
| Runtime | FH (`FoldEdge`, .NET 8), FE (`FoldEngine`, LabAPI) |
| Enterprise control plane | `/dashboard` SPA, JWT + RBAC, PBKDF2 hashing, bilingual log center, time-limited access, VPN credential lifecycle, silent SCP:SL process lifecycle |
| Native integration | `FoldNativeClient/` — Unity C# module |
| Deployment | [`start.sh`](start.sh) + [`FoldEdge/Dockerfile`](FoldEdge/Dockerfile) + [`docker-compose.yml`](docker-compose.yml) with `network_mode: "host"` |
| Authoring | [`tools/FoldPacker`](tools/FoldPacker) — `.fdll` packer with AES container and detached ECDSA signature |
| Documentation | [`docs/`](docs/) + MkDocs Material site in [`docs-site/`](docs-site/) |
| Roadmap | [`FoldStudio/ROADMAP.md`](FoldStudio/ROADMAP.md) — MIT-licensed authoring tool |

---

## 5. Reproduce the build and checks

```powershell
# Server control plane
dotnet build .\FoldEdge\FoldEdge.csproj -c Release -warnaserror

# LabAPI plugin
dotnet build .\FoldEngine\FoldEngine.csproj -c Release -warnaserror

# Packaging tool
dotnet build .\tools\FoldPacker\FoldPacker.csproj -c Release -warnaserror

# Native module syntax check
dotnet build .\FoldNativeClient\FoldNativeClient.csproj -c Release -warnaserror `
  -p:UNITY_MANAGED_DIR="<path to your Unity managed assemblies>"
```

Expected: **0 warnings, 0 errors** for all four projects.

Signature behaviour (valid / tampered / expired / wrong-key / replay) is enforced in
[`FoldNativeClient/SignedMediaCommand.cs`](FoldNativeClient/SignedMediaCommand.cs) and mirrors the
server-side envelope produced by [`FoldEdge/MediaSigner.cs`](FoldEdge/MediaSigner.cs).

---

## 6. Our request

1. **Technical review** of the native module as a candidate official feature. The source is
   complete, licensed for Northwood's commercial use, and can be merged, rewritten, or partially
   adopted.
2. If adopted, we are available to adapt the module to your actual client architecture, UI stack,
   and Mirror command routing under your engineering direction.

We are not asking for a privileged position: the code is open, the security model is documented,
and the integration path does not require Northwood to maintain any extra process.

---

## 7. Honest boundaries

- Fold protects the **Fold command channel** against tampering, forgery and replay. It does not
  claim to stop a user who modifies their own client binary or runs unrelated software.
- Fold does not provide, and will not provide, any anti-cheat bypass.
- The native module renders through a runtime-created `Canvas`; it requires the `Video`, `UI`,
  `Unity Web Request Texture` and `Audio` modules to be enabled in the player settings.
- The FoldStudio editor is a documented roadmap, not shipped code.

---

## 8. Contact

Repository issues: https://github.com/xinyantao0803-max/Fold-Engine/issues  
Email draft for Northwood: [`docs/nw-email-draft.md`](docs/nw-email-draft.md)
