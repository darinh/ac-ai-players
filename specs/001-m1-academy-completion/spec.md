# Feature Specification: M1 — autonomous Training Academy completion

**Feature Branch**: `anvil/spec-kit-bootstrap`

**Created**: 2026-05-29

**Status**: Draft

**Input**: User description: "M1 — autonomous Training Academy completion"

## User Scenarios & Testing *(mandatory)*

M1 is successful when a level-1 bot, created with a user-selected ACE-supported build/template, completes the Training Academy like a real player: starts at the canonical Academy entrance, opens required doors, gives and receives required items, completes the verified Academy quest chain, equips the best usable gear it has earned or started with, and walks through the Academy exit portal. ACE-supported stock templates are documented in the AC mechanics textbook §4; canonical Training Academy spawn is documented in §1 and §9; the verified Academy quest chain and exit are documented in §5 and §8.

The spec describes **what** must be true and **why**. Technical approach belongs in `/speckit.plan` after the user resolves the open clarifications.

### User Story 1 - Start a level-1 bot in the canonical tutorial (Priority: P1)

As the product owner, I can start a new level-1 bot with a chosen ACE-supported build/template and see it begin at the Training Academy entrance for its selected starter track, not at a post-Academy town position. ACE-supported templates are listed in textbook §4. `PlayerFactory` sets new-character location from `CharGen.StarterAreas[StartArea].Locations[0]`, which is the Training Academy entrance for the chosen track, per textbook §1 and §9.

**Why this priority**: The user’s Academy litmus cannot be tested while Pilot-01 starts in outdoor Holtburg instead of the Training Academy. Holtburg outdoor landblock identity is documented in textbook §2, and the current open spawn problem is summarized in the handoff under “What’s still TRUE and OPEN.”

**Independent Test**: Create a fresh M1 bot with a user-selected ACE-supported template/build. The evidence bundle shows the bot begins the run at the Training Academy entrance for the chosen track, without admin teleport, admin-cursor placement, or hand-fed inventory outside `starterGear.json` (textbook §1, §3, §4, §9; draft constitution §IV).

**Acceptance Scenarios**:

1. **Given** ACE new-character creation uses `StarterAreas[StartArea].Locations[0]` for canonical spawn (textbook §1, §9), **When** a fresh M1 bot is created, **Then** the bot starts at the Training Academy entrance for the selected track.
2. **Given** the current handoff identifies admin-cursor spawn override and first-tick migration as unresolved causes of non-Academy placement, **When** the M1 run begins, **Then** the run evidence shows the bot is not starting from outdoor Holtburg or any other post-Academy fallback position (textbook §2, §9; handoff “What’s still TRUE and OPEN”).

---

### User Story 2 - Complete Academy quest interactions (Priority: P1)

As the product owner, I can watch the bot progress through the verified Training Academy quest chain by perceiving NPC dialogue, doors, items, and rewards, then acting on that information. The verified chain is Society Greeter → Samuel → Training Master → Academy Foreman → Academy Blacksmith → Academy Researcher → Senior Guard → Sentry → exit portal (textbook §5).

**Why this priority**: The user’s M1 litmus explicitly requires tutorial navigation, door opening, give/receive item interactions, and quest completion. The relevant Academy mechanics are documented in textbook §5 and §7.

**Independent Test**: Start an M1 run in the Training Academy. The evidence bundle maps each required Academy interaction to observed in-game state and timestamped proof of quest progression.

**Acceptance Scenarios**:

1. **Given** every new character receives a Calling Stone (wcid 5084) in starter inventory (textbook §3) and the Society Greeter uses that item to open the east door (textbook §5, §7), **When** the bot reaches the Society Greeter, **Then** it gives the Calling Stone, receives progression, and the door opens.
2. **Given** the verified Academy sequence includes Samuel, Training Master, Academy Foreman, Academy Blacksmith, Academy Researcher, Senior Guard, and Sentry (textbook §5), **When** the bot receives each objective, **Then** it completes the required give/receive item or quest step before moving to the next required step.
3. **Given** the Sentry step rewards Academy Coat and Facility Hub Portal Gem after the Protection Orb objective (textbook §5), **When** the bot completes the Sentry objective, **Then** the evidence bundle records the reward receipt and quest completion.

