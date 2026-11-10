# 🚀 SCP:SL Fold Engine
The Next-Gen Media & Module Framework for SCP:SL.

Fold Engine is a next-generation media & module framework for SCP: Secret Laboratory,
built on two cooperating components:

- **FoldEngine** — an in-game plugin powered by **LabAPI** (.NET Framework 4.8). It plays
  `.fdll` module timelines as SLAC-safe "pixel-monitor-style" text broadcasts through the
  game's Hint system, keeping payloads tiny and server-friendly.
- **FoldEdge** — an external **ASP.NET Core (.NET 8)** host listening on `127.0.0.1:9000`.
  It watches the `Modules` folder for `.fdll` packages, exposes a status API, and pushes
  play triggers to the in-game plugin over WebSocket.

Modules are plain data packages described in [docs/fdll-spec.md](docs/fdll-spec.md):

- `manifest.json` — metadata (id, name, tags)
- `timeline.json` — a timed event list (time, action, CDN url)

The server-side renderer never pushes high-resolution images (SLAC constraint); instead it
uses "pixel-monitor-style" ASCII frames or plain-text broadcasts.

## Quick Start

```bash
# FoldEdge (external host)
cd FoldEdge
dotnet run            # listens on http://127.0.0.1:9000

# FoldEngine (game plugin)
# 1. Build against LabAPI.dll referenced by FoldEngine.csproj
# 2. Drop the built DLL into the game's plugin folder
# 3. In-game server console: fold
```

## API

| Method | Route                   | Description                       |
| ------ | ----------------------- | --------------------------------- |
| GET    | `/api/status`           | Runtime status + loaded modules   |
| POST   | `/api/trigger/{module}` | Push a play command to the plugin |
| GET    | `/ws`                   | WebSocket endpoint for the plugin |
