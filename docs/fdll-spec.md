# Fold Engine `.fdll` Module Specification

**Version: 1.0.0 (Draft)**
**License: PolyForm Noncommercial 1.0.0 (PFN 1.0)**

The `.fdll` file is the core data container for Fold Engine. It is a self-contained, encrypted (optional) package that includes metadata, timelines, subtitles, and all associated media assets.

## 1. Package Structure

A standard `.fdll` file is essentially a ZIP archive (often with a custom extension for encryption). Upon extraction, it should contain the following structure:

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

### Action Types

| Action Type  | Target | Description                             | Key Parameters                     |
| ------------ | ------ | --------------------------------------- | ---------------------------------- |
| PLAY_VIDEO   | Client | Full-screen video playback (WebM/MP4).  | url (CDN link)                     |
| SHOW_IMAGE   | Client | Full-screen image overlay.              | url (CDN link), duration           |
| PLAY_AUDIO   | Client | Background music or 2D audio.           | url, volume                        |
| SHOW_TEXT    | Client | Display rich text (subtitles/broadcast).| text, duration                     |
| CHANGE_ROLE  | Server | Force a player to switch roles.         | playerId, role (e.g., "Scp173")    |
| SPAWN_ITEM   | Server | Spawn a specific item in the world.     | itemId, position                   |
| CONTROL_DOOR | Server | Open, close, or lock a door.            | doorName, state                    |

### Example timeline.json

```json
[
  { "time": 0.0, "action": "PLAY_VIDEO", "url": "https://cdn.fold.com/intro.webm" },
  { "time": 5.5, "action": "SHOW_TEXT", "text": "Warning: SCP-001 breach imminent.", "duration": 3.0 },
  { "time": 10.0, "action": "CHANGE_ROLE", "playerId": "765611980...", "role": "Scp096" },
  { "time": 10.0, "action": "PLAY_AUDIO", "url": "https://cdn.fold.com/roar.ogg", "volume": 0.8 }
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

**Encryption:** Developers may encrypt `.fdll` packages (e.g., AES-256) to protect their intellectual property. FoldEdge requires the decryption key to be provided via the Web API.

**Commercial Use:** The `.fdll` module itself is a standalone work. While Fold Engine uses PFN 1.0 (non-commercial), developers retain 100% rights to sell or monetize their `.fdll` modules.

**Disclaimer:** The Fold Engine developers provide this tool "as is" and assume no liability for how server owners or players use the content within `.fdll` modules.

**Last updated: 2026-11-11**