---

### User Story 3 - Equip best available gear before leaving (Priority: P2)

As the product owner, I can see the bot equip the best usable armor and weapons it has in inventory before leaving the Training Academy. Starter inventory is documented in textbook §3, and Academy quest rewards and weapon upgrade flow are documented in textbook §5.

**Why this priority**: The user’s M1 litmus includes equipping the best armor and weapons available before exiting the tutorial.

**Independent Test**: During an Academy run, compare the bot’s inventory and equipped gear before exit. The evidence bundle shows equipped gear came only from starter inventory or Academy interactions documented in textbook §3 and §5.

**Acceptance Scenarios**:

1. **Given** the bot has starter gear and any Academy rewards it earned (textbook §3, §5), **When** it evaluates usable inventory before exit, **Then** it equips the best currently available armor and weapons by perceived in-game item properties rather than by hardcoded NPC, waypoint, or item-name scripts.
2. **Given** the draft constitution forbids hand-fed inventory outside PlayerFactory starter gear, **When** the bot exits the Academy, **Then** every equipped item is traceable to starter inventory or Academy progression (textbook §3, §5; draft constitution §IV).

---

### User Story 4 - Leave through the Academy exit portal (Priority: P2)

As the product owner, I can watch the bot complete the Academy and voluntarily walk through the exit portal to the chosen starter town. The exit portal is a walk-through portal after Academy completion, not automatic progression, per textbook §8; Training Academy tracks exit to the chosen town per textbook §1 and §5.

**Why this priority**: Walking through the portal is the final M1 tutorial-completion checkpoint.

**Independent Test**: Run the bot through the Academy after required quests are complete. The evidence bundle shows the bot perceives the exit portal, walks into it, and arrives in the selected starter town (textbook §1, §5, §8).

**Acceptance Scenarios**:

1. **Given** the Training Academy exit is a voluntary walk-through portal after Academy tasks are complete (textbook §8), **When** the bot has completed the required Academy chain, **Then** it walks through the exit portal without operator help.
2. **Given** the Academy exits to the chosen starter town track (textbook §1, §5), **When** portal travel completes, **Then** the bot is in the selected starter town and M1 ends.

---

### Edge Cases

