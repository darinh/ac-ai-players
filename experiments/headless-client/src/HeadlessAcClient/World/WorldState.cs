// SPDX-License-Identifier: AGPL-3.0-or-later
// WorldState — guid-keyed in-memory accumulator for everything
// the bot knows about the world. Fed by the post-EnterWorld
// firehose decoded in Phase 4 (ObjectCreate, UpdatePosition,
// Motion, SetState, PrivateUpdatePropertyInt, PlayerCreate).
//
// Design notes:
//   - Single-threaded. Lives behind the receive loop in
//     HandshakeDriver. If we ever multi-thread the network
//     layer, wrap _objects with a lock.
//   - ObjectCreate for a known guid MERGES (preserves FirstSeen
//     and PropertyInts; refreshes weenie / physics; respects
//     sequence high-water marks per rubber-duck pass).
//   - Sequence gating per message family uses a two-level rule:
//       (a) if SeqInstance advanced, accept everything (new
//           instance epoch — the message-specific sequence
//           tracker starts fresh implicitly because the
//           server resets it on the wire too);
//       (b) if SeqInstance is equal, gate on the message-
//           specific sequence (SeqPosition for UpdatePosition,
//           SeqMovement for Motion, SeqState for SetState);
//       (c) if SeqInstance went backward, drop entirely.
//   - PrivateUpdatePropertyInt has no guid in the wire format —
//     it implicitly targets the receiving session's player. We
//     pre-seed SelfGuid from the character chosen at EnterWorld
//     so the initial property dump (which arrives BEFORE the
//     server-issued PlayerCreate) routes correctly.
//
// What's intentionally NOT here yet (Phase 5+):
//   - Full Motion body decode — only header fields surface
//     here; the polymorphic body is still raw bytes.

using System;
using System.Collections.Generic;
using System.Linq;
using HeadlessAcClient.Protocol.GameMessages;

namespace HeadlessAcClient.World;

/// <summary>
/// combat-damage-output: a snapshot of the current melee fight's
/// outcome counters, surfaced to the LLM as raw perception. The Motor
/// (HandshakeDriver) owns and updates this; the policy only reads it.
/// </summary>
/// <param name="TargetGuid">Guid of the locked combat target.</param>
/// <param name="TargetName">Best-known display name of the target (may be null early).</param>
/// <param name="SwingsLanded">Count of swings that LANDED (AttackerNotification) this fight.</param>
/// <param name="SwingsEvaded">Count of swings the target EVADED (EvasionAttackerNotification) this fight.</param>
/// <param name="DamageDealt">Cumulative damage dealt to the target this fight.</param>
internal sealed record CombatFightStatus(
    uint TargetGuid,
    string? TargetName,
    int SwingsLanded,
    int SwingsEvaded,
    uint DamageDealt,
    float? FirstTargetHealthFraction = null,
    float? CurrentTargetHealthFraction = null);

/// <summary>
/// active-combat-telemetry: a rolling-window summary of recent INBOUND
/// damage the bot has TAKEN (landed DefenderNotification hits), surfaced to
/// the LLM as raw perception in the "## Combat readiness" prompt section.
/// Independent of the combat lock, so it persists through a flee when
/// <see cref="CombatFightStatus"/> is cleared at the disengage reflex —
/// which is exactly when the LLM needs the "I am still being hurt, this
/// fast" trajectory to decide to disengage or Recall. Counts and sums only;
/// source assigns no danger label and makes no fight-vs-flee decision.
/// </summary>
/// <param name="Hits">Inbound hits that landed on the bot within the window.</param>
/// <param name="TotalDamage">Cumulative damage taken from those hits.</param>
/// <param name="WindowSeconds">Length of the rolling window the counts cover.</param>
internal sealed record RecentInboundDamage(int Hits, uint TotalDamage, double WindowSeconds);

/// <summary>
/// combat-feel ledger: a per-mob-identity summary of the bot's OWN
/// observed combat outcomes against that kind of monster this session
/// (kills, deaths, near-deaths). Surfaced to the LLM as raw recorded
/// FACTS in the "## Combat history" prompt section so it can learn,
/// across ticks, which monsters it can defeat and which keep killing
/// it — and choose softer targets or flee on its own. Source records
/// the raw outcomes only; it makes NO avoidance decision and assigns
/// NO danger label (the COMBAT SAFETY rule owns the interpretation).
/// </summary>
/// <param name="Name">Best-known display name of the monster kind.</param>
/// <param name="Wcid">WeenieClassId of the monster kind, if observed.</param>
/// <param name="Kills">Times the bot killed this kind this session.</param>
/// <param name="Deaths">Times this kind killed the bot this session.</param>
/// <param name="NearDeaths">Times the bot disengaged this kind at critical health.</param>
/// <param name="Fights">Times the bot engaged this kind this session.</param>
/// <param name="LastOutcome">"kill" | "death" | "near-death" — the most recent outcome.</param>
internal sealed record CombatHistoryEntry(
    string Name,
    uint? Wcid,
    int Kills,
    int Deaths,
    int NearDeaths,
    int Fights,
    string LastOutcome,
    int Ineffective = 0,
    // Highest bot level at which a LOSS to this kind was recorded (null when
    // unknown — e.g. ledgers persisted before this field existed). Drives the
    // fallback's adaptive beaten-kind re-test; never rendered to the LLM.
    int? MaxLossBotLevel = null);

internal sealed class WorldState
{
    private readonly Dictionary<uint, WorldObjectSnapshot> _objects = new();

    /// <summary>
    /// Case-insensitive set of every distinct object name observed via an
    /// ObjectCreate since login. Unlike <see cref="_objects"/> (pruned on
    /// ObjectDelete / FOV eviction), this is append-only and never pruned,
    /// so it answers the session-wide question "has an object by this name
    /// EVER entered the world model" — distinct from "is it visible now".
    /// Pure perception memory; carries no priority or game knowledge.
    /// </summary>
    private readonly HashSet<string> _everObservedNames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-(32-bit PropertyInt property) byte-sequence high-water
    /// marks. The server keys this sequence by
    /// (SequenceType.UpdatePropertyInt, property) — see
    /// SequenceManager.GetSequence: key = (type &lt;&lt; 16) | property —
    /// so every PropertyInt advances an INDEPENDENT 1-byte counter
    /// (e.g. the per-heartbeat Age ticker advances only Age's counter,
    /// not Level's). An earlier single shared counter was a BUG: a
    /// frequently-ticking property (Age) drove the shared max up so a
    /// later first-time update of another property (a Level-up,
    /// CoinValue, NumDeaths) at its own low per-property sequence was
    /// falsely dropped as stale. We therefore key the high-water mark
    /// by property, mirroring <see cref="_selfPropertyInt64ByteSeq"/>.
    /// Values nullable because 0 is a valid sequence; absent key =
    /// never seen. (Distinct SequenceTypes — Int vs Int64 vs Bool vs
    /// Float vs String, Private vs Public — are already separate
    /// counters server-side, so each decoded family keeps its own map.)
    /// </summary>
    private readonly Dictionary<uint, byte> _selfPropertyByteSeq = new();

