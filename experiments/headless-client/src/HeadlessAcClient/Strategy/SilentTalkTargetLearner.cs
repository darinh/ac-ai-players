// SPDX-License-Identifier: AGPL-3.0-or-later
// SilentTalkTargetLearner — runtime-LEARNED set of creature WCIDs that never
// answer a Talk with NPC dialog.
//
// WHY: the autonomous no-quest fallback (NoQuestKnowledgePolicy) Talks the
// nearest non-monster creature to coax dialog out of it. Some Creature-typed
// wire objects are inert scenery with no dialog tree (e.g. a "Fishing Hole",
// wcid 22257) — Talking them produces NOTHING, yet they pass the fallback's
// `IsCreature && !IsMonster` filter, so when the LLM is unavailable the bot
// marches across the map (observed: 67u each) to "Talk" object after object of
// the same useless kind, burning tempo. There is no single wire flag that
// means "conversational NPC" (dialog capability is server-side emote data), so
// this is learned BEHAVIOURALLY from the wire instead of hardcoded.
//
// HOW: the Motor records every Talk it dispatches (guid + wcid) and every
// observed NpcDialog source guid. A wcid that the bot has Talked on enough
// DISTINCT instances, each of which fell silent for a grace window with NO
// dialog ever observed from that wcid, is concluded "silent" and surfaced to
// the fallback so it stops auto-Talking that kind. ANY dialog from a wcid
// immunises it permanently (it is a real talker). Mirrors the combat-feel
// ledger's per-(wcid) learning.
//
// SCOPE / SAFETY: this set ONLY filters the autonomous fallback's civilian
// Talk step. It NEVER blocks an LLM-authored Talk goal, so a false positive
// only stops the fallback auto-Talking a kind — it can never override the
// Strategy layer. Keys ONLY on the bot's OWN Talk dispatches and OBSERVED
// dialog source guids: no monster/NPC name list, no wcid list, no
// landblock, no object-type priority.

using System;
using System.Collections.Generic;
using System.Linq;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Outcome of a single Talk-dispatch probe record. Returned by
/// <see cref="SilentTalkTargetLearner.RecordTalkDispatch"/> PURELY so the Motor
/// can log WHY a probe was (or was not) opened — the learner's behaviour is
/// unchanged by the caller ignoring it. Diagnoses the unobservable-learner
/// failure mode where a fallback Talk-tour of inert scenery never concludes
/// silent (e.g. a null wcid at dispatch silently drops every probe).
/// </summary>
internal enum TalkProbeOutcome
{
    /// <summary>A fresh dialog-response window opened for this instance.</summary>
    Recorded,
    /// <summary>The creature's wcid was unknown (null) — cannot be learned.</summary>
    IgnoredUnknownWcid,
    /// <summary>The wcid already proved conversational — not re-probed.</summary>
    IgnoredImmuneWcid,
}

internal sealed class SilentTalkTargetLearner
{
    // How long after a Talk dispatch to wait for a dialog response before
    // concluding that THIS instance answered nothing. Must comfortably exceed
    // normal server dialog latency (a Tell arrives within a tick or two of the
    // Use), so a real NPC whose dialog merely lags is not mis-counted.
    private readonly double _graceWindowSeconds;

    // How many DISTINCT silent instances of a wcid are required before the kind
    // is concluded non-conversational. >1 so a single fluke (dialog dropped /
    // bot walked off) cannot blacklist a kind on its own.
    private readonly int _silentConfirmThreshold;

    // guid -> (wcid, dispatch time) for Talk dispatches awaiting a verdict.
    private readonly Dictionary<uint, (uint Wcid, DateTime At)> _pending = new();
    // wcid -> count of DISTINCT instances that matured silent.
    private readonly Dictionary<uint, int> _silentCounts = new();
    // guids already counted toward a silent verdict (count each instance once).
    private readonly HashSet<uint> _countedGuids = new();
    // wcids proven to answer dialog at least once — permanently immune.
    private readonly HashSet<uint> _dialogCapableWcids = new();
    // the learned verdict surfaced to the fallback.
    private readonly HashSet<uint> _silentWcids = new();

    public SilentTalkTargetLearner(
        double graceWindowSeconds = 12.0, int silentConfirmThreshold = 2)
    {
        _graceWindowSeconds = graceWindowSeconds;
        _silentConfirmThreshold = silentConfirmThreshold;
    }

    /// <summary>
    /// Record that the bot just dispatched a Talk against <paramref name="guid"/>
    /// (a creature of <paramref name="wcid"/>). Starts the dialog-response
    /// window for this instance. A null wcid (unknown class) cannot be learned
    /// and is ignored; an already-proven talker wcid is not re-probed. Returns
    /// the <see cref="TalkProbeOutcome"/> so the Motor can log why a probe was
    /// or was not opened (the learner is unaffected by the caller ignoring it).
    /// </summary>
    public TalkProbeOutcome RecordTalkDispatch(uint guid, uint? wcid, DateTime nowUtc)
    {
        if (wcid is not uint w) return TalkProbeOutcome.IgnoredUnknownWcid;
        if (_dialogCapableWcids.Contains(w)) return TalkProbeOutcome.IgnoredImmuneWcid;
        _pending[guid] = (w, nowUtc);
        return TalkProbeOutcome.Recorded;
    }

