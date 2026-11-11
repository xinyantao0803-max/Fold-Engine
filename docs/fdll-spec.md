# Fold Engine `.fdll` Module Specification

**Version: 1.0.0 (Draft)**
**License: PolyForm Noncommercial 1.0.0 (PFN 1.0)**

The `.fdll` file is the core data container for Fold Engine. It is a self-contained, encrypted (optional) package that includes metadata, timelines, subtitles, and all associated media assets.

## 1. Package Structure

A standard `.fdll` file is a ZIP archive with a custom extension. It can optionally be wrapped in an AES-256 encrypted container (see §5). Upon extraction, it contains the following structure:

```text
MyModule.fdll (ZIP)
├── manifest.json      # [Required] Module metadata and compliance tags
├── timeline.json      # [Required] The playback and interaction script
├── subtitles.json     # [Optional] Subtitle and text overlay data
└── assets/            # [Optional] Media files (video, audio, images, models)
    ├── video/
    ├── audio/
    ├── images/
    └── models/
```

## 2. manifest.json (Metadata & Compliance)

This file defines the module's identity, author, and essential compliance information. The FoldEdge master program uses this to enforce server-wide content warnings.

```json
{
  "id": "com.fold.o5_meeting",
  "name": "O5 Council Meeting",
  "version": "1.0.0",
  "author": "YourName",
  "engineVersion": ">=1.0.0",
  "description": "A cinematic narrative of the O5 council.",
  "tags": ["18+", "GORE", "INTENSE"]
}
```

### 🛡️ Compliance & Tagging System (Important)

To protect developers, server owners, and players globally, tags are mandatory for certain content types. FoldEdge will forcefully inject a warning prefix into the server name if these tags are present.

- `[18+]` - Contains adult themes, nudity, or highly sensitive content.
- `[GORE]` - Contains extreme blood, violence, or graphic imagery.
- `[FLASH]` - Contains intense flashing lights (Epilepsy warning).
- `[POLITICS]` - Contains controversial political themes.

> **Note:** The tags array is used for **Disclosure, not censorship**. Players with "Parental Control" enabled in FoldClient will have these modules blocked automatically.

## 3. timeline.json (The Action Script)

This is the heart of the module. It is an array of actions, sorted by timestamp (in seconds). FoldEngine reads this and dispatches instructions to the game server or FoldClient.

The file can be a bare array `[ ... ]` or an object `{ "entries": [ ... ] }`; both are accepted.

### Action Types (implemented in 1.0.0)

| Action               | Target | Description                                    | Parameters                                              |
| -------------------- | ------ | ---------------------------------------------- | ------------------------------------------------------- |
| `SHOW_TEXT`          | Server | Plain broadcast text.                          | `text` (or `url`), `duration`                            |
| `SHOW_SUBTITLE`      | Client | TMP subtitle below the media overlay.          | `text`, `duration`                                       |
| `SHOW_IMAGE`         | Client | Full-screen image overlay.                     | `url` (CDN link, or `test` / `smiley` local images)      |
| `PLAY_VIDEO`         | Client | Full-screen video playback (MP4 / WebM).       | `url`                                                   |
| `PLAY_AUDIO`         | Client | 2D audio playback (OGG / MP3).                 | `url`, `volume`                                          |
| `PLAY_AUDIO_SERVER`  | Server | Global non-spatial sound via LabAPI SpeakerToy. | `url` (WAV: PCM 8/16/24/32 or float32), `volume`, `loop` |
| `CHANGE_ROLE`        | Server | Force a player to switch roles.                | `playerId` (SteamID64, `@steam` optional), `role`       |
| `SPAWN_ITEM`         | Server | Give an item to a player or spawn it in world. | `itemId`, `playerId` **or** `position` (`"x,y,z"`)      |
| `CONTROL_DOOR`       | Server | Open / close / lock / unlock a map door.       | `doorName` (e.g. `HczArmory`), `state`                   |
| `CLEAR`              | Client | Clear the current overlay.                     | none                                                    |

`CONTROL_DOOR` states: `open`, `close`, `lock`, `unlock`, `toggle` (default).

### Targeted Playback

Every timeline entry accepts an optional `target` field:

| Value | Meaning |
| --- | --- |
| `all` (default) | Broadcast to every player. |
| `role:Scp3114` | Only players currently in that role. |
| `player:76561198000000000` | Only that player (`@steam` optional). |

Non-`all` targets are delivered with per-player broadcasts, so only the selected players see the media marker and trigger their FoldClient overlay.

