---
hide:
  - navigation
  - toc
---

<div class="hero" markdown>

# SCP:SL Fold Mod

**Zero-Trust media and event framework for SCP:SL.**

Not just a media player, but a Zero-Trust network protocol reference for SCP:SL.

[Security Declaration](nw-security.md){ .md-button .md-button--primary }
[NW Review Guide](https://github.com/xinyantao0803-max/Fold-Engine/blob/main/NW-REVIEW.md){ .md-button }
[Native Integration](native-integration.md){ .md-button }

</div>

## Two Integration Solutions

<div class="grid cards" markdown>

-   :material-monitor:{ .lg .middle } **Plan A — External Renderer**

    ---

    **External Renderer · Zero Injection**

    A standalone WPF process using Windows Media Foundation hardware
    decoding. It never touches game memory and requires no game modification.

    - No injection, no BepInEx, no C++ loader
    - ECDSA-verified commands
    - F9 instant stop
    - Available now for server owners

    [Open the external client](https://github.com/xinyantao0803-max/Fold-Engine/tree/main/FoldClient)

-   :material-engine:{ .lg .middle } **Plan B — Native Engine Integration**

    ---

    **Native Engine Integration · For Northwood Studios**

    Pure Unity C# scripts that Northwood Studios can copy directly into the
    official client source tree.

    - Canvas `sortingOrder = 9999`
    - `RawImage` + `VideoPlayer` + `RenderTexture`
    - P-256 verification and replay protection
    - No external launcher, no extra process

    [Open the native module](https://github.com/xinyantao0803-max/Fold-Engine/tree/main/FoldNativeClient)

</div>

## Enterprise Control Plane (v1.4)

- **Dashboard** — `/dashboard` SPA with login, RBAC, module trigger, live logs, and settings.
- **RBAC** — Owner / Admin / Viewer; PBKDF2-SHA256 passwords; HS256 JWT sessions.
- **Unified logs** — daily files, 7-day retention, live bilingual panel.
- **Time-limited access** — expiry revokes the JWT and VPN credential, then triggers an in-game revoke and round restart.
- **Server lifecycle** — start / stop / restart the SCP:SL server from the dashboard; stdout/stderr is captured and merged into the log center.
- **VPN credentials** — native Windows L2TP / IKEv2 profile script with gateway verification.

[Docker deployment](docker.md){ .md-button } [NW Review Guide](https://github.com/xinyantao0803-max/Fold-Engine/blob/main/NW-REVIEW.md){ .md-button }

## Core Security Properties

- **Zero injection** — the external client is a separate WPF process.
- **ECDSA P-256** — every executable command is signed.
- **Pinned public key** — dynamic key substitution is rejected.
- **Replay protection** — 3-second window plus single-use nonce.
- **Anti-flood** — 4 KB control packet cap, 5 req/s token bucket, bounded queue.

## Explore

- [.fdll specification](fdll-spec.md)
- [Security declaration and MitM proposal](nw-security.md)
- [Docker deployment](docker.md)
- [Native integration guide](native-integration.md)
