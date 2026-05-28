# AC1 Protocol Specification

**Status:** v0.1 — covers UDP transport, packet framing,
cryptography, and the login handshake. Game messages and world
state are stubs that grow as the headless-client spike advances.

**Audience:** an engineer who wants to implement a working AC1
client from scratch using only this spec and the ACE server
source as ground truth. No prior protocol knowledge assumed.

**Source of truth:** the [ACE-bots](https://github.com/darinh/ACE-bots)
codebase under `Source/ACE.Server/Network/` and
`Source/ACE.Common/Cryptography/`. Every claim in this spec
should be backed by a `file:line` citation to that source. Where
the spec disagrees with the source, the source wins.

## Reading order

| # | File | Contents | Status |
|---|---|---|---|
| 01 | [01-overview.md](01-overview.md) | High-level protocol model, layer boundaries | ✅ |
| 02 | [02-network.md](02-network.md) | UDP transport, packet framing, headers, sequencing | ✅ partial |
| 03 | [03-crypto.md](03-crypto.md) | Hash32, ISAAC, CryptoSystem, checksum formulas | ✅ |
| 04 | [04-handshake.md](04-handshake.md) | Three-leg login, state machine, account auto-create | ✅ |
| 05 | [05-data-types.md](05-data-types.md) | Strings, primitives, common wire encodings | ✅ |
| 06 | [06-game-messages.md](06-game-messages.md) | BlobFragments, GameMessage dispatch, common opcodes | 🚧 stub |
| 07 | [07-world-state.md](07-world-state.md) | Object spawns, positions, vitals, inventory | 🚧 stub |
| 99 | [99-references.md](99-references.md) | File citations, related projects, terminology | ✅ |

## Conventions

- All multi-byte integers are little-endian unless noted.
- Bit positions are LSB-first (bit 0 = `0x1`).
- Field offsets are zero-based byte offsets from the start of
  the enclosing structure.
- `u8`, `u16`, `u32`, `u64` = unsigned little-endian. `i*`
  = signed. `f32`, `f64` = IEEE-754 little-endian.
- `bytes[N]` = N raw bytes, no endianness.
- `string16L`, `string32L` = AC's length-prefixed string types
  (see [05-data-types.md](05-data-types.md)).
- Server logs cited in this spec are from
  `C:\ACE\Logs\ACE_Log.txt` on the spike's dev host.

## Status legend

- ✅ Documented and verified against working spike code.
- ✅ partial — covers what the spike currently uses; more to add.
- 🚧 stub — placeholder; will fill in as the spike progresses.
- ⚠ inferred — derived from server code; not yet verified by an
  on-the-wire round trip.

## Version history

- v0.1 (2026-05-28) — initial drop after Phase 1 handshake
  passed against a live ACE dev server.
