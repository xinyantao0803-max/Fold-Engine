# FoldNativeClient — Native Unity Integration Module

**This is a native Unity integration module. NW can copy these scripts directly into their client source code. It does not require any external launchers, injected DLLs, or BepInEx.**

**这是一个纯 Unity C# 原生集成模块。Northwood Studios 可以直接把本目录的源码并入 SCP:SL 客户端工程，随游戏一起下发与更新；不需要外部启动器、不需要 DLL 注入、不依赖 BepInEx。**

---

## 1. What this folder is

| | |
| --- | --- |
| Type | Unity client-side C# scripts (no executable) |
| Target | SCP:SL client source tree (Unity 6 / IL2CPP) |
| Namespace | `FoldNative` |
| Assembly definition | `FoldNativeClient.asmdef` |
| Transport | Transport-agnostic: accepts a signed `[FOLD_*]` string from your own Mirror handler |
| External dependencies | `UnityEngine.UI`, `UnityEngine.Video`, `UnityEngine.UnityWebRequestTexture` (all built into Unity) |
| Licence | PFN 1.0, with an irrevocable free commercial grant for Northwood Studios (see root `README.md`) |

This is **not** a standalone program and **not** an overlay. It is the in-engine client renderer
for Fold. The server keeps signing the same ECDSA envelope regardless of how the client is built.

---

## 2. Files

| File | Responsibility |
| --- | --- |
| `FoldNativeRuntime.cs` | Public entry point: `Initialize()`, `LoadPinnedKey()`, `HandleMarker()`, `HandlePacket()`, `Shutdown()`. Creates the persistent `FoldNativeClient` GameObject. |
| `FoldCommandReceiver.cs` | The only executable-command entry point. Validates the marker, verifies the signature, applies parental control, then queues playback. |
| `SignedMediaCommand.cs` | ECDSA P-256 + SHA-256 verification, 3-second window, single-use nonce cache, pinned public key, DER→raw conversion, constant-time key comparison. |
| `FoldNativeUI.cs` | Native renderer. Creates a `ScreenSpaceOverlay` Canvas at `sortingOrder = 9999`, a black backdrop and a `RawImage`; `PlayVideo(string url)` drives `VideoPlayer` + `RenderTexture`; `PlayImage(string url)` uses `UnityWebRequestTexture`; `Stop()` destroys the Canvas, textures and playback components; F9 also calls `Stop()`. |
| `FoldNativeConfig.cs` | Local policy: `MaxAgeMilliseconds = 3000`, parental-control tags (`18+`, `GORE`), payload inspection. |
| `FoldNativeClient.asmdef` | Unity assembly definition so the module compiles as its own assembly. |
| `FoldNativeClient.csproj` | **Not shipped to Unity.** Local/CI-only syntax check project. It references Unity managed assemblies from `UNITY_MANAGED_DIR` and produces no runtime artifact. |

---

## 3. Integration in three steps

### Step 1 — copy the folder

Copy `FoldNativeClient/` into the Unity project, for example `Assets/FoldNativeClient/`.
Unity picks up `FoldNativeClient.asmdef` automatically; no manual assembly setup is required.

### Step 2 — initialise once on the Unity main thread

Call this after the client boots (for example next to your existing service initialisation),
before any Fold command can arrive:

```csharp
using FoldNative;

// Must run on the Unity main thread.
FoldNativeRuntime.Initialize();

// Trust anchor: StreamingAssets/FoldClient.pub (SPKI Base64 or PEM).
// If the file is missing or invalid, every command is rejected (fail-closed).
FoldNativeRuntime.LoadPinnedKey();
```

Place `FoldClient.pub` in `Assets/StreamingAssets/FoldClient.pub`. If you prefer to embed the key,
call `SignedMediaCommand.SetPinnedKey(base64)` directly instead of `LoadPinnedKey()`.

Optional: if the game's UI stack should own the generated Canvas, parent it before the first
playback. `sortingOrder = 9999` and `overrideSorting` are preserved inside that hierarchy:

```csharp
FoldNativeRuntime.UI.SetCanvasParent(existingUiRoot.transform);
```

### Step 3 — forward received envelopes

