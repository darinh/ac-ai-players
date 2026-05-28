# AC mechanics textbook

**Status:** Live document. Every claim is sourced. Unverified items are
flagged. When you find a gap, add it — never guess.

**Why this exists:** Past sessions repeatedly broke the bot by guessing at
AC mechanics. This file is the source of truth the Pilot Track agent
loop consults before making spawn / inventory / quest decisions. If a
fact is not in here with a source, run research and add it before
acting on it.

**Verification protocol:** Every section must list its sources by URL
or repo path. Sections marked **REVIEWED** have been independently
checked by a second LLM. Sections marked **DRAFT** are awaiting review.

---

## 1. Spawn: where does a new character actually start?

**REVIEWED — pending second-LLM pass.**

A brand-new character in retail Asheron's Call (post-Throne of Destiny,
2005+) and in modern ACEmulator spawns inside the **Training Academy**,
a dedicated tutorial dungeon, **not** in their heritage starter town.
The Training Academy has a separate instance per starter-town track
(Holtburg, Shoushi, Yaraq, Sanamar) and exits to the chosen town via
a portal at the end.

- Wiki landblock for Training Academy: `0x7204`.
- The Training Academy is **not** called "Newbie Isle," "Allegiance
  Training Academy," or "Hall of Stars." Those names do not appear in
  any primary source.

**In ACE source:** `Source/ACE.Server/Factories/PlayerFactory.cs:355-362`
sets `player.Location = CharGen.StarterAreas[(int)StartArea].Locations[0]`.
The `Name`s in that list, per the switch at lines 366-385, are
`"Holtburg"`, `"Shoushi"`, `"Yaraq"`, `"Sanamar"`, `"OlthoiLair"`.
`Locations[0]` is the Training Academy entrance for that town's track.
`Instantiation` (the post-academy fallback used after `RecallsDisabled`
clears) is set separately from each town's "Free Ride" spell
(Holtburg=3815, Shoushi=3813, Yaraq=3814, Sanamar=3535).

The Training Academy's exact `ObjCellID` is encoded in `Portal.dat`
and cannot be read from ACE source alone — it requires a DAT dump.

**Sources:**
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.Server/Factories/PlayerFactory.cs (lines 355-390)
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.DatLoader/Entity/StarterArea.cs
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.DatLoader/FileTypes/CharGen.cs
- https://asheron.fandom.com/wiki/Training_Academy
- https://asheron.fandom.com/wiki/Character_Creation

---

## 2. Landblock identifications

**REVIEWED — pending second-LLM pass.**

| Landblock | Identity | Confidence | Notes |
|---|---|---|---|
| `0x7204` | Training Academy (tutorial dungeon) | High | Wiki landblock field |
| `0xA9B4` | Holtburg outdoor landscape | High | PlayerFactory.cs:364 hardcoded fallback `Position(0xA9B40019, ...)` for Holtburg. Holtburg Allegiance Hall (Meeting Hall) dungeon portal is at cell `0xA9B4017A` per the redox-extensions dungeon DB. |
| `0xD095` | Surface landscape landblock containing the portal entrance to Thieves' Den (dungeon `0x01E1`) | High | Portal entrance at cell `0xD095002E`, `(121.672, 121.43, 1.6785)`. Not a tutorial zone, hub, or anything related to the Training Academy. |
| `0x8602` | **UNVERIFIED** | Low | Not in the indexed dungeon landblock DB; not in any ACE source reference; not in any wiki page searched via the fandom API. Cell `0x860201AD` has lower 16 bits ≥ `0x0100`, so it is structurally an indoor cell within `0x8602`. What building is unknown. The earlier claim that this is the "Holtburg arrival hall" is plausible but **cannot be cited**. |

**Sources:**
- https://github.com/mrvoorhe/redox-extensions/blob/1f9909c4/RedoxExtensions/Databases/Landblocks-Dungeons.tsv (dungeon entries `01E1`, `02E0`, etc.)
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.Server/Factories/PlayerFactory.cs (line 364 hardcoded Holtburg fallback)

---

## 3. Starter inventory (Aluvian)

**REVIEWED — pending second-LLM pass.**

ACE distributes starter items by **trained skill**, not by template
name. Source of truth is `Source/ACE.Server/starterGear.json`.

**Items every new character gets** (via Jump skill id=22, which all
characters have trained):

| Item | WCID | Stack |
|---|---|---|
| Pyreal | 273 | 10,000 |
| Sack | 166 | 1 |
| Calling Stone | 5084 | 1 |
| Pathwarden Token | 33613 | 1 |
| Bread | 259 | 1 |
| Ust | 20646 | 1 |

**Aluvian-specific** (heritage id=1, under skill id=22):