- Existing persisted bots that are already outside the Training Academy must not be silently treated as successful M1 runs. The handoff identifies Pilot-01 as currently outside the Academy; the spawn correction is a required clarification before planning.
- If a perceived NPC, item, landblock, or quest step is not in the textbook or a primary source, it is `UNVERIFIED`. The agent must research and record it before acting, per draft constitution §I.
- An optional Academy Exit Token bypass exists in the Academy (textbook §5), but it is not acceptable for M1 success unless the user explicitly changes the goal. M1 is full tutorial completion, not skipping the Academy.
- The spec must not depend on hallucinated NPC or item labels identified in the handoff and textbook. Unsupported labels must remain out of scope unless they are later verified in the textbook or a stronger primary source (textbook §6, §7).
- If a required Academy door, item handoff, reward, or portal interaction fails, absence of an error is not success. A successful claim needs fresh log evidence, per draft constitution §V.
- Different starter-town tracks may have different Academy instances. The bot must use the selected track’s canonical Training Academy entrance rather than assuming Holtburg unless Holtburg is the selected track (textbook §1, §9).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST support starting a new level-1 bot with a user-selected ACE-supported character template/build using only templates verified in textbook §4; unsupported labels are not valid inputs.
- **FR-002**: System MUST start the M1 bot at `CharGen.StarterAreas[StartArea].Locations[0]`, the Training Academy entrance for the selected track, not at the post-Academy town fallback or outdoor Holtburg position (textbook §1, §2, §9).
- **FR-003**: System MUST avoid Academy shortcuts: no teleport, no admin-cursor override, no hand-fed inventory outside PlayerFactory starter gear, and no operator-directed quest progression (textbook §3; draft constitution §IV).
- **FR-004**: System MUST progress from perceived game state. NPC names, item names, and waypoints may appear in perception and LLM-compiled Plans, but deterministic code paths must not hardcode “walk N meters, talk to NPC X” instructions (draft constitution §IV).
- **FR-005**: System MUST complete the required Training Academy sequence through Society Greeter, Samuel, Training Master, Academy Foreman, Academy Blacksmith, Academy Researcher, Senior Guard, Sentry, and the exit portal (textbook §5, §8).
- **FR-006**: System MUST complete required give/receive item interactions, including giving the Calling Stone (wcid 5084) to the Society Greeter and receiving quest rewards needed for progression (textbook §3, §5, §7).
- **FR-007**: System MUST equip the best currently available usable armor and weapons from starter gear and Academy-earned items before exit (textbook §3, §5).
- **FR-008**: System MUST walk through the Training Academy exit portal after required Academy completion, because the portal is voluntary walk-through progression, not automatic progression (textbook §8).
- **FR-009**: System MUST produce an evidence bundle for each acceptance criterion using fresh timestamped `C:\ACE\Logs\ACE_Log.txt` lines or stronger primary evidence; absence of an error is not success (draft constitution §V).
- **FR-010**: System MUST NOT identify or depend on hallucinated NPC or item labels identified in the handoff and textbook. Unsupported labels must remain out of scope unless they are later verified in textbook §6, §7, or a stronger primary source.
- **FR-011**: System MUST NOT count the optional Academy Exit Token bypass as M1 completion unless the user explicitly revises this spec, because M1 requires completing the tutorial Academy path (textbook §5; handoff user litmus list).

### Clarifications Required Before `/speckit.plan`

- **CL-001 [NEEDS CLARIFICATION]**: Spawn-fix Option 1 — Should M1 delete/disable the first-tick migration and stop `BotPlayerFactory` from overwriting PlayerFactory’s canonical Training Academy spawn, accepting that existing bots must be re-spawned to enter the Academy? This option comes from the handoff “What’s still TRUE and OPEN” section and relies on canonical spawn behavior in textbook §1 and §9.
- **CL-002 [NEEDS CLARIFICATION]**: Spawn-fix Option 2 — Should M1 keep the migration behavior but retarget it to the Training Academy entrance for the chosen track, preserving an active recovery path for stranded or rehydrated bots? This option comes from the handoff “What’s still TRUE and OPEN” section and is related to the draft analysis in textbook §10.
- **CL-003 [NEEDS CLARIFICATION]**: Spawn-fix Option 3 — Should M1 do both: canonical Academy spawn on creation and an active Training Academy pull on rehydrate? This option comes from the handoff “What’s still TRUE and OPEN” section and must be chosen or rejected by the user before planning.

### Key Entities *(include if feature involves data)*

- **Bot Character**: The level-1 ACE player-bot running M1 with a user-selected supported template/build (textbook §4).
- **Training Academy Run**: One end-to-end attempt from canonical Academy spawn to exit portal arrival in the selected starter town (textbook §1, §5, §8, §9).
- **Academy Quest Objective**: A required Academy interaction, handoff, reward, or portal step from the verified quest chain (textbook §5, §7, §8).
- **Inventory and Equipment State**: The bot’s starter items, Academy-earned items, and equipped usable gear, constrained to documented starter gear and Academy rewards (textbook §3, §5).
- **Evidence Bundle**: The collection of timestamped log lines or stronger primary evidence proving each acceptance criterion, consistent with draft constitution §V.

### Out of Scope

