# Northwood Studios — Fold Security Declaration

**Security Against Man-in-the-Middle (MitM): Even in the native integrated version using Mirror UDP, all Fold commands are cryptographically signed with ECDSA. If a hacker intercepts and modifies the packet to render ESP or unauthorized media, the client will reject the payload entirely. It is physically impossible to abuse Fold for cheating.**

**防中间人劫持：即便是在原生集成版的 UDP 通信中，所有 Fold 指令也都有 ECDSA 签名。黑客即便拦截并篡改数据包，客户端也会拒绝执行。物理上不可能利用 Fold 作弊。**

---

## 1. Command envelope

Every executable Fold command is a signed envelope:

```
[FOLD_VIDEO]<tsMs>|<nonce>|<base64url(DER signature)>|<payloadJson>
```

The signature covers:

```
kind + "\n" + payloadJson + "\n" + timestampMs + "\n" + nonce
```

- Algorithm: ECDSA P-256 + SHA-256
- Signature encoding: DER sequence, Base64Url
- Timestamp window: 3 seconds
- Nonce: single use, replay rejected

## 2. Client verification

`FoldCommandReceiver` is the only executable-command entry point in FVP. Before any
UI or media API is touched, it enforces:

1. Marker and payload are well-formed.
2. ECDSA signature verifies against the pinned public key.
3. Timestamp is within the 3-second window.
4. Nonce has not been seen. The nonce is recorded **only after** signature
   verification succeeds, so invalid packets cannot consume nonces.
5. Local parental-control tags (`18+` / `GORE`) are respected.

Any failure is logged with a reason (`bad-signature`, `expired`, `replay`,
`no-key`, `malformed`) and the command is discarded. There is no plaintext or
"trusted network" fast path.

## 3. Trust anchor

FVP ships with `FoldClient.pub` next to `FoldClient.exe`. It contains the SPKI
public key (Base64 or PEM). This file is the only trust anchor.

- FH may still push its current public key over WebSocket for convenience.
- FVP accepts that key **only if it is byte-for-byte identical to `FoldClient.pub`**.
- If `FoldClient.pub` is missing or invalid, FVP fails closed and rejects every command.
- Key rotation requires distributing a new `FoldClient.pub`; a network attacker
  cannot substitute a key by intercepting the handshake.

## 4. FoldEngine role

FE (FoldEngine) does **not** hold an ECDSA private key and cannot sign commands.

- FE receives signed envelopes from FH and forwards them verbatim.
- FE rejects malformed or unsigned `[FOLD_*]` markers before forwarding.
- FE's server-side native actions (spawning, role changes, CASSIE, doors) are
  server-authoritative gameplay actions and are not client-rendered Fold commands.
- A compromised FE can drop commands, but it cannot forge or modify them without
  the FH private key.

## 5. Transport coverage

The same verifier is used for:

- Current external beta: WebSocket (`ws://` / `wss://`)
- Future native integration: Mirror UDP

The transport is irrelevant to the security decision: the payload is verified
before execution in both cases.

## 6. Scope and limitations

This declaration protects the **Fold command channel**: remote parties cannot
inject, alter, replay, or redirect Fold media commands without the signing key.

It does **not** claim that a modified client binary, a local overlay, or a
separate cheat tool is impossible. A user who controls their own machine can
always patch their client or run other software; no client-side plugin can
prevent that. Fold does not provide, and does not attempt to provide, any
anti-cheat bypass.

## 7. Verification evidence

The repository's `tests/FoldClient.Test` includes automated security checks:

- valid signature accepted
- tampered payload rejected (`bad-signature`)
- expired timestamp rejected (`expired`)
- wrong signing key rejected (`bad-signature`)
- replayed nonce rejected (`replay`)

Run:

```powershell
dotnet run --project .\tests\FoldClient.Test\FoldClient.Test.csproj -c Release
```

---

Repository: https://github.com/xinyantao0803-max/Fold-Engine

---

## 8. Server stability guarantee

**Server Stability Guaranteed: FoldEngine is designed with strict input validation and rate limiting. It is immune to malicious packet flooding and memory overflow exploits that commonly crash other modded servers.**