In the Mirror handler that already receives the server's Fold marker, forward the string
verbatim. Do not parse, rewrite or re-serialise it — the signature covers the exact bytes:

```csharp
private void OnFoldMarker(string marker)
{
    // Returns true only when the envelope was verified and accepted.
    if (!FoldNativeRuntime.HandleMarker(marker))
        return;
}
```

For binary handlers there is a UTF-8 convenience overload with a hard 4 KB cap:

```csharp
FoldNativeRuntime.HandlePacket(payloadBytes);
```

That is the whole integration. There is no second process, no IPC, no window handle to find,
and no external launcher to keep alive.

---

## 4. Rendering behaviour

- **Canvas**: `RenderMode.ScreenSpaceOverlay`, `sortingOrder = 9999`, `CanvasScaler` at 1920×1080
  reference resolution, plus a black backdrop and a full-screen `RawImage` with
  `AspectRatioFitter` (`FitInParent`).
- **Video**: `VideoPlayer` with `VideoSource.Url`, `VideoRenderMode.RenderTexture`, and an
  `AudioSource` for the audio track. The `RenderTexture` is created in `prepareCompleted` from the
  real clip dimensions, then playback starts.
- **Image**: `UnityWebRequestTexture.GetTexture`, then the downloaded texture is assigned to the
  `RawImage`.
- **Stop**: unsubscribes events, stops and destroys the `VideoPlayer` / `AudioSource`, releases and
  destroys the `RenderTexture`, and destroys the Canvas GameObject. F9 triggers the same path.
- **Threading**: render calls are queued through a `ConcurrentQueue<Action>` and drained in
  `Update()`, so the receiver may be called from a network callback without touching Unity APIs
  off the main thread.

Because the surface is a normal in-engine Canvas, it composites with the game's own UI, and the
`AudioSource` feeds the same audio pipeline as in-game voice and effects.

---

## 5. Security model (unchanged from the external client)

Envelope:

```
[FOLD_*]<unix-ms>|<nonce>|<base64url(DER signature)>|<payloadJson>
```

Signature input:

```
kind + "\n" + payloadJson + "\n" + timestampMs + "\n" + nonce
```

- ECDSA P-256 + SHA-256
- 3-second acceptance window (`FoldNativeConfig.MaxAgeMilliseconds`)
- nonce cache with single-use enforcement; the nonce is recorded **only after** a valid signature,
  so forged packets cannot burn legitimate nonces
- pinned public key in `StreamingAssets/FoldClient.pub`; a key received over the network is accepted
  **only if it is byte-for-byte identical** to the pin
- missing/invalid key ⇒ fail-closed, every command rejected
- rejection reasons are logged: `malformed`, `bad-timestamp`, `expired`, `replay`,
  `bad-signature-encoding`, `no-key`, `bad-signature`
- parental control is enforced locally before anything is rendered

The transport is irrelevant to the security decision: WebSocket today, Mirror UDP after
integration, verified by the same code before any Unity API is touched.

---

## 6. Build / syntax check

The scripts target Unity's .NET Standard 2.1 profile. To compile-check them outside Unity:

```powershell
dotnet build .\FoldNativeClient\FoldNativeClient.csproj -c Release `
  -p:UNITY_MANAGED_DIR="D:\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed"
```

`UNITY_MANAGED_DIR` must point at a folder containing the Unity managed assemblies
(`UnityEngine.CoreModule.dll`, `UnityEngine.UI.dll`, `UnityEngine.VideoModule.dll`, …).
Those DLLs are never committed to this repository.

Inside Unity, no build step is needed beyond the normal player build. Ensure the
**Video** and **Unity Web Request Texture** modules are enabled in the player settings.

---

## 7. Notes for reviewers

- No `DllImport`, no P/Invoke, no native plugin, no `Process.Start`, no window hooks.
- No reflection into game assemblies and no patching of existing game code.
- The module only creates its own Canvas when a verified command asks it to, and destroys it on
  `Stop()`.
- Everything is plain C# and can be code-reviewed, unit-tested, or rewritten by Northwood
  engineers without reverse engineering.

See the repository-root [`NW-REVIEW.md`](../NW-REVIEW.md) for the full integration and review
guide.