- Post-Academy outdoor questing, lifestone attunement, low-level mob hunting, corpse looting, vendor selling, vendor buying, spell-component purchasing, and town-to-hunting-zone navigation. Those are M2+ work and begin after this spec’s exit-portal endpoint.
- A reusable combat, looting, or vendor economy system. Academy-required objective completion is in scope only as part of the verified tutorial quest outcomes in textbook §5.
- Choosing among the three spawn-fix options. That is intentionally left to the explicit clarification items above before `/speckit.plan`.
- Implementation design, task breakdown, code changes to `ACE-bots`, GitHub issue creation, `/speckit.clarify`, `/speckit.plan`, `/speckit.tasks`, or later spec-kit lifecycle steps.
- No-cheating rules, ADR-0012, doorway-discovery docs, or edits to `docs/pilot/improvement-loop.md`.

### Reference Links

- [Session handoff — 2026-05-28](../../docs/research/session-handoff-2026-05-28.md)
- [AC mechanics textbook](../../docs/research/ac-mechanics-textbook.md)
- [Project constitution DRAFT v0.1.0](../../.specify/memory/constitution.md)
- [Spec template](../../.specify/templates/spec-template.md)
- [Pilot Track improvement loop](../../docs/pilot/improvement-loop.md)

## Success Criteria *(mandatory)*

### M1 Acceptance Criteria

- **AC-001**: Fresh level-1 bot starts at the canonical Training Academy entrance for the selected track, with no admin teleport, admin-cursor placement, or post-Academy fallback start (textbook §1, §2, §9; draft constitution §IV).
- **AC-002**: Bot gives the Calling Stone (wcid 5084) to the Society Greeter and opens the required first Academy door (textbook §3, §5, §7).
- **AC-003**: Bot completes each required Academy quest step through Sentry in the verified sequence and receives required progression rewards, including Academy Coat and Facility Hub Portal Gem at the Sentry step (textbook §5).
- **AC-004**: Bot equips the best usable armor and weapons available from starter gear and Academy-earned items before exiting (textbook §3, §5).
- **AC-005**: Bot walks through the Training Academy exit portal after Academy completion and arrives in the selected starter town (textbook §1, §5, §8).
- **AC-006**: Each successful claim above is backed by fresh timestamped server-log evidence or stronger primary evidence, not by agent assertion or absence of errors (draft constitution §V).

### Measurable Outcomes

- **SC-001**: In a fresh M1 run, 100% of start-location evidence shows the bot begins at the selected track’s Training Academy entrance rather than outdoor Holtburg or another post-Academy position (textbook §1, §2, §9).
- **SC-002**: In a successful M1 run, every required Academy quest checkpoint in AC-002 and AC-003 has explicit evidence of completion (textbook §5, §7).
- **SC-003**: Before exit, the evidence bundle shows the bot’s equipped armor and weapons came from documented starter inventory or Academy progression, and the bot selected the best currently usable options it could perceive (textbook §3, §5).
- **SC-004**: The run ends only after evidence shows the bot voluntarily walked through the Training Academy exit portal and arrived in the selected starter town (textbook §1, §5, §8).
- **SC-005**: The evidence bundle maps every M1 acceptance criterion to fresh timestamped server-log lines or stronger primary evidence, consistent with draft constitution §V.

## Assumptions

- The constitution remains DRAFT v0.1.0-draft. This spec follows it as draft guidance but does not ratify it.
- Textbook sections 1-9 are treated as verified for this spec. Textbook §10 is draft analysis and is used only to frame open spawn clarifications.
- The selected character template/build and starter-town track are inputs to an M1 run. This spec does not choose one for the user.
- The bot’s LLM role is constrained to compiling Plans from perceived dialogue and state; deterministic behavior tree execution performs actions. This follows the Pilot Track directive and draft constitution §IV.
- Any AC mechanic not covered by the textbook or a primary source must be researched and added to the textbook before it can affect planning or implementation.
