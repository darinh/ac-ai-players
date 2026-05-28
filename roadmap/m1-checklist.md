# M1 checklist

The headless-player milestone. Done when every box here is checked.

See [`milestones.md`](milestones.md) for the definition of M1 and its
success criterion.

## Original deliverables

- [x] ACE fork with the minimum hook needed to instantiate a `BotPlayer`
- [x] Console command (`/spawnplayerbot`, GM-only) that creates one at
      the caller's location — see
      [#6](https://github.com/darinh/ac-ai-players/issues/6) and
      [#34](https://github.com/darinh/ac-ai-players/issues/34)
- [x] Bot appears in `/who`, in the world, with a name

## Capabilities shipped (M1 spike on `botplayer-spike`)

Tracked as `M1: ...` issues. All closed-on-creation with commit links.

- [x] [#6](https://github.com/darinh/ac-ai-players/issues/6) — `BotCreature` spawning + `/spawnbot` (superseded by BotPlayer below)
- [x] [#7](https://github.com/darinh/ac-ai-players/issues/7) — `/botdirector` command surface (populate/clear/follow/reload)
- [x] [#8](https://github.com/darinh/ac-ai-players/issues/8) — per-archetype chat/greetings/tell replies/emotes
- [x] [#9](https://github.com/darinh/ac-ai-players/issues/9) — bot persistence (TSV roster save/load/auto-save)
- [x] [#10](https://github.com/darinh/ac-ai-players/issues/10) — hot-reload of bot data via JSON overlay
- [x] [#11](https://github.com/darinh/ac-ai-players/issues/11) — `autoSpawnFromRoster` on world open
- [x] [#12](https://github.com/darinh/ac-ai-players/issues/12) — heritage-based bot name generator
- [x] [#13](https://github.com/darinh/ac-ai-players/issues/13) — greeter mute on bot spawn
- [x] [#14](https://github.com/darinh/ac-ai-players/issues/14) — Windows Service deployment via NSSM
- [x] [#15](https://github.com/darinh/ac-ai-players/issues/15) — `/tell` → bot reply routing (BotCreature variant — superseded by [#44](https://github.com/darinh/ac-ai-players/issues/44))

## Bots-as-real-players migration parity (epic [#27](https://github.com/darinh/ac-ai-players/issues/27))

ADR-0003 (BotCreature) was reversed by ADR-0007 (BotPlayer). M1 is not
considered closed until BotPlayer reaches parity with the BotCreature
capabilities above.

- [x] [#28](https://github.com/darinh/ac-ai-players/issues/28) — E0: ADR-0008 tick mechanism
- [x] [#29](https://github.com/darinh/ac-ai-players/issues/29) — E1: NetworkSession virtualization audit
- [x] [#30](https://github.com/darinh/ac-ai-players/issues/30) — E2: `BotSession` + `BotNetworkSession` scaffold
- [x] [#31](https://github.com/darinh/ac-ai-players/issues/31) — E3: `Character.IsBot` + `IPlayer.IsBot`
- [x] [#32](https://github.com/darinh/ac-ai-players/issues/32) — E4: `BotPlayer` + lifecycle
- [x] [#33](https://github.com/darinh/ac-ai-players/issues/33) — E5: `BotManager` migration (feature-flagged)
- [x] [#34](https://github.com/darinh/ac-ai-players/issues/34) — E6: `/spawnplayerbot` + `/botdirector`
- [x] [#35](https://github.com/darinh/ac-ai-players/issues/35) — E7: per-archetype chat + `/tell` + emote shims + aggro fix + gender randomization
- [x] [#36](https://github.com/darinh/ac-ai-players/issues/36) — E8: follow-target
- [x] [#39](https://github.com/darinh/ac-ai-players/issues/39) — `ForceLogoff` routes through bot teardown
- [x] [#40](https://github.com/darinh/ac-ai-players/issues/40) — Rehydrate persisted `BotPlayer` on world open
- [x] [#43](https://github.com/darinh/ac-ai-players/issues/43) — purple-haze fix (`SimulateClientLoginComplete` on spawn + post-teleport rising-edge)
- [x] [#44](https://github.com/darinh/ac-ai-players/issues/44) — `/tell` → `BotPlayer.OnReceivedTell` (both `GameActionTell` and `GameActionTalkDirect` player branches)
- [ ] [#37](https://github.com/darinh/ac-ai-players/issues/37) — E9: re-validate all M1 capabilities on the BotPlayer substrate
      (code-audit ✅; **awaiting in-game smoke from user**)
- [ ] [#38](https://github.com/darinh/ac-ai-players/issues/38) — E10: remove `BotCreature`; flip ADR-0003 to retired; promote
      ADR-0007 to Accepted
      (blocked on E9 in-game smoke)

## M1-done definition (proposed by [#26](https://github.com/darinh/ac-ai-players/issues/26))

- [x] (a) Bot stands there
- [x] (b) Bot has an identity that persists across reboots (#9 + #11 + #40)
- [x] (c) Bot can be spawned and despawned by an admin (#6 + #7 + #34 + #39)
- [x] (d) Hot-reload works for tuning data (#10)
- [x] (e) Auto-spawn from saved roster works (#11 + #40)
- [ ] (f) The bot is a real `Player`, not a `Creature`
      — code-complete on `botplayer-spike`, awaiting E9 in-game smoke +
      E10 BotCreature removal

## Outstanding

- In-game smoke pass for E9 ([#37](https://github.com/darinh/ac-ai-players/issues/37))
- Final removal of `BotCreature` and ADR flips ([#38](https://github.com/darinh/ac-ai-players/issues/38))
- M1 milestone closeout meta-issue [#26](https://github.com/darinh/ac-ai-players/issues/26) closes after the above

## Final deliverable

The original success criterion for M1: "A human player logs in, runs
`/spawnbot`, and sees another character standing there with no client
connection backing it."

- [x] `/spawnbot` (and follow-up `/spawnplayerbot`) ship and work end-to-end
- [x] Bot is visible in-world to other players
- [x] Bot has no client connection (BotSession + BotNetworkSession scaffold)
- [ ] User has run the full E9 in-game smoke pass to confirm the
      BotPlayer migration is parity-complete

M1 closes once the BotPlayer migration's in-game smoke is signed off.
