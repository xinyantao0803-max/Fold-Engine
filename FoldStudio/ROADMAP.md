# FoldStudio (FS) — Roadmap

**FoldStudio is an independent, MIT-licensed media authoring tool. It is not part of this
repository, is not covered by PFN 1.0, and is not required to run Fold Mod.**

FoldStudio is the authoring half of the ecosystem: Fold Mod plays `.fdll` modules, FoldStudio
creates them. This document describes the planned product so reviewers can see the full roadmap,
the UI layout, and the open-source commitments up front. Nothing here is shipped yet.

---

## 1. Product shape

| | |
| --- | --- |
| Name | FoldStudio (FS) |
| Type | Desktop authoring tool (Windows first) |
| Licence | **MIT** |
| Output | `.fdll` modules, MP4, PNG, timeline JSON |
| Relationship to Fold | Produces data only; Fold Mod remains the runtime |

Design rule: FoldStudio never needs to know how a server is configured, and Fold Mod never needs to
know how a module was authored. The contract is the `.fdll` format documented in
[`docs/fdll-spec.md`](../docs/fdll-spec.md).

---

## 2. Planned UI layout

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  FoldStudio        File  Edit  View  Module  Render  Help          [ MIT ]   │
├───────────────┬──────────────────────────────────────────────┬───────────────┤
│  ASSET LIBRARY│                  PREVIEW                     │   INSPECTOR   │
│               │                                              │               │
│  ▸ Video      │   ┌────────────────────────────────────┐     │  Clip         │
│    intro.mp4  │   │                                    │     │  ├─ start 0.0 │
│    loop.mp4   │   │        3D VIEWPORT / 2D STAGE      │     │  ├─ dur  4.2  │
│  ▸ Image      │   │                                    │     │  ├─ fade 0.3  │
│    alarm.png  │   │   (VLC preview surface + gizmo)    │     │  └─ layer  1  │
│  ▸ Audio      │   │                                    │     │               │
│    siren.wav  │   └────────────────────────────────────┘     │  Trigger      │
│  ▸ Models     │                                              │  ├─ type CODE │
│    door.glb   │   ┌────────────────────────────────────┐     │  ├─ value     │
│  ▸ Fonts      │   │  TIMELINE                          │     │  └─ priority │
│               │   │  00:00   00:05   00:10   00:15     │     │               │
│               │   │  video  ████████████               │     │  Tags         │
│               │   │  image        ████                 │     │  [18+] [GORE] │
│               │   │  audio  ▒▒▒▒▒▒▒▒▒▒▒▒               │     │               │
│               │   │  event      ◆        ◆             │     │  Export       │
│               │   └────────────────────────────────────┘     │  ▸ .fdll      │
├───────────────┴──────────────────────────────────────────────┴───────────────┤
│  Ready · 00:12.4 / 00:30.0 · 60 fps preview · Render queue: idle             │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Asset library** — drag-and-drop media bin, filters per type, search, duplicate detection.
- **Preview** — 3D viewport with orbit camera for model assets, 2D stage for image/video
  compositing; the same scene graph is used for the final render.
- **Timeline** — multi-track (video / image / audio / event), snapping, scrub, loop regions,
  per-clip fades and layer ordering. Event markers map directly to `.fdll` timeline actions.
- **Inspector** — clip properties, trigger conditions (`CODE`, round phase, player count, …),
  priority, mandatory content tags, and validation warnings.
- **Export panel** — one-click `.fdll`, plus MP4/PNG renders for trailers and documentation.

---

## 3. Capability plan

### 3.1 Editing and preview

- GPU-composited timeline with frame-accurate scrubbing.
- **VLC (LibVLCSharp) preview** for broad codec coverage during authoring; final `.fdll` keeps the
  original media so the runtime picks the platform decoder.
- Waveform display and audio scrubbing.
- Undo/redo, autosave, project files (`.fsproj`, JSON-based, human-diffable).

### 3.2 3D

- glTF / GLB import with PBR materials.
- Scene graph with translate/rotate/scale gizmos.
- Camera keyframes and simple lighting rigs for pre-rendered cutscenes.
- Optional offline render to MP4 for servers that prefer video over real-time assets.

### 3.3 Export

- `.fdll` writer compatible with `tools/FoldPacker` (manifest, timeline, media, tags).
- Optional `--encrypt` (`FOLDENC1`) and detached ECDSA signature support.
- MP4 (H.264) and PNG sequence export.
- Validation pass: missing media, oversized payloads, illegal module names, tag enforcement.

### 3.4 Not planned

- No server administration, no remote control of live servers, no gameplay automation.
- No anti-cheat interaction of any kind.
- No closed-source components in the core editor.

---

## 4. Milestones

| Milestone | Scope | Status |
| --- | --- | --- |
| M0 — Specification | `.fdll` schema frozen, validated against Fold Mod | Done (in `docs/fdll-spec.md`) |
| M1 — Shell | Avalonia/WPF shell, project save/load, asset import | Planned |
| M2 — Timeline | Multi-track timeline, event markers, validation | Planned |
| M3 — Preview | VLC video preview, image compositing, audio scrub | Planned |
| M4 — 3D | glTF import, viewport, camera keyframes | Planned |
| M5 — Export | `.fdll` writer, encryption/signature pass-through, MP4/PNG | Planned |
| M6 — Public beta | MIT release, sample projects, documentation site | Planned |

---

## 5. Open-source commitment

- Core editor: **MIT**, public repository, no contributor licence agreement required.
- Project file format and `.fdll` writer are documented and versioned.
- Sample modules (text, image, video, timeline) ship with the repository under MIT.
- Fold Mod's PFN 1.0 licence applies only to this repository and does not extend to FoldStudio.

---

## 6. Why this matters to reviewers

FoldStudio demonstrates that the ecosystem is designed as a complete pipeline, not a single
player:

```
FoldStudio  ──.fdll──▶  FH (FoldEdge)  ──signed envelope──▶  FE / FVP / FoldNativeClient
 authoring                rules + signing                        rendering
 (MIT, independent)       (PFN 1.0)                              (PFN 1.0)
```

Content creators get a real editor, server owners get a deterministic runtime, and Northwood gets
a native integration path that does not depend on any of it.