    /// <summary>
    /// Record an observed NPC dialog from <paramref name="guid"/>. Any dialog
    /// permanently immunises that guid's wcid (it is a real talker) and clears
    /// any silent verdict/count against it. <paramref name="wcid"/> may be null
    /// when the dialog source is no longer in view — the pending probe's
    /// remembered wcid is used as a fallback.
    /// </summary>
    public void RecordDialogFrom(uint guid, uint? wcid)
    {
        uint? resolved = wcid;
        if (resolved is null && _pending.TryGetValue(guid, out var p))
            resolved = p.Wcid;
        if (resolved is uint w)
        {
            _dialogCapableWcids.Add(w);
            _silentWcids.Remove(w);
            _silentCounts.Remove(w);
        }
        _pending.Remove(guid);
    }

    /// <summary>
    /// Record a dialog-like response that carries NO source guid (e.g. a
    /// PopupString — "the world is telling the player something": an NPC reply,
    /// quest popup, or refusal). Correlates it to the MOST-RECENT pending Talk
    /// probe still inside the grace window and immunises THAT kind, because a
    /// Talk that drew ANY response (an attributed Tell OR an unattributed
    /// popup) is, by definition, not silent. If no Talk is pending within the
    /// window the response is unrelated to a Talk and is ignored. SAFE
    /// direction: at worst an unrelated popup that happens to land within the
    /// window of a Talk immunises that kind (LESS suppression) — it can never
    /// cause MORE suppression.
    /// </summary>
    public void RecordUnattributedDialog(DateTime nowUtc)
    {
        uint? bestGuid = null;
        DateTime bestAt = DateTime.MinValue;
        foreach (var kv in _pending)
        {
            if ((nowUtc - kv.Value.At).TotalSeconds <= _graceWindowSeconds
                && kv.Value.At >= bestAt)
            {
                bestAt = kv.Value.At;
                bestGuid = kv.Key;
            }
        }
        if (bestGuid is uint g && _pending.TryGetValue(g, out var probe))
            RecordDialogFrom(g, probe.Wcid);
    }

    /// <summary>
    /// Promote any pending probe that has aged past the grace window with no
    /// dialog into the silent tally, and conclude a kind silent once enough
    /// distinct instances have. Call once per tick. Cheap (small dictionary).
    /// Returns the wcids (if any) that crossed the silent threshold on THIS
    /// call, so the Motor can log the conclusion (a kind being concluded silent
    /// is the moment the fallback starts skipping it). Empty when nothing new
    /// concluded; existing callers may ignore the return without behaviour change.
    /// </summary>
    public IReadOnlyList<uint> Evaluate(DateTime nowUtc)
    {
        List<uint>? matured = null;
        foreach (var kv in _pending)
        {
            if ((nowUtc - kv.Value.At).TotalSeconds >= _graceWindowSeconds)
                (matured ??= new List<uint>()).Add(kv.Key);
        }
        if (matured is null) return Array.Empty<uint>();

        List<uint>? newlySilent = null;
        foreach (var guid in matured)
        {
            var w = _pending[guid].Wcid;
            _pending.Remove(guid);
            // A late dialog may have immunised the kind after dispatch.
            if (_dialogCapableWcids.Contains(w)) continue;
            // Count each instance (guid) once, even if it was re-Talked.
            if (!_countedGuids.Add(guid)) continue;
            var c = _silentCounts.GetValueOrDefault(w) + 1;
            _silentCounts[w] = c;
            // _silentWcids.Add returns true only the FIRST time the kind crosses
            // the threshold, so report each newly-concluded wcid exactly once.
            if (c >= _silentConfirmThreshold && _silentWcids.Add(w))
                (newlySilent ??= new List<uint>()).Add(w);
        }
        return newlySilent ?? (IReadOnlyList<uint>)Array.Empty<uint>();
    }

    /// <summary>
    /// True once the kind has been concluded non-conversational. The fallback
    /// Talk step uses this to skip such creatures. Unknown/null wcid is never
    /// silent.
    /// </summary>
    public bool IsSilent(uint? wcid) => wcid is uint w && _silentWcids.Contains(w);

    /// <summary>Count of kinds currently concluded silent (diagnostics).</summary>
    public int SilentWcidCount => _silentWcids.Count;

    /// <summary>
    /// Distinct silent instances tallied for a wcid so far (diagnostics:
    /// progress toward the silent-confirm threshold). 0 for null/unknown or a
    /// wcid with no matured-silent instances yet. Lets the Motor log how close
    /// a kind is to being concluded non-conversational.
    /// </summary>
    public int DistinctSilentInstances(uint? wcid)
        => wcid is uint w ? _silentCounts.GetValueOrDefault(w) : 0;

    /// <summary>Snapshot of the learned silent wcids (diagnostics/telemetry).</summary>
    public IReadOnlyCollection<uint> SilentWcids => _silentWcids.ToArray();
}