    /// <summary>
    /// Per-(Int64 property) byte-sequence high-water marks. Like the
    /// 32-bit <see cref="_selfPropertyByteSeq"/>, the server keys the
    /// Int64 sequence by (SequenceType.UpdatePropertyInt64, property) —
    /// so TotalExperience
    /// and AvailableExperience advance INDEPENDENT counters (e.g.
    /// spending XP advances AvailableExperience without TotalExperience).
    /// Gating all Int64 properties against one shared max would falsely
    /// drop a valid update whenever the two counters diverge, so we key
    /// the high-water mark by property. Values nullable because 0 is a
    /// valid sequence; absent key = never seen.
    /// </summary>
    private readonly Dictionary<uint, byte> _selfPropertyInt64ByteSeq = new();

    /// <summary>
    /// Highest byte-sequence seen on PrivateUpdateVital for the HEALTH
    /// vital. This is a SEPARATE counter from _selfPropertyByteSeq: the
    /// vital update uses a per-(type,vital) UpdateAttribute2ndLevel
    /// ByteSequence (see GameMessagePrivateUpdateVital), keyed by a
    /// different SequenceType than the PropertyInt counters. Nullable
    /// because 0 is a valid sequence value.
    /// </summary>
    private byte? _selfHealthVitalByteSeq;

    /// <summary>
    /// Highest byte-sequence seen on PrivateUpdateAttribute2ndLevel
    /// (0x02E9) for the HEALTH vital. ACE keys ByteSequences by
    /// (type, vital); the current-level health packet keys on Health (2)
    /// while the 0x02E7 descriptor keys on MaxHealth (1), so these are
    /// DISTINCT counters and must be gated independently. Nullable
    /// because 0 is a valid sequence value.
    /// </summary>
    private byte? _selfHealthLevelByteSeq;

    /// <summary>
    /// Per-(skill id) byte-sequence high-water marks for discrete
    /// PrivateUpdateSkill (0x02DD). The server keys this ByteSequence by
    /// (SequenceType.UpdateSkill, skillId), so each skill advances an
    /// INDEPENDENT 1-byte counter — gate per skill id, exactly like the
    /// per-property Int64 counters. Nullable because 0 is a valid
    /// sequence; absent key = never seen. NOT touched by the login
    /// PlayerDescription seed (only real discrete packets populate it).
    /// </summary>
    private readonly Dictionary<uint, byte> _selfSkillByteSeq = new();

    /// <summary>
    /// Per-(primary-attribute id) byte-sequence high-water marks for
    /// discrete PrivateUpdateAttribute (0x02E3). Keyed by
    /// (SequenceType.UpdateAttribute, attrId); independent per attribute.
    /// Same contract as <see cref="_selfSkillByteSeq"/>.
    /// </summary>
    private readonly Dictionary<uint, byte> _selfAttributeByteSeq = new();


    /// <summary>
    /// Guid of the bot's own player. Set by SetSelf — typically
    /// pre-seeded from the chosen character guid at EnterWorld
    /// time so private property updates can be routed BEFORE
    /// PlayerCreate arrives.
    /// </summary>
    public uint? SelfGuid { get; private set; }

    /// <summary>
    /// Convenience accessor for the bot's own snapshot. Returns
    /// null if SelfGuid is unset OR if the snapshot for that
    /// guid hasn't been materialized yet.
    /// </summary>
    public WorldObjectSnapshot? Self
        => SelfGuid is uint g && _objects.TryGetValue(g, out var s) ? s : null;

    /// <summary>
    /// The bot's current fellowship membership, set/refreshed from the server's
    /// FellowshipFullUpdate (0x02BE) snapshot and cleared on Disband (0x02BF) or
    /// a self-targeted Quit/Dismiss. Null when the bot is not in a fellowship.
    /// Pure perception memory; the LLM owns any decision about the fellowship.
    /// </summary>
    public FellowshipMembership? Fellowship { get; private set; }

    /// <summary>
    /// The bot's currently tracked contracts/objectives, set from the server's
    /// SendClientContractTrackerTable (0x0314) full snapshot and upserted/removed
    /// by SendClientContractTracker (0x0315). Empty when none are tracked. Pure
    /// perception memory; the LLM owns any decision about a contract.
    /// </summary>
    public IReadOnlyList<ContractTrackerEntry> Contracts { get; private set; }
        = new List<ContractTrackerEntry>();

    /// <summary>
    /// The vendor trade panel the bot most recently opened (ApproachVendor
    /// 0x0062) and the landblock it was opened in. The projection surfaces it
    /// only while the bot is still in that landblock (a vendor interaction is
    /// single-landblock); a later ApproachVendor replaces it. Pure perception —
    /// source never decides to buy.
    /// </summary>
    public VendorInfoPayload? OpenVendor { get; private set; }
    public uint? OpenVendorLandblock { get; private set; }

    public int ObjectCount => _objects.Count;

    /// <summary>
    /// combat-damage-output: the Motor's live view of the current melee
    /// fight, set/cleared by HandshakeDriver as it tracks the active
    /// combat lock. Null when not in combat. Surfaced verbatim to the
    /// LLM in the "## Combat readiness" prompt section as RAW perception
    /// (swings landed vs evaded, damage dealt) so the LLM can judge
    /// whether it is actually hurting the target and decide to disengage
    /// — source never makes that decision itself.
    /// </summary>
    public CombatFightStatus? CurrentFight { get; set; }

    /// <summary>
    /// Session-cumulative count of the bot's OWN melee swings that LANDED a
    /// hit (AttackerNotification), summed across every fight. Unlike the
    /// per-fight counters inside <see cref="CurrentFight"/> (which reset each
    /// fight) this is monotonic for the run, so the prompt can surface the
    /// bot's overall hit/evade split beside the spend-XP decision. Raw
    /// observed outcome; source draws no conclusion from it.
    /// </summary>
    public int CumulativeSwingsLanded { get; set; }

    /// <summary>
    /// Session-cumulative count of the bot's OWN melee swings the target
    /// EVADED (EvasionAttackerNotification), summed across every fight. Pairs
    /// with <see cref="CumulativeSwingsLanded"/> to express overall melee
    /// accuracy. Raw observed outcome; source draws no conclusion from it.
    /// </summary>
    public int CumulativeSwingsEvaded { get; set; }

    /// <summary>
    /// active-combat-telemetry: rolling-window summary of recent inbound
    /// damage the bot has TAKEN, set/cleared by HandshakeDriver before each
    /// projection build from a short TTL window of landed DefenderNotification
    /// hits. Null when the bot has taken no damage recently. Lock-independent
    /// so it survives a flee (when <see cref="CurrentFight"/> clears).
    /// Surfaced as RAW perception in "## Combat readiness"; source makes no
    /// fight-vs-flee decision.
    /// </summary>
    public RecentInboundDamage? RecentInboundDamage { get; set; }

    /// <summary>
    /// combat-feel ledger: per-mob-identity summary of the bot's own
    /// observed combat outcomes this session (kills/deaths/near-deaths),
    /// set by HandshakeDriver from the <c>CombatFeelLedger</c>. Surfaced
    /// to the LLM in the "## Combat history" prompt section as RAW
    /// recorded facts so it can learn which monsters it can defeat —
    /// source records outcomes only and makes no avoidance decision.
    /// Null until the bot has at least one significant outcome.
    /// </summary>
    public IReadOnlyList<CombatHistoryEntry>? CombatHistory { get; set; }