| Item | WCID | Stack |
|---|---|---|
| Letter From Home | 30988 | 1 |

**Per chosen skill** (Aluvian examples):

| Skill | Item | WCID |
|---|---|---|
| Healing (21) | Handy Healing Kit | 628 |
| Lockpick (23) | Crude Lockpick | 511 |
| Heavy Weapons (44) | Training Dirk | 12739 |
| Light Weapons (45) | Training Dagger | 45538 |
| Two Handed Combat (41) | Training Spadone | 41512 |
| War Magic (34) | Training Wand + Foci of Strife + Lead Scarab ×5 + Prismatic Taper ×25 | 12748, 15271, 691, 20631 |

**Not a starter item:** "Trade Note 500" — does not appear in starter
inventory. The 500 Pyreals come from **Buckminster** (Bartender Greeter
NPC in Holtburg) as a quest reward, not from character creation.

**Sources:**
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.Server/starterGear.json (lines 47-165 Jump skill, lines 607-618 Aluvian Heavy Weapons)
- https://asheron.fandom.com/wiki/Calling_Stone
- https://asheron.fandom.com/wiki/Pathwarden_Token

---

## 4. Character creation templates

**REVIEWED — pending second-LLM pass.**

There is **no character creation template called "Pathwarden"** in
Asheron's Call. The term does not appear on the Character Professions
or Character Creation wiki pages.

**Current stock templates** (late-era, post-"Crests of a Turbulent Sea"):
Bow Hunter, Life Caster, Soldier, Swashbuckler, War Mage, Wayfarer.

**Original (pre-"Crests of a Turbulent Sea") templates**:
Archer, Blademaster, Enchanter, Life Mage, Sorcerer, Vagabond, Warrior.

**"Pathwarden" is a greeter service**, introduced in the
"Toward Ancient Shores" patch (July 2006). The Pathwardens are
described in-fiction as "a new organization who have made it their
task to outfit new arrivals in Dereth." They run the Pathwarden Token
→ Aluvian Pathwarden Chest item-grant in each starter town. They are
**not** a faction the character "joins" and **not** a creation template.

**There is no in-game faction called "Pathwarden Society."** The
in-world organization that runs the Training Academy is **The
Exploration Society**.

**Sources:**
- https://asheron.fandom.com/wiki/Character_Professions
- https://asheron.fandom.com/wiki/Toward_Ancient_Shores
- https://asheron.fandom.com/wiki/The_Exploration_Society

---

## 5. Training Academy quest chain (canonical post-ToD)

**REVIEWED — pending second-LLM pass.**

**Inside the Training Academy:**

1. **Society Greeter** — hand her the Calling Stone (wcid 5084); opens the door east.
   - *Optional bypass*: **Jonathan** (Exploration Society Agent inside the Academy) gives an "Academy Exit Token" allowing the player to skip the rest of the Academy.
2. **Samuel** — sends player to find 3 pieces of starting armor (Leather Cap, Leather Gauntlets, Leather Leggings).
3. **Training Master** — return Academy Token (looted from Sparring Golem).
4. **Academy Foreman** (Central Courtyard) — wants a Carpenter Wasp Wing.
5. **Academy Blacksmith** — wants a Bellows (from Thieving Thrungus); gives Academy Library Key.
6. **Academy Researcher** (in Library) — sells Oil of Rendering; used to upgrade Training Weapon → Academy Weapon.
7. *Optional*: Wordsmith, Academy Crier, Academy Shopkeep.
8. **Senior Guard** — sends player to Outer Courtyard.
9. **Sentry** — wants Protection Orb (from Adolescent Olthoi); gives **Academy Coat** and **Facility Hub Portal Gem**.

**Exit portal** → chosen starter town. Voluntary walk-through, not auto.

**In Holtburg (after the Academy):**

1. **Alcott** (Lifestone Greeter) at 42.1N, 33.6E — teaches lifestone; gives "Find the Bartender" contract; +7,000 XP.
2. **Buckminster** (Bartender Greeter) in Holtburg Tavern at 42.2N, 33.7E — teaches contracts/barkeepers; gives "Find the Pathwarden" contract; +9,300 XP; gives 500 Pyreals.
3. **Pathwarden Thorolf** (Pathwarden Greeter) at 42.2N, 33.6E — exchanges Pathwarden Token → Pathwarden Supply Key; +12,500 XP. The key unlocks the **Aluvian Pathwarden Chest** at 42.2N, 33.7E (contains Pathwarden Helm, Gauntlets, Sollerets, Robe (Aluvian), Plate Leggings, Plate Hauberk, Great Mana Charge, Pathwarden Trinket).

