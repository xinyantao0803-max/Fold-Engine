# Native Engine Integration (Plan B)

Plan B is the pure Unity C# module under
[`FoldNativeClient/`](https://github.com/xinyantao0803-max/Fold-Engine/tree/main/FoldNativeClient).
Northwood Studios can copy the scripts directly into the official client source
tree. It does not require an external launcher, an injected DLL, or BepInEx.

## What the module does

- Verifies every `[FOLD_*]` command with ECDSA P-256.
- Enforces a 3-second timestamp window and a single-use nonce cache.
- Rejects dynamic public keys that do not match the pinned key.
- Creates a `ScreenSpaceOverlay` Canvas with `sortingOrder = 9999`.
- Renders video with `VideoPlayer` into a `RenderTexture` and a `RawImage`.
- Supports image rendering through `UnityWebRequestTexture`.
- Stops playback with F9 and destroys the generated Canvas.

## Files

| File | Purpose |
| --- | --- |
| `FoldNativeRuntime.cs` | Entry point: `Initialize`, `LoadPinnedKey`, `HandleMarker`, `HandlePacket`, `Shutdown` |
| `FoldCommandReceiver.cs` | Single verified command entry point |
| `SignedMediaCommand.cs` | ECDSA verification, timestamp, nonce, pinned key |
| `FoldNativeUI.cs` | Canvas 9999, RawImage, VideoPlayer, RenderTexture, F9 |
| `FoldNativeConfig.cs` | 3-second window, parental control tags |
| `FoldNativeClient.asmdef` | Unity assembly definition |

## Integration steps

1. Copy the entire `FoldNativeClient/` folder into the Unity project's
   `Assets/` directory.
2. Call `FoldNativeRuntime.Initialize()` once after the client boots. This must
   happen on the Unity main thread.
3. Place the server public key at `StreamingAssets/FoldClient.pub`, then call:

   ```csharp
   FoldNativeRuntime.LoadPinnedKey();
   ```

   If the key is missing, the module fails closed and rejects every command.
4. In the Mirror packet handler, after extracting the `[FOLD_*]` string:

   ```csharp
   bool accepted = FoldNativeRuntime.HandleMarker(marker);
   ```

   For a UTF-8 `byte[]` payload:

   ```csharp
   bool accepted = FoldNativeRuntime.HandlePacket(payload);
   ```

5. The module creates its own Canvas and does not touch the existing UI
   hierarchy. F9 stops playback and destroys the generated Canvas.

## Security notes

- The public key must be distributed out-of-band and pinned. Do not trust a key
  received over the same unauthenticated channel.
- The verifier rejects malformed, expired, replayed, and incorrectly signed
  commands before any UI or media call.
- `FoldEngine` never signs; it forwards FH-signed envelopes and rejects unsigned
  or malformed markers. The private key stays in FH.
- The module is fail-closed: no pinned key means no rendering.

## Build check

The module can be syntax-checked outside Unity against the game's managed
assemblies:

```powershell
dotnet build .\FoldNativeClient\FoldNativeClient.csproj -c Release `
  -p:UNITY_MANAGED_DIR="<path-to-UnityEngine-managed-folder>"
```

Unity itself compiles the scripts through the included `.asmdef`; the
`csproj` is only a build/CI aid and does not ship Unity DLLs.