    /// <summary>
    /// The SAME combat-feel outcomes as <see cref="CombatHistory"/> but
    /// UNCAPPED (every kind with a recorded outcome, not just the 6
    /// most-recent). The prompt uses the capped <see cref="CombatHistory"/>;
    /// the <c>kill_count_since_push</c> Intent predicate needs the full set so
    /// its per-kind baseline does not miss a kind that has aged out of the
    /// capped snapshot (which would over-count pre-push kills). Set alongside
    /// <see cref="CombatHistory"/> by HandshakeDriver.
    /// </summary>
    public IReadOnlyList<CombatHistoryEntry>? CombatHistoryFull { get; set; }

    /// <summary>
    /// observed-hostile perception: the set of NORMALIZED creature names
    /// the server has recently told us are attacking the bot (decoded from
    /// DefenderNotification 0x01B2 / EvasionDefenderNotification 0x01B4),
    /// set/pruned by HandshakeDriver before each projection build. A
    /// visible object whose normalized name is in this set is surfaced to
    /// the LLM as <c>ObservedHostile</c> ("it has attacked you") — RAW
    /// perception only; the LLM owns the fight-vs-flee decision. Empty/null
    /// when nothing is currently attacking the bot.
    /// </summary>
    public IReadOnlySet<string>? RecentHostileNames { get; set; }

    /// <summary>
    /// loot bookkeeping: the set of corpse/container GUIDs the bot has
    /// itself opened within the loot-tracking TTL window, set/pruned by
    /// HandshakeDriver before each projection build. Pure own-action
    /// bookkeeping the LLM cannot reconstruct from wire flags alone (the
    /// wire IsCorpse flag does not say "I already opened this one"). A
    /// visible corpse whose GUID is in this set is annotated to the LLM as
    /// already opened so it does not re-pick a corpse it has already
    /// looted; absence means the bot has not opened it recently. Unlike
    /// the loot-mechanics tracker this set is NOT removed when a corpse is
    /// reported empty — it ages out by TTL only, so the "opened by bot"
    /// claim stays truthful for the corpse's visible lifetime. Empty/null
    /// when the bot has opened nothing recently.
    /// </summary>
    public IReadOnlySet<uint>? OpenedCorpseGuids { get; set; }

    /// <summary>
    /// cold-start egress: stable kind-keys (in <see
    /// cref="HeadlessAcClient.Strategy.CombatFeelLedger.KeyOf"/> form —
    /// <c>w:wcid</c> or <c>n:name</c>) of monster KINDS the bot has KILLED
    /// since it entered the current landblock. Reset on landblock change and
    /// published by HandshakeDriver before each projection build. The
    /// mechanical hunt-egress override uses it to tell whether a visible
    /// monster is a kind the bot has already farmed HERE (so, once the bot
    /// is tapped out, that kind no longer keeps it in the zone) — RAW
    /// bot-owned outcome data; it carries no danger/value/priority label.
    /// Null when the bot has killed nothing in this landblock yet.
    /// </summary>
    public IReadOnlySet<string>? KilledKindsThisDwell { get; set; }

    /// <summary>
    /// immobile-stuck telemetry: how many consecutive full movement
    /// block-stops (each = several server-rejected zero-progress walk ticks)
    /// have fired without the bot's self-position changing. 0 when the bot
    /// last moved normally. Published by HandshakeDriver before each
    /// projection build and surfaced as raw movement-failure facts in the
    /// "## Movement" prompt section so the LLM can recognise a physical wedge
    /// (boxed in / on a ledge) and choose a different action. Source assigns
    /// no urgency — it only counts its own failed-movement bookkeeping.
    /// </summary>
    public int MovementBlockStopsSinceSelfMoved { get; set; }

    /// <summary>
    /// named-target frontier-search telemetry: when an LLM goal names a target
    /// that is not currently visible, the Motor drives inert "frontier probes"
    /// (walks toward unexplored cells) to discover it. These three fields record
    /// the CURRENT consecutive search run — its target name, how many discovery
    /// probes it has launched, and how many DISTINCT frontier cells it has tried
    /// — so the prompt can surface a stalled/repeating search (probes &gt; distinct
    /// cells ⇒ the bot is revisiting ground it already covered without finding the
    /// target). Published by HandshakeDriver before each projection build and reset
    /// when the bot locks a real (resolved) target or the search key changes
    /// (different goal kind / target name / landblock). Pure own-bookkeeping; the
    /// LLM decides whether the target is unreachable this way and what to do
    /// instead (e.g. open a Door, pick a different objective). Source assigns no
    /// urgency and takes no autonomous action.
    /// </summary>
    public string? NamedSearchTargetName { get; set; }

    /// <summary>Consecutive discovery probes spent on the current named-target
    /// search (see <see cref="NamedSearchTargetName"/>). 0 when not searching.</summary>
    public int NamedSearchProbeCount { get; set; }

    /// <summary>Distinct frontier cells tried during the current named-target
    /// search (see <see cref="NamedSearchTargetName"/>).</summary>
    public int NamedSearchDistinctCells { get; set; }

    /// <summary>Read-only view of all known objects, keyed by guid.</summary>
    public IReadOnlyDictionary<uint, WorldObjectSnapshot> Objects => _objects;

    /// <summary>
    /// Read-only view of every distinct object name observed since login
    /// (append-only, case-insensitive). See <see cref="_everObservedNames"/>.
    /// </summary>
    public IReadOnlySet<string> EverObservedNames => _everObservedNames;

    /// <summary>
    /// True if an object with this exact name (case-insensitive) has entered
    /// the world model at any point since login. False for null/empty. Distinct
    /// from "currently visible" — a phantom name from dialog text that never
    /// resolves to a real object returns false no matter how long it is sought.
    /// </summary>
    public bool WasObjectNameEverObserved(string? name)
        => !string.IsNullOrEmpty(name) && _everObservedNames.Contains(name);

    public WorldObjectSnapshot? TryGet(uint guid)
        => _objects.TryGetValue(guid, out var s) ? s : null;

    /// <summary>
    /// Pre-seed (or confirm) the bot's own guid. Safe to call
    /// repeatedly with the same value. If a different non-zero
    /// guid is passed after one is already set, logs the
    /// mismatch to stderr — would indicate a bug; the chosen
    /// guid should match what PlayerCreate eventually reports.
    /// </summary>
    public void SetSelf(uint guid)
    {
        if (guid == 0) return;
        if (SelfGuid is uint existing && existing != guid)
        {
            Console.Error.WriteLine(
                $"[worldstate] SelfGuid mismatch: had 0x{existing:X8}, got 0x{guid:X8}");
        }
        SelfGuid = guid;

        // Materialize an empty snapshot for the self guid so
        // PrivateUpdatePropertyInt has a place to land even if
        // ObjectCreate hasn't arrived yet for our own player.
        if (!_objects.ContainsKey(guid))
            _objects[guid] = new WorldObjectSnapshot(guid);
    }

