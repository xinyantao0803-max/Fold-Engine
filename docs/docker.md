# Docker Deployment

## One-command FH deployment (v1.2)

From the repository root:

```bash
docker compose up -d --build
```

The All-in-One image contains FoldEdge (FH), the `/dashboard` Web UI, and FFmpeg
for media transcoding. Open:

```
http://127.0.0.1:9000/dashboard
```

Image tag:

```
ghcr.io/xinyantao0803-max/fold-engine:v1.2
```

Build and push manually:

```bash
docker build -f FoldEdge/Dockerfile -t ghcr.io/xinyantao0803-max/fold-engine:v1.2 .
docker push ghcr.io/xinyantao0803-max/fold-engine:v1.2
```

Logs:

```bash
docker compose logs -f foldedge
```

## Why host networking

`docker-compose.yml` uses:

```yaml
network_mode: "host"
```

This avoids an extra UDP/NAT hop between the SCP:SL server and FH. It also lets
the game server reach `127.0.0.1:9000` directly. Because host networking ignores
`ports:`, FH binds `0.0.0.0:9000` inside the container (`ListenUrl`) and occupies
host port 9000 directly.

> Host networking is intended for a Linux host or a Linux VM. On Docker Desktop
> for Windows/macOS, remove `network_mode: "host"`, add `ports: ["9000:9000"]`,
> and point `FoldEngine` at `ws://<host-ip>:9000/ws` instead of `127.0.0.1`.

## Volumes

| Host path | Container path | Purpose |
| --- | --- | --- |
| `./FoldEdge/keys` | `/app/keys` | ECDSA key pair; never commit the private key |
| `./FoldEdge/Modules` | `/app/Modules` | `.fdll` modules |
| `./FoldEdge/Assets` | `/app/Assets` | Media served at `/Assets` |
| `./FoldEdge/Media` | `/app/Media` | Uploaded media |
| `./FoldEdge/Data` | `/app/Data` | Dashboard config (`dashboard-config.json`) |

## Dashboard

The Web UI reads `Modules/*.fdll` in real time and can:

- show online FE / FVP counts and the last telemetry line
- trigger a module (`POST /api/trigger/{module}` → signed play command over WebSocket)
- toggle FE rate limiting and change FH log level (`PUT /api/config`)

Configuration is persisted to `/app/Data/dashboard-config.json` and broadcast to
connected FE clients as a `{"type":"config"}` message.

## SCP:SL game server

A reliable SCP:SL server image depends on the server distribution and LabAPI
version you use. This repository does not ship a fake or untested game-server
image. Add your existing server service to `docker-compose.yml`, keep
`network_mode: "host"`, and point FoldEngine's `FoldEdgeUrl` at:

```
ws://127.0.0.1:9000/ws
```

## Client (FVP)

The WPF client is Windows-native and cannot run in Docker. Each player runs
`FoldClient.exe` locally with `FoldClient.pub` next to it. See the README section
"场景三：客户端 FVP".

## Security

- Do not publish port 9000 to the public internet. The dashboard has no built-in
  authentication; use a firewall, an authenticating reverse proxy, or a VPN.
- Back up `FoldEdge/keys` securely. The private key signs every Fold command.
- The external FVP verifies commands against a pinned `FoldClient.pub`; a
  missing pin file means fail-closed.
