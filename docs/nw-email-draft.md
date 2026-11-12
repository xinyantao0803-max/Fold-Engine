# Email draft — Northwood Studios

**To:** Northwood Studios Security / Whitelist Team  
**Subject:** Proposal: open-source Zero-Trust command envelope for SCP:SL

---

Dear Northwood Studios team,

We maintain **Fold Mod**, an open-source media and event framework for SCP:SL.
The client is an external WPF renderer: it does not inject into the game, does
not use BepInEx, and does not modify game memory. The project is currently
prepared for whitelist review.

Beyond media playback, we would like to share a small, tested security design
that may be useful for SCP:SL's native network layer.

## The gap

SCP:SL's native gameplay traffic runs over Mirror's UDP transport. Mirror
provides delivery and ordering, but the application layer does not carry a
per-command signature, a short timestamp window, and a nonce cache by default.
If an attacker can intercept or inject UDP traffic, command or RPC payloads may
be altered or replayed unless the receiving side independently validates them.

## The proposal

Fold uses an application-layer envelope for every executable command:

```
[FOLD_*]<tsMs>|<nonce>|<base64url(DER signature)>|<payloadJson>
```

Signature input:

```
kind + "\n" + payloadJson + "\n" + timestampMs + "\n" + nonce
```

- ECDSA P-256 + SHA-256
- 3-second acceptance window
- bounded nonce cache, single use
- pinned public key in the client
- fail-closed: missing key or bad signature means no execution

We propose evaluating the same envelope as an optional application-layer
security extension for SCP:SL's native command channels. It can be carried
inside Mirror without replacing it, and piloted on one command family first
(for example media/UI commands).

## What it changes

For the **in-transit tampering** vector, forged, modified, or replayed commands
are rejected before execution. This closes the packet forgery/tampering/replay
vector.

Scope: this is not a substitute for server-side validation, and it does not
stop a modified client from reading data it legitimately receives (for example
ESP-style rendering). It addresses command authenticity and freshness, not every
client-side cheat category.

## Reference implementation

The complete implementation is open source and tested:

- `FoldNativeClient/FoldCommandReceiver.cs` — single verification entry point
- `FoldNativeClient/SignedMediaCommand.cs` — P-256, 3-second window, nonce cache, pinned key
- `FoldEdge/MediaSigner.cs` — server-side envelope producer
- `FoldEngine/FoldEdgeClient.cs` — unsigned or malformed envelopes are rejected; FE never holds a private key
- `docs/nw-security.md` — full security declaration

Repository: https://github.com/xinyantao0803-max/Fold-Engine

## License

The main engine uses PFN 1.0 (noncommercial). We have granted Northwood Studios
a free, irrevocable commercial license to integrate this client rendering code
into the official SCP:SL source code. The same grant covers extracting the
envelope and verification code for native use.

We would be glad to provide a patch, a test harness, or a technical walkthrough
if this direction is useful.

Best regards,  
Fold Team  
https://github.com/xinyantao0803-max/Fold-Engine