    /// <summary>
    /// Dispatch a decoded message into the world-state model.
    /// Returns true if the message updated state, false if it
    /// was ignored (unknown type, stale sequence, etc.).
    /// </summary>
    public bool Apply(object? decoded)
    {
        return decoded switch
        {
            ObjectCreateMessage oc            => ApplyObjectCreate(oc),
            ObjectDeleteMessage od            => ApplyObjectDelete(od),
            InventoryRemoveObjectMessage ir   => ApplyInventoryRemove(ir),
            UpdatePositionMessage up          => ApplyUpdatePosition(up),
            MotionMessage mm                  => ApplyMotion(mm),
            SetStateMessage ss                => ApplySetState(ss),
            PrivateUpdatePropertyIntMessage p => ApplyPrivatePropertyInt(p),
            PrivateUpdatePropertyInt64Message p64 => ApplyPrivatePropertyInt64(p64),
            PrivateUpdateVitalMessage v       => ApplyPrivateVital(v),
            PrivateUpdateAttribute2ndLevelMessage a => ApplyPrivateVitalLevel(a),
            PrivateUpdateSkillMessage sk      => ApplyPrivateSkill(sk),
            PrivateUpdateAttributeMessage at  => ApplyPrivateAttribute(at),
            PlayerCreateMessage pc            => ApplyPlayerCreate(pc),
            _                                 => false,
        };
    }

    private bool ApplyPlayerCreate(PlayerCreateMessage pc)
    {
        SetSelf(pc.Guid);
        return true;
    }

    /// <summary>
    /// Record the server's whole-fellowship snapshot (FellowshipFullUpdate,
    /// 0x02BE). Replaces any prior membership wholesale — the snapshot is
    /// authoritative. Maps each wire fellow to a <see cref="FellowshipMember"/>
    /// (identity/name/level only). Always returns true. Wire projection only;
    /// assigns no priority or game meaning.
    /// </summary>
    public bool ApplyFellowshipFullUpdate(FellowshipFullUpdatePayload payload)
    {
        var members = payload.Members
            .Select(m => new FellowshipMember(m.Guid, m.Name, m.Level))
            .ToList();
        Fellowship = new FellowshipMembership(
            payload.FellowshipName,
            payload.LeaderGuid,
            members,
            payload.ShareXp,
            payload.EvenShare,
            payload.Open,
            payload.IsLocked);
        return true;
    }

    /// <summary>
    /// Apply a fellowship departure (FellowshipQuit 0x00A3 or FellowshipDismiss
    /// 0x00A4). If the departing guid is the bot's own, the bot has left → clear
    /// the whole membership. Otherwise drop that member from the snapshot so the
    /// perceived roster stays exact between full snapshots (a non-leader quit
    /// sends remaining members only a Quit, no FullUpdate). No-op (returns false)
    /// when not in a fellowship or the guid is not a current member.
    /// </summary>
    public bool ApplyFellowshipDeparture(uint departedGuid)
    {
        if (Fellowship is null)
            return false;
        if (SelfGuid is uint self && departedGuid == self)
            return ClearFellowship();
        var remaining = Fellowship.Members
            .Where(m => m.Guid != departedGuid)
            .ToList();
        if (remaining.Count == Fellowship.Members.Count)
            return false; // departed guid was not a current member
        Fellowship = Fellowship with { Members = remaining };
        return true;
    }

    /// <summary>
    /// Clear the bot's fellowship membership (FellowshipDisband 0x02BF, or a
    /// self-targeted Quit/Dismiss). Returns false if there was nothing to clear.
    /// </summary>
    public bool ClearFellowship()
    {
        if (Fellowship is null)
            return false;
        Fellowship = null;
        return true;
    }

    /// <summary>
    /// Replace the tracked-contract set from a SendClientContractTrackerTable
    /// (0x0314) snapshot — the server's authoritative full list. Returns true.
    /// </summary>
    public bool ApplyContractTable(ContractTrackerTablePayload table)
    {
        Contracts = table.Contracts.ToList();
        return true;
    }

    /// <summary>
    /// Record the vendor trade panel the bot just opened (ApproachVendor 0x0062),
    /// stamped with the bot's current landblock so the projection can drop it once
    /// the bot walks away. Replaces any prior open vendor. Returns true.
    /// </summary>
    public bool ApplyVendorInfo(VendorInfoPayload vendor)
    {
        OpenVendor = vendor;
        OpenVendorLandblock = Self?.CellId is uint c ? c >> 16 : (uint?)null;
        return true;
    }

    /// <summary>
    /// Apply a single SendClientContractTracker (0x0315) update: remove the
    /// matching contract when <see cref="ContractTrackerPayload.DeleteContract"/>
    /// is set, otherwise upsert it by contract id (replace an existing entry or
    /// append a new one). Returns false when a delete targets a contract that is
    /// not tracked (no-op).
    /// </summary>
    public bool ApplyContractUpdate(ContractTrackerPayload update)
    {
        var entry = update.Entry;
        var remaining = Contracts.Where(c => c.ContractId != entry.ContractId).ToList();
        if (update.DeleteContract)
        {
            if (remaining.Count == Contracts.Count)
                return false; // delete targeted a contract we were not tracking
            Contracts = remaining;
            return true;
        }
        remaining.Add(entry);
        Contracts = remaining;
        return true;
    }

    /// <summary>
    /// Remove an object from the world snapshot. Stale-delete
    /// protection: drops the message if the incoming instance
    /// sequence is STRICTLY OLDER than the snapshot's current
    /// SeqInstance (wrap-aware). This handles the race where a
    /// respawn (new instance epoch) arrives before a stale
    /// delete from the previous epoch.
    ///
    /// If we never saw an ObjectCreate for this guid, the
    /// delete is a no-op (returns false). This is the common
    /// case where the bot enters the world after an object's
    /// brief lifetime, and the server flushes a delete for an
    /// object we never observed.
    ///
    /// Self-guid protection: refuse to delete our own player
    /// snapshot. A server-sent ObjectDelete for SelfGuid would
    /// indicate logout, which we model elsewhere — never wipe
    /// SelfGuid mid-session via the routine delete path.
    /// </summary>
    private bool ApplyObjectDelete(ObjectDeleteMessage od)
    {
        if (!_objects.TryGetValue(od.Guid, out var snap))
            return false;

        if (snap.SeqInstance is ushort cur
            && SequenceCompare.IsStrictlyNewer(cur, od.InstanceSequence))
            return false;

        if (SelfGuid is uint selfGuid && selfGuid == od.Guid)
        {
            Console.Error.WriteLine(
                $"[worldstate] ignoring ObjectDelete for SelfGuid 0x{od.Guid:X8}");
            return false;
        }

        _objects.Remove(od.Guid);
        return true;
    }

    /// <summary>
    /// Remove an item from the snapshot in response to a server
    /// InventoryRemoveObject (0x0024): the item has left the local-player
    /// inventory (a successful give, drop, use-consume, or sale). Without
    /// this the bot keeps a phantom inventory item and a later give/use of
    /// the removed guid is refused "Item not found!".
    ///
    /// No-op if the guid is unknown (returns false). Self-guid protection:
    /// never remove our own player snapshot (the server would not send an
    /// inventory-remove for the player object, but guard anyway).
    /// </summary>
    private bool ApplyInventoryRemove(InventoryRemoveObjectMessage ir)
    {
        if (!_objects.ContainsKey(ir.Guid))
            return false;

        if (SelfGuid is uint selfGuid && selfGuid == ir.Guid)
        {
            Console.Error.WriteLine(
                $"[worldstate] ignoring InventoryRemoveObject for SelfGuid 0x{ir.Guid:X8}");
            return false;
        }

        _objects.Remove(ir.Guid);
        return true;
    }

