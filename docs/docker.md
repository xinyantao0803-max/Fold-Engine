# Docker Deployment

## One-command FH deployment

From the repository root:

```bash
docker compose up -d --build
```

FH listens on `http://127.0.0.1:9000` and exposes the WebSocket endpoint:

```
ws://127.0.0.1:9000/ws
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
the game server reach `127.0.0.1:9000` directly.

## Volumes

| Host path | Container path | Purpose |
| --- | --- | --- |
| `./FoldEdge/keys` | `/app/keys` | ECDSA key pair; never commit the private key |
| `./FoldEdge/Modules` | `/app/Modules` | `.fdll` modules |
| `./FoldEdge/Assets` | `/app/Assets` | Media served at `/Assets` |
| `./FoldEdge/Media` | `/app/Media` | Uploaded media |

## SCP:SL game server

A reliable SCP:SL server image depends on the server distribution and LabAPI
version you use. This repository does not ship a fake or untested game-server
image. Add your existing server service to `docker-compose.yml`, keep
`network_mode: "host"`, and point FoldEngine's `FoldEdgeUrl` at:

```
ws://127.0.0.1:9000/ws
```

## Security

- Do not publish port 9000 to the public internet. Use localhost, a private
  network, or a VPN.
- Back up `FoldEdge/keys` securely. The private key signs every Fold command.
- The external FVP verifies commands against a pinned `FoldClient.pub`; a
  missing pin file means fail-closed.