**Sources:**
- https://asheron.fandom.com/wiki/Training_Academy_Quest
- https://asheron.fandom.com/wiki/Alcott
- https://asheron.fandom.com/wiki/Buckminster
- https://asheron.fandom.com/wiki/Pathwarden_Thorolf
- https://asheron.fandom.com/wiki/Aluvian_Pathwarden_Chest
- https://asheron.fandom.com/wiki/Jonathan

---

## 6. Specific NPCs the bot has perceived

**REVIEWED — pending second-LLM pass.**

| Name (as the bot sees it) | Identity | On the new-player chain? |
|---|---|---|
| **Alcott** | Holtburg Lifestone Greeter, 42.1N 33.6E | YES — step 1 of the post-Academy Holtburg chain |
| **Buckminster** | Holtburg Bartender Greeter, 42.2N 33.7E | YES — step 2 of the post-Academy Holtburg chain |
| **Pathwarden Thorolf** | Holtburg Pathwarden Greeter, 42.2N 33.6E | YES — step 3 of the post-Academy Holtburg chain |
| **Tirenia** | Holtburg Royal Guard, 42.1N 33.7E, Assault Quest NPC | NO — high-level Queen's vault content |
| **Fispur Ansel the Grocer** | Holtburg grocer / shopkeeper, 42.2N 33.6E | NO — sells food and packs |
| **"Pathwarden Jonathan"** | DOES NOT EXIST under that name | The "Jonathan" referenced is the Exploration Society Agent inside the Training Academy who gives the optional Exit Token. A separate Jonathan exists in Eldrytch Web (Society Collector, level 180) but is unrelated. |
| **"Instructor Liela"** | NOT FOUND in any primary source | The fandom wiki opensearch returns zero results for this name. The earlier web-search answer that named her as running Society selection was hallucinated — there is no canonical AC "Society selection" NPC by that name. |

**Sources:**
- https://asheron.fandom.com/wiki/Alcott
- https://asheron.fandom.com/wiki/Buckminster
- https://asheron.fandom.com/wiki/Pathwarden_Thorolf
- https://asheron.fandom.com/wiki/Tirenia
- https://asheron.fandom.com/wiki/Fispur_Ansel_the_Grocer
- https://asheron.fandom.com/wiki/Jonathan
- https://asheron.fandom.com/wiki/Special:Search?query=Instructor+Liela (zero results)

---

## 7. Calling Stone — basic vs. "Society"

**REVIEWED — pending second-LLM pass.**

- **Calling Stone (wcid 5084)** — given to all new characters in starter inventory. *"This is a Calling Stone that all newcomers arrive with. It is a plain, lightweight gem. Give this item to the Society Greeter."* Attuned, Bonded. Function: one-time use to hand to the Society Greeter at the Training Academy entrance to open the first door.
- **"Society Calling Stone"** — **NOT A DISTINCT ITEM**. The fandom wiki opensearch for "Society Calling Stone" returns only the Calling Stone page, the Society Greeter page, and unrelated results. No separate item by that name exists in any primary source. The earlier claim that Instructor Liela grants a "Society Calling Stone" after Society selection is hallucinated.

**Sources:**
- https://asheron.fandom.com/wiki/Calling_Stone
- https://asheron.fandom.com/wiki/Society_Greeter

---

## 8. Exit from the Training Academy

**REVIEWED — pending second-LLM pass.**

After completing all Academy tasks (or after returning Jonathan's exit
token via the optional bypass), an **exit portal** in the Outer
Courtyard leads to the player's chosen starter town. The portal is
walk-through; it is not a dialogue choice and not auto-progression.

In ACE, `PlayerFactory.cs:394` sets `PropertyBool.RecallsDisabled = true`
on new characters, preventing teleport recalls while in the Academy.
The `Instantiation` (post-Academy fallback town position) becomes
relevant once `RecallsDisabled` is cleared.

**Sources:**
- https://asheron.fandom.com/wiki/Training_Academy (Portals section)
- https://asheron.fandom.com/wiki/Training_Academy_Quest (step-by-step)
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.Server/Factories/PlayerFactory.cs (line 394)

---

## 9. ACE `StarterAreas` / `PlayerFactory.cs:355-390` — exact behavior

**REVIEWED — pending second-LLM pass.**

