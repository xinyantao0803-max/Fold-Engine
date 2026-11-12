# Docker Deployment

## One-command FH deployment (v1.3.0)

From the repository root:

```bash
./start.sh
```

`start.sh` verifies Docker, runs `docker compose up -d --build`, waits for
`/api/health`, then prints the dashboard URL. Manual equivalent:

```bash
docker compose up -d --build
```

The All-in-One image contains FoldEdge (FH), the `/dashboard` enterprise control
plane, and FFmpeg for media transcoding. Open:

```
http://127.0.0.1:9000/dashboard
```

The first visit forces Owner initialisation (PBKDF2 password hashing).

Image tag:

```
ghcr.io/xinyantao0803-max/fold-engine:v1.3.0
```

Build and push manually:

```bash
docker build -f FoldEdge/Dockerfile -t ghcr.io/xinyantao0803-max/fold-engine:v1.3.0 .
docker push ghcr.io/xinyantao0803-max/fold-engine:v1.3.0
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
| `./FoldEdge/Data` | `/app/Data` | Users, JWT secret, dashboard config, logs (`Logs/`, 7-day retention) |

## Dashboard

The control plane reads `Modules/*.fdll` in real time and can:

- show online FE / FVP counts and the last telemetry line
- trigger a module (`POST /api/trigger/{module}` → signed play command over WebSocket)
- manage users with Owner / Admin / Viewer roles, expiries, SteamID binding, and VPN credentials
- stream the unified bilingual log center (daily files, 7-day retention)
- toggle FE rate limiting and change FH log level (`PUT /api/config`)

Configuration is persisted to `/app/Data/dashboard-config.json` and broadcast to
connected FE clients as a `{"type":"config"}` message.

## VPN credentials

Folded into the user-management page: creating an account can issue an L2TP or
IKEv2 credential and a Windows `Add-VpnConnection` script. The actual tunnel is
hosted by the gateway configured through `VpnServer` / `VpnTunnelType`; a gateway
can validate credentials with `POST /api/vpn/verify`.

Set `VpnGatewayToken` before exposing the verify endpoint beyond localhost:

```yaml
environment:
  - VpnServer=vpn.example.com
  - VpnTunnelType=L2tp
  - VpnGatewayToken=change-me
```

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

- The dashboard has built-in JWT authentication and Owner/Admin/Viewer RBAC, but
  no TLS or MFA. Put it behind an HTTPS reverse proxy and firewall port 9000.
- Do not publish port 9000 to the public internet.
- Back up `FoldEdge/keys` securely. The private key signs every Fold command.
- Back up `FoldEdge/Data` securely: it contains the user database, JWT secret,
  and VPN credentials.
- The external FVP verifies commands against a pinned `FoldClient.pub`; a
  missing pin file means fail-closed.