**保证服务器稳定：FoldEngine 具备严格的输入校验和限流，免疫恶意炸服和内存溢出漏洞。**

### Implemented controls

- **Hard packet caps**: 4 KB for control commands; 256 KB absolute cap for
  encrypted module/timeline payloads. Oversized frames are discarded without
  parsing.
- **Strict format validation**: only the five `[FOLD_*]` prefixes are accepted;
  timestamp, nonce, signature, and payload are all mandatory and length-checked.
- **Exception isolation**: WebSocket parsing, AES decryption, timeline parsing,
  and player command execution are wrapped in try-catch blocks; malformed input
  is logged and dropped.
- **Per-player token bucket**: 5 requests/second at the command entry point.
  Server console is exempt.
- **Queue circuit breaker**: the main-thread action queue is capped at 64
  entries; under burst load the oldest command is dropped.
- **Thread isolation**: JSON/decryption/timeline parsing runs on the WebSocket
  background thread; only game API actions are marshalled back to the Unity
  main thread.

### Scope

These controls target oversized packets, malformed JSON, request flooding, and
main-thread stalls. They do not claim absolute immunity against game-level
vulnerabilities or zero-day exploits outside Fold's own input surface.

---

## 9. Proposal: a Zero-Trust reference protocol for SCP:SL

### 9.1 The transport gap

SCP:SL's native gameplay traffic runs over Mirror's UDP transport. Mirror
provides delivery and ordering, but the application layer does not carry a
per-command signature, a short timestamp window, and a nonce cache by default.
A party that can intercept or inject UDP traffic can therefore attempt to alter
or replay command/RPC payloads when the receiving side does not independently
validate them.

### 9.2 The envelope

Fold already uses an application-layer envelope for every executable command:

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

### 9.3 A native security extension

We propose that Northwood Studios evaluate the same envelope as an optional
application-layer security extension for SCP:SL's native command channels:

1. Sign client→server command/RPC payloads at the application layer.
2. Sign server→client control/UI/media payloads where integrity matters.
3. Distribute the server public key out-of-band and pin it in the client;
   rotate it with an explicit key-distribution step.
4. Keep a bounded nonce cache and a short timestamp window per connection.
5. Keep server-side validation (range checks, ownership, cooldowns) in place.

This does not require replacing Mirror. It is a signed envelope carried inside
the existing transport and can be piloted on a single command family first.

### 9.4 Effect on network-layer cheating

For the **in-transit tampering** vector, the envelope removes the attacker's
ability to alter, forge, or replay a command without the signing key. Forged
teleport, interaction, or UI commands injected into the UDP stream are rejected
before execution.

Scope: this does not stop a modified client from reading data it legitimately
receives (for example ESP-style rendering), and it does not replace server-side
validation. It closes the packet forgery/tampering/replay vector, not every
client-side cheat category.

### 9.5 Open-source reference code

The complete reference implementation is open source and tested:

- [`FoldClient/FoldCommandReceiver.cs`](../FoldClient/FoldCommandReceiver.cs) —
  single verification entry point; rejects malformed, expired, replayed, or
  incorrectly signed commands before execution.
- [`FoldClient/Core/src/SignedMediaCommand.cs`](../FoldClient/Core/src/SignedMediaCommand.cs) —
  P-256 verification, 3-second window, nonce cache, pinned-key enforcement.
- [`tests/FoldClient.Test/SecurityTests.cs`](../tests/FoldClient.Test/SecurityTests.cs) —
  valid, tampered, expired, wrong-key, and replay test cases.
- [`FoldEngine/FoldEdgeClient.cs`](../FoldEngine/FoldEdgeClient.cs) — forwarding
  layer that rejects unsigned or malformed envelopes and never holds a private
  key.

Northwood Studios may extract these files, or the envelope design itself, for
native integration under the commercial license granted in the README.

### 9.6 Suggested pilot

Start with one signed command family (for example media/UI commands), with the
server public key pinned in the client and a 3-second window. Once telemetry is
clean, extend the envelope to movement/interaction RPCs and measure CPU and
bandwidth overhead. P-256 verification is low-cost on modern hardware, and the
nonce cache is bounded.

---

Repository: https://github.com/xinyantao0803-max/Fold-Engine