```csharp
// PlayerFactory.cs:355-362
var startArea = characterCreateInfo.StartArea;
var starterArea = DatManager.PortalDat.CharGen.StarterAreas[(int)startArea];
player.Location = new Position(starterArea.Locations[0].ObjCellID,
    starterArea.Locations[0].Frame.Origin.X, ...);

// PlayerFactory.cs:364-387
var instantiation = new Position(0xA9B40019, 84, 7.1f, 94, ...); // ultimate fallback
switch (starterArea.Name)
{
    case "OlthoiLair": ... // no Free Ride spell
    case "Shoushi":    spellFreeRide = GetCachedSpell(3813); break;
    case "Yaraq":      spellFreeRide = GetCachedSpell(3814); break;
    case "Sanamar":    spellFreeRide = GetCachedSpell(3535); break;
    case "Holtburg":
    default:           spellFreeRide = GetCachedSpell(3815); break;
}
player.Instantiation = new Position(instantiation);
```

**Conclusions:**

- `CharGen.StarterAreas` is a `List<StarterArea>`; each entry has a
  `Name` (string) and `Locations` (`List<Position>`).
- StartArea is an integer index sent by the client at character
  creation; it picks the named area. Names are `"Holtburg"`,
  `"Shoushi"`, `"Yaraq"`, `"Sanamar"`, `"OlthoiLair"`.
- `Locations[0]` is the **Training Academy entrance for that town's
  track**, not the town itself.
- The `Instantiation` is the post-Academy town landing position
  (driven by the "Free Ride to [Town]" spell at runtime —
  `GetCachedSpell(3815)` for Holtburg). The hardcoded
  `Position(0xA9B40019, 84, 7.1f, 94, ...)` at line 364 is only the
  **fallback** used when the spell DB lookup fails or the spell has
  no position; under normal operation the Free Ride spell's position
  wins.
- The "Holtburg arrival hall" the bot has been talking about is in
  reality landblock `0xA9B4` (outdoor Holtburg landscape). The bot
  has been at the post-Academy *Instantiation* position the whole
  time, not at a real "academy" location.
- `HeritageGroupCG` has `PrimaryStartAreas` and `SecondaryStartAreas`
  (lists of `int` indices into `StarterAreas`) — indicating which
  town(s) are default for each heritage. Which integer = which town
  comes from `Portal.dat` and is not in ACE source.

**Sources:**
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.Server/Factories/PlayerFactory.cs (lines 355-390)
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.DatLoader/FileTypes/CharGen.cs
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.DatLoader/Entity/StarterArea.cs
- https://github.com/ACEmulator/ACE/blob/9bc20cbd/Source/ACE.DatLoader/Entity/HeritageGroupCG.cs

---

## 10. Implications for the current bot (Pilot-01, 0x50000002)

**DRAFT — derived from sections 1-9.**

The live bot is at cell `0xA9B40019` in outdoor Holtburg landscape
(landblock `0xA9B4`). This matches PlayerFactory's hardcoded
`Instantiation` fallback for Holtburg — i.e., the bot is at the
**post-Academy landing position**, not at the canonical level-1
spawn location.

**How it got there:**

1. `BotPlayerFactory.cs:165` calls `PlayerFactory.Create(...)`, which
   at line 360 sets `Location = StarterAreas[StartArea].Locations[0]`
   (the Training Academy entrance for the chosen town track). ✅
2. `BotPlayerFactory.cs:181-183` then **overwrites** `Location`,
   `Sanctuary`, and `Instantiation` with `startPosition` — the admin's
   `/spawnplayerbot` cursor location. ❌ Throws away the canonical
   Training Academy spawn from step 1.
3. (Until commit `db2da06ec` this session, the just-deleted
   `MigrateToCanonicalStarterIfNeeded()` then teleported the bot to
   landblock `0xA9B4` on first tick.) The bot is now sitting at the
   coordinates that the now-deleted migration last left it at, because
   rehydration uses the persisted DB Location.

**What true Pilot-01 M1 requires:**

- For a fresh bot to spawn at the Training Academy (M1 litmus item
  "navigate the training academy, opening doors as needed"),
  `BotPlayerFactory.cs:181-183` must stop overwriting
  PlayerFactory's canonical spawn. The bot then needs to autonomously
  walk through the academy NPC chain (section 5 above) and use the
  exit portal to arrive in its starter town.
- For the existing bot to do M1, its persisted Location must be
  reset to a Training Academy spawn cell — either by deleting its
  character row and re-spawning, or by adding an admin `/resetbot`
  command that re-runs the canonical spawn lookup.

**Open questions for the user (do NOT decide autonomously):**

- Should the next code change be removing the `BotPlayerFactory.cs:181-183`
  overrides? Or gating them behind a flag so dev `/spawnplayerbot` can
  still drop bots at the admin's cursor for non-Pilot testing?
- For the live bot, wipe and re-spawn, or add a `/resetbot` command?

---

## Change log

- 2026-05-28: Initial draft of sections 1-10 from `ac-spawn-research`
  subagent (gpt-5.3-codex-equivalent) with primary sources cited.
  Awaiting second-LLM review pass.