**Signed media markers:** client-bound media actions are signed by FoldEdge with **ECDSA P-256 (SHA-256)** and delivered as:

```text
[FOLD_VIDEO]<unix-milliseconds>|<nonce>|<base64url-signature>|<payload-json>
```

The signature covers `kind + "\n" + payload-json + "\n" + timestamp-ms + "\n" + nonce` (`kind` = `video` / `image` / `audio` / `text`). FoldEngine forwards the marker unchanged; FoldClient verifies it with the embedded public key and rejects signatures that do not match, timestamps outside a 3-second window, or reused nonces.

**Audio note:** `PLAY_AUDIO` (client) supports OGG/MP3 via `UnityWebRequestMultimedia`. `PLAY_AUDIO_SERVER` decodes WAV natively and resamples to 48000 Hz mono. Other server-side codecs must be transcoded to WAV first.

**Role / item ids:** `role` and `itemId` are parsed with `Enum.TryParse` against `PlayerRoles.RoleTypeId` and `InventorySystem.Items.ItemType` (case-insensitive), e.g. `Scp096`, `KeycardO5`.

### Example timeline.json

```json
[
  { "time": 0.0,  "action": "SHOW_TEXT",    "text": "Warning: SCP-001 breach imminent.", "duration": 3.0 },
  { "time": 2.0,  "action": "SHOW_IMAGE",   "url": "https://cdn.fold.com/warning.png" },
  { "time": 5.0,  "action": "PLAY_VIDEO",   "url": "https://cdn.fold.com/intro.webm" },
  { "time": 10.0, "action": "PLAY_AUDIO",   "url": "https://cdn.fold.com/alarm.wav", "volume": 0.8 },
  { "time": 12.0, "action": "CHANGE_ROLE",  "playerId": "76561198000000000", "role": "Scp096" },
  { "time": 13.0, "action": "SPAWN_ITEM",   "itemId": "KeycardO5", "position": "0,1,0" },
  { "time": 14.0, "action": "CONTROL_DOOR", "doorName": "HczArmory", "state": "lock" },
  { "time": 15.0, "action": "SHOW_IMAGE",   "url": "https://cdn.fold.com/3114.png", "target": "role:Scp3114" },
  { "time": 16.0, "action": "CLEAR" }
]
```

## 4. subtitles.json (Optional)

For accessibility and localization, subtitles can be separated from the timeline.

```json
[
  { "time": 5.5, "duration": 3.0, "text": "Warning: SCP-001 breach imminent.", "position": "center" }
]
```

## 5. Security & Licensing

### 5.1 Encrypted packages (FOLDENC1)

An encrypted `.fdll` is a plain ZIP encrypted in memory with AES-256-CBC (PKCS7) and prefixed with the magic bytes `FOLDENC1`:

```text
FOLDENC1 | AES-256-CBC(zip bytes)     # 8-byte ASCII magic, then ciphertext
```

- Key: `Fold-Engine-Preset-AES-Key-2026!` (32 bytes, UTF-8)
- IV: `FoldEngine-IV-01` (16 bytes, UTF-8)
- Mode: CBC, padding PKCS7

FoldEdge detects the magic automatically, decrypts in memory (never writes plaintext to disk), and reads `manifest.json` / `timeline.json`. For encrypted modules the play command is sent to FoldEngine as an AES-encrypted `enc` field instead of plaintext JSON, and the plugin decrypts it in memory.

> **Honest security note:** the preset key ships with the engine, so this is content obfuscation against casual extraction, not strong DRM. Treat it as a speed bump, not a vault.

The repository includes `tools/pack-fdll.ps1` (Windows PowerShell 5.1, no external dependencies):

```powershell
# plain ZIP
.\tools\pack-fdll.ps1 -Manifest .\manifest.json -Timeline .\timeline.json -Output .\FoldEdge\Modules\demo.fdll

# AES-256 encrypted
.\tools\pack-fdll.ps1 -Manifest .\manifest.json -Timeline .\timeline.json -Output .\FoldEdge\Modules\demo.fdll -Encrypt
```

Any language can produce a compatible package: build the ZIP, then apply AES-256-CBC with the key/IV above and prepend `FOLDENC1`.

**Commercial Use:** The `.fdll` module itself is a standalone work. While Fold Engine uses PFN 1.0 (non-commercial), developers retain 100% rights to sell or monetize their `.fdll` modules.

**Disclaimer:** The Fold Engine developers provide this tool "as is" and assume no liability for how server owners or players use the content within `.fdll` modules.

**Last updated: 2026-11-11**