    private bool ApplyObjectCreate(ObjectCreateMessage oc)
    {
        // Merge semantics: refresh weenie/physics fields, but
        // preserve FirstSeen and PropertyInts. Sequence high-
        // water marks ADVANCE only — never reset to a lower
        // value the server happens to send in this ObjectCreate
        // (unless the instance epoch advanced; see below).
        var snap = GetOrCreateSnapshot(oc.Guid);

        // Two-level gate, mirroring ApplyUpdatePosition:
        // 1. If incoming instance is strictly older (wrap-aware)
        //    than what we already have, this is a stale resend
        //    that arrived after our world moved on — drop it
        //    entirely so it can't clobber identity/spatial fields
        //    with stale values.
        // 2. If incoming instance is strictly NEWER, the server
        //    has reset its per-instance counters, so reset ours
        //    too before AdvanceSeqX runs.
        // 3. If equal, we're in the same epoch; gate spatial
        //    fields on SeqObjectPosition so an out-of-order
        //    older ObjectCreate within the same epoch doesn't
        //    overwrite newer position state.
        if (!IsInstanceCurrentOrNewer(snap, oc.Physics.SeqObjectInstance))
            return false;

        var instanceAdvanced = IsInstanceStrictlyNewer(snap, oc.Physics.SeqObjectInstance);
        if (instanceAdvanced)
            snap.ResetForNewInstance();

        // Identity fields are intrinsic to the weenie/guid and
        // safe to refresh on every (non-stale-instance) ObjectCreate.
        snap.Name = oc.Weenie.Name;
        if (!string.IsNullOrEmpty(oc.Weenie.Name))
            _everObservedNames.Add(oc.Weenie.Name);
        snap.WeenieClassId = oc.Weenie.WeenieClassId;
        snap.ItemType = oc.Weenie.ItemType;
        snap.WeenieFlags = (uint)oc.Weenie.Flags;
        snap.WeenieFlags2 = (uint)oc.Weenie.Flags2;
        snap.ObjectDescriptionFlags = (uint)oc.Weenie.DescriptionFlags;
        snap.ValidLocations = oc.Weenie.ValidLocations;
        snap.CurrentWieldedLocation = oc.Weenie.CurrentlyWieldedLocation;
        // ContainerGuid/WielderGuid: populate when the header carries
        // them. ObjectCreate for landscape items omits both; for items
        // in someone's bag the server sets ContainerGuid; for equipped
        // items the server sets WielderGuid. Treat absence as "not in
        // container / not wielded" by overwriting with null - the
        // server re-emits ObjectCreate when the linkage changes.
        snap.ContainerGuid = oc.Weenie.ContainerGuid;
        snap.WielderGuid = oc.Weenie.WielderGuid;

        // Spatial/physics fields are gated by SeqObjectPosition
        // within the same instance epoch. After a ResetForNewInstance,
        // snap.SeqPosition is null and the compare always accepts.
        var positionFresh = SequenceCompare.IsCurrentOrNewer(
            oc.Physics.SeqObjectPosition, snap.SeqPosition);

        if (positionFresh)
        {
            snap.PhysicsState = oc.Physics.PhysicsState;

            if (oc.Physics.Position is { } p)
            {
                snap.CellId = p.LandblockId;
                snap.Position = new System.Numerics.Vector3(p.X, p.Y, p.Z);
                snap.Rotation = new System.Numerics.Quaternion(p.RotationX, p.RotationY, p.RotationZ, p.RotationW);
            }

            if (oc.Physics.Velocity is { } v)
                snap.Velocity = v;
        }

        snap.AdvanceSeqInstance(oc.Physics.SeqObjectInstance);
        snap.AdvanceSeqPosition(oc.Physics.SeqObjectPosition);
        snap.AdvanceSeqTeleport(oc.Physics.SeqObjectTeleport);
        snap.AdvanceSeqForcePosition(oc.Physics.SeqObjectForcePosition);
        snap.AdvanceSeqState(oc.Physics.SeqObjectState);
        snap.AdvanceSeqMovement(oc.Physics.SeqObjectMovement);
        snap.AdvanceSeqServerControl(oc.Physics.SeqObjectServerControl);
        snap.AdvanceSeqVisualDesc(oc.Physics.SeqObjectVisualDesc);
        snap.AdvanceSeqVector(oc.Physics.SeqObjectVector);

        snap.Touch();
        return true;
    }

    private bool ApplyUpdatePosition(UpdatePositionMessage up)
    {
        var snap = GetOrCreateSnapshot(up.Guid);

        if (!IsInstanceCurrentOrNewer(snap, up.InstanceSequence))
            return false;

        // New instance epoch → reset per-instance counters before
        // gating, so the smaller position-sequence values the
        // server starts emitting in this epoch are accepted.
        if (IsInstanceStrictlyNewer(snap, up.InstanceSequence))
            snap.ResetForNewInstance();
        else if (!SequenceCompare.IsCurrentOrNewer(up.PositionSequence, snap.SeqPosition))
            return false;

        snap.CellId = up.CellId;
        snap.Position = up.Position;
        snap.Rotation = up.Rotation;
        if (up.Velocity is { } v) snap.Velocity = v;

        snap.AdvanceSeqInstance(up.InstanceSequence);
        snap.AdvanceSeqPosition(up.PositionSequence);
        snap.AdvanceSeqTeleport(up.TeleportSequence);
        snap.AdvanceSeqForcePosition(up.ForcePositionSequence);

        snap.Touch();
        return true;
    }

    private bool ApplyMotion(MotionMessage mm)
    {
        var snap = GetOrCreateSnapshot(mm.Guid);

        if (!IsInstanceCurrentOrNewer(snap, mm.InstanceSequence))
            return false;

        if (IsInstanceStrictlyNewer(snap, mm.InstanceSequence))
            snap.ResetForNewInstance();
        else if (!SequenceCompare.IsCurrentOrNewer(mm.MovementSequence, snap.SeqMovement))
            return false;

        snap.LastMovementType = mm.MovementType;
        snap.LastMotionFlags = mm.MotionFlags;
        snap.LastMotionStyle = mm.CurrentStyle;

        snap.AdvanceSeqInstance(mm.InstanceSequence);
        snap.AdvanceSeqMovement(mm.MovementSequence);
        snap.AdvanceSeqServerControl(mm.ServerControlSequence);

        snap.Touch();
        return true;
    }

    private bool ApplySetState(SetStateMessage ss)
    {
        var snap = GetOrCreateSnapshot(ss.Guid);

        if (!IsInstanceCurrentOrNewer(snap, ss.InstanceSequence))
            return false;

        if (IsInstanceStrictlyNewer(snap, ss.InstanceSequence))
            snap.ResetForNewInstance();
        else if (!SequenceCompare.IsCurrentOrNewer(ss.StateSequence, snap.SeqState))
            return false;

        snap.PhysicsState = ss.State;
        snap.AdvanceSeqInstance(ss.InstanceSequence);
        snap.AdvanceSeqState(ss.StateSequence);

        snap.Touch();
        return true;
    }

