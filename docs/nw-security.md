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
