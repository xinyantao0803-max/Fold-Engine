# NW-REVIEW — Northwood Studios Review Guide

**A review map for SCP:SL Fold Mod: what it is, where the code is, how to integrate it natively,
and what we are asking for.**

Repository: https://github.com/xinyantao0803-max/Fold-Engine  
Documentation site: https://xinyantao0803-max.github.io/Fold-Engine/  
Version reviewed: **v1.4.0**

---

## 1. Summary

Fold Mod is a data-driven media and event framework for SCP:SL. It is delivered as **two
independent, real implementations** — not a single product with a promise attached:

| Track | Code | Integration | Status |
| --- | --- | --- | --- |
| **A · External renderer** | [`FoldClient/`](FoldClient/) | Separate WPF process, zero injection, no game modification | Running, tested |
| **B · Native engine integration** | [`FoldNativeClient/`](FoldNativeClient/) | Pure Unity C# scripts copied into the client source tree | Source-complete, compile-verified |

Both tracks verify the **same ECDSA-signed envelope**, so a server signs commands once and either
client can consume them. The transport can be WebSocket today and Mirror UDP after integration
without changing the verification code.

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
| Signature verification (external client) | [`FoldClient/Core/src/SignedMediaCommand.cs`](FoldClient/Core/src/SignedMediaCommand.cs) |
| Server-side forwarding and limits | [`FoldEngine/FoldEdgeClient.cs`](FoldEngine/FoldEdgeClient.cs) |
| Security declaration and zero-trust proposal | [`docs/nw-security.md`](docs/nw-security.md) |
| `.fdll` format | [`docs/fdll-spec.md`](docs/fdll-spec.md) |
| Security test cases | [`tests/FoldClient.Test/SecurityTests.cs`](tests/FoldClient.Test/SecurityTests.cs) |

---

## 3. Plan B — native integration guide

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

## 4. Plan A — external renderer (already usable)

`FoldClient/` is a standalone WPF process that connects to FH over WebSocket:

- Windows Media Foundation + DXVA hardware decoding, frames uploaded once per frame to a
  `WriteableBitmap`.
- Borderless topmost window, mouse-transparent
  (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`).
- F9 stops and destroys the overlay immediately; Ctrl+Shift+F9 exits.
- It does not touch game memory, does not load into the game process, and does not call Unity APIs.

This track exists so server owners can use Fold today, before any native integration lands.

---

## 5. Ecosystem completeness

| Area | Deliverable |
| --- | --- |
| Runtime | FH (`FoldEdge`, .NET 8), FE (`FoldEngine`, LabAPI), FVP (`FoldClient`, WPF) |
| Enterprise control plane | `/dashboard` SPA, JWT + RBAC, PBKDF2 hashing, bilingual log center, time-limited access, VPN credential lifecycle, silent SCP:SL process lifecycle |
| Native integration | `FoldNativeClient/` — Unity C# module (this release) |
| Deployment | [`start.sh`](start.sh) + [`FoldEdge/Dockerfile`](FoldEdge/Dockerfile) + [`docker-compose.yml`](docker-compose.yml) with `network_mode: "host"` |
| Authoring | [`tools/FoldPacker`](tools/FoldPacker) — `.fdll` packer with AES container and detached ECDSA signature |
| Documentation | [`docs/`](docs/) + MkDocs Material site in [`docs-site/`](docs-site/) |
| Verification | [`tests/FoldClient.Test`](tests/FoldClient.Test) — valid / tampered / expired / wrong-key / replay |
| Roadmap | [`FoldStudio/ROADMAP.md`](FoldStudio/ROADMAP.md) — MIT-licensed authoring tool |

---

## 6. Reproduce the security checks

```powershell
dotnet run --project .\tests\FoldClient.Test\FoldClient.Test.csproj -c Release
```

Expected: valid signature accepted; tampered payload, expired timestamp, wrong signing key and
replayed nonce all rejected with machine-readable reasons.

---

## 7. Our request

1. **Whitelist review** of Plan A so server owners can deploy the external renderer without
   ambiguity.
2. **Technical review of Plan B** as a candidate native feature. The source is complete, licensed
   for Northwood's commercial use, and can be merged, rewritten, or partially adopted.
3. If Plan B is adopted, we are available to adapt the module to your actual client architecture,
   UI stack, and Mirror command routing under your engineering direction.

We are not asking for a privileged position: the code is open, the security model is documented,
and the integration path does not require Northwood to maintain any extra process.

---

## 8. Honest boundaries

- Fold protects the **Fold command channel** against tampering, forgery and replay. It does not
  claim to stop a user who modifies their own client binary or runs unrelated software.
- Fold does not provide, and will not provide, any anti-cheat bypass.
- Plan A requires windowed or borderless-fullscreen mode; exclusive fullscreen cannot host an
  external overlay. Plan B does not have this limitation.
- `FoldClient` audio playback is not yet wired in the external renderer; the native module feeds
  Unity's `AudioSource` directly.
- The FoldStudio editor is a documented roadmap, not shipped code.

---

## 9. Contact

Repository issues: https://github.com/xinyantao0803-max/Fold-Engine/issues  
Email draft for Northwood: [`docs/nw-email-draft.md`](docs/nw-email-draft.md)