    private bool ApplyPrivatePropertyInt(PrivateUpdatePropertyIntMessage pup)
    {
        // No guid in wire format — implicitly targets the
        // receiving session's player. If SelfGuid is unset
        // (caller didn't pre-seed AND PlayerCreate hasn't
        // arrived), drop with a warning. Driver should pre-seed
        // at EnterWorld time.
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdatePropertyInt before SelfGuid known: " +
                $"prop={pup.PropertyName} val={pup.Value} (dropped)");
            return false;
        }

        // Per-property byte-sequence check. The server keys the
        // PropertyInt ByteSequence by (SequenceType.UpdatePropertyInt,
        // property) — see _selfPropertyByteSeq — so each property has
        // an independent counter; gating against a single shared max
        // would falsely drop a valid update once two properties'
        // counters diverge (e.g. Age ticking past a first Level-up).
        byte? prevSeq = _selfPropertyByteSeq.TryGetValue(pup.Property, out var s)
            ? s
            : (byte?)null;
        if (!SequenceCompare.IsCurrentOrNewer(pup.Sequence, prevSeq))
            return false;
        _selfPropertyByteSeq[pup.Property] = pup.Sequence;

        var snap = GetOrCreateSnapshot(selfGuid);
        snap.PropertyInts ??= new Dictionary<uint, int>();
        snap.PropertyInts[pup.Property] = pup.Value;
        snap.Touch();
        return true;
    }

    /// <summary>
    /// Apply a PrivateUpdatePropertyInt64 (0x02CF). Like
    /// PrivateUpdatePropertyInt it has no guid and is implicitly scoped
    /// to the receiving session's player; carries player XP totals
    /// (TotalExperience=1, AvailableExperience=2). Stale-gated PER
    /// PROPERTY (see <see cref="_selfPropertyInt64ByteSeq"/>) because the
    /// server uses an independent ByteSequence per Int64 property.
    /// </summary>
    private bool ApplyPrivatePropertyInt64(PrivateUpdatePropertyInt64Message pup)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdatePropertyInt64 before SelfGuid known: " +
                $"prop={pup.PropertyName} val={pup.Value} (dropped)");
            return false;
        }

        byte? prevSeq = _selfPropertyInt64ByteSeq.TryGetValue(pup.Property, out var s)
            ? s
            : (byte?)null;
        if (!SequenceCompare.IsCurrentOrNewer(pup.Sequence, prevSeq))
            return false;
        _selfPropertyInt64ByteSeq[pup.Property] = pup.Sequence;

        var snap = GetOrCreateSnapshot(selfGuid);
        snap.PropertyInt64s ??= new Dictionary<uint, long>();
        snap.PropertyInt64s[pup.Property] = pup.Value;
        snap.Touch();
        return true;
    }

    /// <summary>
    /// Seed a self PropertyInt from the login PlayerDescription bundle
    /// (0x0013), which carries no per-property byte sequence. Unlike
    /// <see cref="ApplyPrivatePropertyInt"/> this DOES NOT touch the
    /// stale-gating high-water map, so the first real discrete update
    /// (0x02CD, whatever its starting sequence) is still accepted. The
    /// seed is SKIPPED when a discrete update has already been applied
    /// for the property (its byte-seq map entry exists) so an
    /// out-of-order or re-sent bundle never clobbers a fresher discrete
    /// value. Returns true if the value was seeded.
    /// </summary>
    public bool SeedSelfPropertyInt(uint property, int value)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PlayerDescription PropertyInt seed before SelfGuid known: " +
                $"prop={property} val={value} (dropped)");
            return false;
        }
        if (_selfPropertyByteSeq.ContainsKey(property))
            return false; // a discrete update already owns this property

        var snap = GetOrCreateSnapshot(selfGuid);
        snap.PropertyInts ??= new Dictionary<uint, int>();
        snap.PropertyInts[property] = value;
        snap.Touch();
        return true;
    }

    /// <summary>
    /// Seed a self PropertyInt64 from the login PlayerDescription bundle.
    /// Same contract as <see cref="SeedSelfPropertyInt"/>: leaves the
    /// Int64 stale-gating map untouched and defers to any
    /// already-applied discrete update for the property.
    /// </summary>
    public bool SeedSelfPropertyInt64(uint property, long value)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PlayerDescription PropertyInt64 seed before SelfGuid known: " +
                $"prop={property} val={value} (dropped)");
            return false;
        }
        if (_selfPropertyInt64ByteSeq.ContainsKey(property))
            return false;

        var snap = GetOrCreateSnapshot(selfGuid);
        snap.PropertyInt64s ??= new Dictionary<uint, long>();
        snap.PropertyInt64s[property] = value;
        snap.Touch();
        return true;
    }

    /// <summary>
    /// Seed the bot's attribute ranks from the login PlayerDescription
    /// bundle. Merges into the self snapshot: if a discrete
    /// PrivateUpdateAttribute (0x02E3) already populated an attribute before
    /// this login seed (SelfGuid is known before world entry, so an early
    /// discrete can land first), that fresher entry is preserved and only the
    /// missing attributes are filled in. A re-sent login bundle therefore adds
    /// nothing (all names already present) — same effect as the old
    /// first-seed-wins contract, without clobbering live discrete state.
    /// Returns true if the snapshot was populated or extended.
    /// </summary>
    public bool SeedSelfAttributes(IReadOnlyList<PdAttribute> attributes)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                "[worldstate] PlayerDescription attribute seed before SelfGuid known (dropped)");
            return false;
        }
        var snap = GetOrCreateSnapshot(selfGuid);
        if (snap.SelfAttributes is null)
        {
            snap.SelfAttributes = attributes;
            snap.Touch();
            return true;
        }
        var merged = new List<PdAttribute>(snap.SelfAttributes);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in merged)
            present.Add(a.Name);
        bool added = false;
        foreach (var a in attributes)
        {
            if (present.Add(a.Name))
            {
                merged.Add(a);
                added = true;
            }
        }
        if (!added)
            return false; // every attribute already known (discrete or re-send)
        snap.SelfAttributes = merged;
        snap.Touch();
        return true;
    }

    /// <summary>
    /// Seed the bot's skills from the login PlayerDescription bundle. Same
    /// merge contract as <see cref="SeedSelfAttributes"/>, keyed by skill id:
    /// a discrete PrivateUpdateSkill (0x02DD) that landed before login is
    /// preserved and only the missing skills are filled in.
    /// </summary>
    public bool SeedSelfSkills(IReadOnlyList<PdSkill> skills)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                "[worldstate] PlayerDescription skill seed before SelfGuid known (dropped)");
            return false;
        }
        var snap = GetOrCreateSnapshot(selfGuid);
        if (snap.SelfSkills is null)
        {
            snap.SelfSkills = skills;
            snap.Touch();
            return true;
        }
        var merged = new List<PdSkill>(snap.SelfSkills);
        var present = new HashSet<uint>();
        foreach (var s in merged)
            present.Add(s.Id);
        bool added = false;
        foreach (var s in skills)
        {
            if (present.Add(s.Id))
            {
                merged.Add(s);
                added = true;
            }
        }
        if (!added)
            return false; // every skill already known (discrete or re-send)
        snap.SelfSkills = merged;
        snap.Touch();
        return true;
    }

    /// <summary>
    /// Apply a discrete PrivateUpdateSkill (0x02DD): the server reports a
    /// single skill's new descriptor after a RaiseSkill (or a full sync).
    /// Upserts the matching PdSkill in the self snapshot by skill id so the
    /// surfaced raised ranks / advancement class stay live after a spend
    /// (the login bundle is otherwise stale until relogin). Stale-gated PER
    /// SKILL (see <see cref="_selfSkillByteSeq"/>). No guid on the wire;
    /// implicitly the receiving session's player.
    /// </summary>
    private bool ApplyPrivateSkill(PrivateUpdateSkillMessage m)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdateSkill before SelfGuid known: " +
                $"skill={m.Skill} ranks={m.Ranks} (dropped)");
            return false;
        }

        byte? prevSeq = _selfSkillByteSeq.TryGetValue(m.Skill, out var s)
            ? s
            : (byte?)null;
        if (!SequenceCompare.IsCurrentOrNewer(m.Sequence, prevSeq))
            return false;
        _selfSkillByteSeq[m.Skill] = m.Sequence;

        var snap = GetOrCreateSnapshot(selfGuid);
        var updated = new PdSkill(
            GameEventPayloadDecoder.SkillName(m.Skill),
            m.Skill, m.AdvancementClass, m.Ranks, m.InitLevel, m.ExperienceSpent);
        snap.SelfSkills = UpsertSkill(snap.SelfSkills, updated);
        snap.Touch();
        return true;
    }

    /// <summary>
    /// Apply a discrete PrivateUpdateAttribute (0x02E3): a single primary
    /// attribute's new descriptor after a RaiseAttribute (or full sync).
    /// Upserts the matching PdAttribute by canonical lowercase NAME (the
    /// attribute snapshot carries no id; the name is the stable key shared
    /// with the login seed). Stale-gated PER ATTRIBUTE
    /// (see <see cref="_selfAttributeByteSeq"/>). Only the six primaries
    /// flow here; vital max-pools ride 0x02E7/0x02E9.
    /// </summary>
    private bool ApplyPrivateAttribute(PrivateUpdateAttributeMessage m)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdateAttribute before SelfGuid known: " +
                $"attr={m.Attribute} ranks={m.Ranks} (dropped)");
            return false;
        }

        var name = GameEventPayloadDecoder.PrimaryAttributeName(m.Attribute);
        if (name is null)
            return false; // not one of the six primary attributes

        byte? prevSeq = _selfAttributeByteSeq.TryGetValue(m.Attribute, out var s)
            ? s
            : (byte?)null;
        if (!SequenceCompare.IsCurrentOrNewer(m.Sequence, prevSeq))
            return false;
        _selfAttributeByteSeq[m.Attribute] = m.Sequence;

        var snap = GetOrCreateSnapshot(selfGuid);
        var updated = new PdAttribute(
            name, m.StartingValue + m.Ranks, m.Ranks, m.ExperienceSpent);
        snap.SelfAttributes = UpsertAttribute(snap.SelfAttributes, updated);
        snap.Touch();
        return true;
    }

    // Replace the PdSkill with a matching id, or append if new. Returns a
    // fresh list (the snapshot field is replaced wholesale) so a concurrent
    // projection read never sees a half-mutated list.
    private static IReadOnlyList<PdSkill> UpsertSkill(
        IReadOnlyList<PdSkill>? existing, PdSkill updated)
    {
        var list = existing is null
            ? new List<PdSkill>()
            : new List<PdSkill>(existing);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id == updated.Id)
            {
                list[i] = updated;
                return list;
            }
        }
        list.Add(updated);
        return list;
    }

    // Replace the PdAttribute with a matching name (ordinal), or append.
    private static IReadOnlyList<PdAttribute> UpsertAttribute(
        IReadOnlyList<PdAttribute>? existing, PdAttribute updated)
    {
        var list = existing is null
            ? new List<PdAttribute>()
            : new List<PdAttribute>(existing);
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, updated.Name, StringComparison.Ordinal))
            {
                list[i] = updated;
                return list;
            }
        }
        list.Add(updated);
        return list;
    }

    /// PrivateUpdatePropertyInt it has no guid and is implicitly
    /// scoped to the receiving session's player. We only track the
    /// HEALTH vital; Stamina/Mana updates are accepted (to advance the
    /// shared vital sequence) but otherwise ignored. HealthMax is the
    /// peak Current observed so the projection can compute a fraction
    /// without AC's Endurance-derived max-vital formula.
    /// </summary>
    private bool ApplyPrivateVital(PrivateUpdateVitalMessage v)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdateVital before SelfGuid known: " +
                $"vital=0x{v.Vital:X} current={v.Current} (dropped)");
            return false;
        }

        // Only the health vital drives self-health state.
        if (!v.IsHealth)
            return false;

        // Per-(type,vital) byte-sequence gate — drop stale/reordered
        // health updates. Separate counter from the property family.
        if (!SequenceCompare.IsCurrentOrNewer(v.Sequence, _selfHealthVitalByteSeq))
            return false;
        _selfHealthVitalByteSeq = v.Sequence;

        WriteSelfHealth(selfGuid, v.Current);
        return true;
    }

    /// <summary>
    /// Apply a PrivateUpdateAttribute2ndLevel (0x02E9) - the per-tick
    /// CURRENT-LEVEL vital update. This is the timely source for the
    /// bot's own current HP (damage/regen/death/respawn). Only the HEALTH
    /// vital is tracked; here health is keyed by Health (2), unlike the
    /// 0x02E7 descriptor (keyed by MaxHealth (1)), and uses its own
    /// distinct sequence counter.
    /// </summary>
    private bool ApplyPrivateVitalLevel(PrivateUpdateAttribute2ndLevelMessage a)
    {
        if (SelfGuid is not uint selfGuid)
        {
            Console.Error.WriteLine(
                $"[worldstate] PrivateUpdateAttribute2ndLevel before SelfGuid known: " +
                $"vital=0x{a.Vital:X} current={a.Current} (dropped)");
            return false;
        }

        if (!a.IsHealth)
            return false;

        if (!SequenceCompare.IsCurrentOrNewer(a.Sequence, _selfHealthLevelByteSeq))
            return false;
        _selfHealthLevelByteSeq = a.Sequence;

        WriteSelfHealth(selfGuid, a.Current);
        return true;
    }

    /// <summary>
    /// Write the bot's current health onto the self snapshot and update
    /// the peak-observed max. Shared by both health-bearing wire messages
    /// (0x02E7 descriptor and 0x02E9 current-level). Peak-observed max
    /// seeds to the true max on a full-health login/respawn, rises on
    /// level-up at full health, and never shrinks from taking damage -
    /// avoiding a reimplementation of AC's Endurance-derived max formula.
    /// The peak can UNDER-estimate the true max when the first reading is
    /// a sub-max value (logged in damaged); the raw HealthRising trend
    /// (below) lets Strategy detect that case (current still climbing via
    /// regen => below true max) instead of trusting a misleading fraction.
    /// </summary>
    private void WriteSelfHealth(uint selfGuid, uint current)
    {
        var snap = GetOrCreateSnapshot(selfGuid);
        var prev = snap.HealthCurrent;
        snap.HealthCurrent = current;
        snap.HealthMax = snap.HealthMax is uint prevMax && prevMax >= current
            ? prevMax
            : current;
        // Raw observed trend over DISTINCT current readings. Both wire
        // sources (0x02E7 descriptor and 0x02E9 current-level) feed through
        // here and both report the same Health current, so a redundant
        // same-value update from the alternate source must NOT clobber a
        // rising signal: only a strict change updates the trend; an equal
        // reading leaves the prior trend unchanged. null until the first
        // distinct change establishes a direction.
        if (prev is uint p)
        {
            if (current != p)
                snap.HealthRising = current > p;
        }
        else
        {
            snap.HealthRising = null;
        }
        snap.Touch();
    }

    private WorldObjectSnapshot GetOrCreateSnapshot(uint guid)
    {
        if (!_objects.TryGetValue(guid, out var snap))
        {
            snap = new WorldObjectSnapshot(guid);
            _objects[guid] = snap;
        }
        return snap;
    }

    /// <summary>
    /// True if <paramref name="incomingInstance"/> is at least
    /// as recent as the snapshot's current SeqInstance (or if
    /// SeqInstance is unset).
    /// </summary>
    private static bool IsInstanceCurrentOrNewer(WorldObjectSnapshot snap, ushort incomingInstance)
        => SequenceCompare.IsCurrentOrNewer(incomingInstance, snap.SeqInstance);

    /// <summary>
    /// True if the incoming instance is STRICTLY newer than the
    /// snapshot's SeqInstance (i.e., a new instance epoch).
    /// Returns false if SeqInstance is unset (no prior epoch
    /// to compare against).
    /// </summary>
    private static bool IsInstanceStrictlyNewer(WorldObjectSnapshot snap, ushort incomingInstance)
        => snap.SeqInstance is ushort cur
           && SequenceCompare.IsStrictlyNewer(incomingInstance, cur);

    /// <summary>
    /// Format a one-line debug summary suitable for periodic
    /// log output from the receive loop.
    /// </summary>
    public string FormatSummary()
    {
        var selfStr = "(unset)";
        if (Self is { } s)
        {
            var pos = s.CellId is uint c
                ? $"lb=0x{c:X8} xyz=({s.Position.X:F1},{s.Position.Y:F1},{s.Position.Z:F1})"
                : "(no pos)";
            selfStr = $"0x{s.Guid:X8} {pos}";
        }
        var props = Self?.PropertyInts?.Count ?? 0;
        return $"objects={_objects.Count} self={selfStr} selfProps={props}";
    }

    // ---- Spatial queries ----
    //
    // Public API for the future Motor/Tactics layer. All methods
    // return eagerly-materialized lists — never lazy enumerators
    // over the underlying dictionary — so a caller iterating
    // results while another callback applies new messages can't
    // trip a "Collection was modified" exception.
    //
    // All queries skip snapshots without a CellId (no spatial
    // state yet) and skip the origin itself (matched by Guid,
    // not reference identity, so callers can pass a copy/stub).

    /// <summary>
    /// Enumerate every snapshot in the world with a known position,
    /// paired with its squared distance to <paramref name="origin"/>.
    /// Excludes the origin itself and any object without a CellId.
    /// Returns empty if origin has no CellId.
    /// Results are NOT sorted — use NearestN if you need ordering.
    /// </summary>
    public IReadOnlyList<(WorldObjectSnapshot Object, float SquaredDistance)>
        EnumerateNearby(WorldObjectSnapshot origin)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));
        if (origin.CellId is not uint originCell)
            return Array.Empty<(WorldObjectSnapshot, float)>();

        var result = new List<(WorldObjectSnapshot, float)>(_objects.Count);
        foreach (var snap in _objects.Values)
        {
            if (snap.Guid == origin.Guid) continue;
            if (snap.CellId is not uint cell) continue;
            var d2 = WorldDistance.SquaredDistanceBetween(
                originCell, origin.Position, cell, snap.Position);
            result.Add((snap, d2));
        }
        return result;
    }

    /// <summary>
    /// All objects within <paramref name="radius"/> game units of
    /// <paramref name="origin"/>. Order is unspecified — call
    /// NearestN if you need distance-sorted output.
    /// Throws on negative or NaN radius.
    /// </summary>
    public IReadOnlyList<WorldObjectSnapshot>
        WithinRadius(WorldObjectSnapshot origin, float radius)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));
        if (float.IsNaN(radius) || radius < 0f)
            throw new ArgumentOutOfRangeException(nameof(radius),
                $"radius must be non-negative and finite (got {radius})");

        var radiusSq = radius * radius;
        var nearby = EnumerateNearby(origin);
        var result = new List<WorldObjectSnapshot>(nearby.Count);
        foreach (var (snap, d2) in nearby)
        {
            if (d2 <= radiusSq) result.Add(snap);
        }
        return result;
    }

    /// <summary>
    /// The <paramref name="count"/> objects closest to
    /// <paramref name="origin"/>, sorted ascending by distance.
    /// Tie-break: guid ascending (deterministic).
    /// If fewer objects than <paramref name="count"/> have known
    /// positions, returns all of them.
    /// Throws on negative count.
    /// </summary>
    public IReadOnlyList<WorldObjectSnapshot>
        NearestN(WorldObjectSnapshot origin, int count)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count),
                $"count must be non-negative (got {count})");
        if (count == 0)
            return Array.Empty<WorldObjectSnapshot>();

        var nearby = EnumerateNearby(origin);
        if (nearby.Count == 0)
            return Array.Empty<WorldObjectSnapshot>();

        // Stable sort by (squared-distance asc, guid asc).
        var sorted = new List<(WorldObjectSnapshot Obj, float D2)>(nearby);
        sorted.Sort((a, b) =>
        {
            var cmp = a.D2.CompareTo(b.D2);
            return cmp != 0 ? cmp : a.Obj.Guid.CompareTo(b.Obj.Guid);
        });

        var take = Math.Min(count, sorted.Count);
        var result = new List<WorldObjectSnapshot>(take);
        for (var i = 0; i < take; i++) result.Add(sorted[i].Obj);
        return result;
    }

    /// <summary>
    /// All objects whose ItemType bitmask intersects
    /// <paramref name="itemTypeMask"/>. AC's ItemType is a bit
    /// field — `Creature | MeleeWeapon | ...` — so a mask of
    /// multiple bits matches objects of any of those types.
    /// Objects without an ItemType (no ObjectCreate yet) are
    /// excluded. A zero mask matches nothing.
    /// </summary>
    public IReadOnlyList<WorldObjectSnapshot> OfType(uint itemTypeMask)
    {
        if (itemTypeMask == 0)
            return Array.Empty<WorldObjectSnapshot>();

        var result = new List<WorldObjectSnapshot>();
        foreach (var snap in _objects.Values)
        {
            if (snap.ItemType is uint t && (t & itemTypeMask) != 0)
                result.Add(snap);
        }
        return result;
    }
}
