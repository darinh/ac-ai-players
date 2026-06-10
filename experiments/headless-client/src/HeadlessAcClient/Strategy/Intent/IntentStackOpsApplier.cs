// SPDX-License-Identifier: AGPL-3.0-or-later
// IntentStackOpsApplier — atomic apply of an LLM-emitted stack_ops
// batch to an IntentStack. All-or-nothing semantics: if any op fails
// (revision mismatch, depth overflow, root pop, invalid spec), the
// entire batch is rejected and the stack is untouched.
//
// Implementation: we dry-run the batch against a clone of the stack
// state. Only if every op succeeds do we apply them to the real
// stack. This avoids the "partial mutation, now what?" problem the
// LLM cannot reason about.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HeadlessAcClient.Strategy.Intent;

internal enum BatchApplyResult
{
    Ok                = 0,
    RejectedRevision  = 1,
    RejectedEmpty     = 2,
    RejectedOverflow  = 3,
    RejectedRootPop   = 4,
    RejectedInvalid   = 5,
}

internal sealed record BatchApplyOutcome(
    BatchApplyResult Result,
    string? RejectReason,
    IReadOnlyList<string> AppliedLog)
{
    /// <summary>
    /// True when the batch applied despite a stale echoed revision because it was
    /// PUSH-only. The reachable cause is a root whose deadline elapsed mid-call
    /// (marked Blocked in place, revision bumped, no event) — see the TryApply
    /// stale-revision comment for the full reachability argument. Surfaced for
    /// diagnostics so a deploy log can show the tolerance firing.
    /// </summary>
    public bool StaleRevisionTolerated { get; init; }
}

internal static class IntentStackOpsApplier
{
    /// <summary>
    /// Try to apply <paramref name="ops"/> to <paramref name="stack"/> as
    /// a single batch. If any op would fail, NONE are applied. Returns
    /// an outcome carrying a human-readable per-op log so the caller
    /// can log and feed into training data.
    ///
    /// IDs in `push` ops are filled with the allocator if the LLM did
    /// not supply one; if the LLM supplied a duplicate of an existing
    /// frame id, the op is rejected (avoids ambiguous "i-005" debug
    /// output).
    /// </summary>
    public static BatchApplyOutcome TryApply(
        IntentStack stack,
        IntentIdAllocator allocator,
        IReadOnlyList<IntentStackOp>? ops,
        long? echoedRevision,
        WorldStateProjection world,
        EventStream events,
        DateTime utcNow)
    {
        if (ops is null || ops.Count == 0)
        {
            return new BatchApplyOutcome(BatchApplyResult.Ok, null, Array.Empty<string>());
        }

        // A stale echoed revision means the stack changed between when the LLM
        // saw it (prompt render) and now (apply). The decision loop runs at most
        // ONE LLM call in flight (LlmGoalPolicy._inflight gates it), so the only
        // stack mutations in that window come from IntentStack.CheckTopForCompletion
        // on the Motor tick — never another LLM op. Of those, a child auto-pop and
        // an in-place root Completed mark both emit a GoalCompleted event, which
        // LlmGoalPolicy discards (HasPlanInvalidatingSince) BEFORE reaching this
        // apply — so the stale revision that actually arrives here is the no-event
        // bump: a root whose deadline elapsed, marked Blocked in place (depth
        // unchanged). A PUSH does not depend on that: it adds a new frame above the
        // CURRENT top, so it lands validly above the Blocked root (which resurfaces
        // for the LLM to REPLACE_TOP once the pushed child completes — the LLM's
        // just-computed decision is honored rather than wasted on a deadline that
        // happened to elapse mid-call). So a PUSH-ONLY batch is applied — the
        // dry-run below still re-validates depth + duplicate-id against the live
        // state. A batch containing REPLACE_TOP/POP_TOP/MARK_TOP_BLOCKED targets the
        // top BY IDENTITY, which the intervening mutation can change, so it stays
        // strictly revision-guarded. Mechanical optimistic-concurrency bookkeeping;
        // no game knowledge.
        var staleRevisionTolerated = false;
        if (echoedRevision is long er && er != stack.Revision)
        {
            var pushOnly = ops.All(o => o.Op == IntentStackOpKind.Push);
            if (!pushOnly)
            {
                return new BatchApplyOutcome(
                    BatchApplyResult.RejectedRevision,
                    $"echoed revision {er} != current stack revision {stack.Revision}",
                    Array.Empty<string>());
            }
            staleRevisionTolerated = true;
        }

        // Dry-run against a logical-only mirror of the stack so we can
        // validate the whole batch before touching the real stack.
        // We don't need a real IntentStack clone — just track the
        // depth and id-set the ops would produce.
        var depth = stack.Depth;
        var maxDepth = stack.MaxDepth;
        var existingIds = new HashSet<string>(stack.Frames.Select(f => f.Id), StringComparer.Ordinal);

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            switch (op.Op)
            {
                case IntentStackOpKind.Push:
                    if (op.Intent is null)
                        return Reject(BatchApplyResult.RejectedInvalid, $"op[{i}] push: missing intent");
                    if (op.Intent.Completion is null)
                        return Reject(BatchApplyResult.RejectedInvalid, $"op[{i}] push: missing completion predicate");
                    if (string.IsNullOrWhiteSpace(op.Intent.Kind))
                        return Reject(BatchApplyResult.RejectedInvalid, $"op[{i}] push: blank kind");
                    if (depth >= maxDepth)
                        return Reject(BatchApplyResult.RejectedOverflow, $"op[{i}] push: would exceed max depth {maxDepth}");
                    if (!string.IsNullOrWhiteSpace(op.Intent.Id) && existingIds.Contains(op.Intent.Id!))
                        return Reject(BatchApplyResult.RejectedInvalid, $"op[{i}] push: duplicate intent id '{op.Intent.Id}'");
                    var pushedId = string.IsNullOrWhiteSpace(op.Intent.Id) ? "(auto)" : op.Intent.Id!;
                    if (pushedId != "(auto)") existingIds.Add(pushedId);
                    depth++;
                    break;

                case IntentStackOpKind.PopTop:
                    if (depth == 0)
                        return Reject(BatchApplyResult.RejectedEmpty, $"op[{i}] pop_top: stack empty");
                    if (depth == 1)
                        return Reject(BatchApplyResult.RejectedRootPop, $"op[{i}] pop_top: cannot pop root");
                    depth--;
                    break;

                case IntentStackOpKind.ReplaceTop:
                    if (depth == 0)
                        return Reject(BatchApplyResult.RejectedEmpty, $"op[{i}] replace_top: stack empty");
                    if (op.Intent is null)
                        return Reject(BatchApplyResult.RejectedInvalid, $"op[{i}] replace_top: missing intent");
                    if (op.Intent.Completion is null)
                        return Reject(BatchApplyResult.RejectedInvalid, $"op[{i}] replace_top: missing completion predicate");
                    break;

                case IntentStackOpKind.MarkTopBlocked:
                    if (depth == 0)
                        return Reject(BatchApplyResult.RejectedEmpty, $"op[{i}] mark_top_blocked: stack empty");
                    if (string.IsNullOrWhiteSpace(op.Reason))
                        return Reject(BatchApplyResult.RejectedInvalid, $"op[{i}] mark_top_blocked: reason required");
                    break;

                default:
                    return Reject(BatchApplyResult.RejectedInvalid, $"op[{i}]: unknown op {(int)op.Op}");
            }
        }

        // All ops validated. Apply for real.
        var log = new List<string>(ops.Count);
        foreach (var op in ops)
        {
            switch (op.Op)
            {
                case IntentStackOpKind.Push:
                {
                    var intent = BuildIntent(op.Intent!, allocator, world, events, utcNow);
                    var r = stack.TryPush(intent);
                    log.Add($"push id={intent.Id} kind={intent.Kind} reason=\"{op.Reason}\" -> {r}");
                    break;
                }
                case IntentStackOpKind.PopTop:
                {
                    var topId = stack.Top?.Id ?? "?";
                    var r = stack.PopTop(IntentLifecycle.Completed, op.Reason);
                    log.Add($"pop_top id={topId} reason=\"{op.Reason}\" -> {r}");
                    break;
                }
                case IntentStackOpKind.ReplaceTop:
                {
                    var intent = BuildIntent(op.Intent!, allocator, world, events, utcNow);
                    var oldId = stack.Top?.Id ?? "?";
                    var r = stack.ReplaceTop(intent, op.Reason);
                    log.Add($"replace_top old={oldId} new={intent.Id} reason=\"{op.Reason}\" -> {r}");
                    break;
                }
                case IntentStackOpKind.MarkTopBlocked:
                {
                    var topId = stack.Top?.Id ?? "?";
                    var r = stack.MarkTopBlocked(op.Reason);
                    log.Add($"mark_top_blocked id={topId} reason=\"{op.Reason}\" -> {r}");
                    break;
                }
            }
        }
        return new BatchApplyOutcome(BatchApplyResult.Ok, null, log)
        {
            StaleRevisionTolerated = staleRevisionTolerated,
        };

        static BatchApplyOutcome Reject(BatchApplyResult result, string reason) =>
            new(result, reason, Array.Empty<string>());
    }

    private static Intent BuildIntent(
        IntentSpec spec,
        IntentIdAllocator allocator,
        WorldStateProjection world,
        EventStream events,
        DateTime utcNow)
    {
        var id = string.IsNullOrWhiteSpace(spec.Id) ? allocator.Allocate() : spec.Id!;
        var baseline = IntentBaseline.Capture(world, events, utcNow);
        DateTime? deadline = spec.DeadlineSeconds is int s && s > 0
            ? utcNow.AddSeconds(s)
            : null;
        return new Intent
        {
            Id = id,
            Kind = spec.Kind,
            TargetName = spec.TargetName,
            TargetGuid = spec.TargetGuid,
            Rationale = spec.Rationale,
            DeadlineUtc = deadline,
            Completion = spec.Completion,
            Baseline = baseline,
            Status = IntentLifecycle.Active,
            PredicateRequest = spec.PredicateRequest,
        };
    }

    /// <summary>
    /// Build the human-readable "## Intent stack" block surfaced to
    /// the LLM. Top frame is rendered in full; ancestors compactly.
    /// Always includes Revision so the LLM can echo it.
    /// </summary>
    public static string RenderStackForPrompt(IntentStack stack)
    {
        var sb = new StringBuilder(256);
        sb.Append("## Intent stack (revision=").Append(stack.Revision).Append(", depth=").Append(stack.Depth).Append('/').Append(stack.MaxDepth).AppendLine(")");
        if (stack.Depth == 0)
        {
            sb.AppendLine("- (empty - emit a `push` op to establish a root intent)");
            return sb.ToString();
        }
        // Bottom-up: index 0 is root. Render ancestors first (compact),
        // then TOP last in full.
        for (int i = 0; i < stack.Depth - 1; i++)
        {
            var f = stack.Frames[i];
            sb.Append("- ancestor[").Append(i).Append("] ").Append(f.ToString());
            // ToString() omits Rationale; surface it here so a paused parent
            // frame's own recorded plan (e.g. a follow-up step it intends to run
            // once the active child frame completes) survives into the next
            // deliberation rather than being dropped from the prompt.
            if (!string.IsNullOrEmpty(f.Rationale)) sb.Append(" rationale=\"").Append(f.Rationale).Append('"');
            sb.AppendLine();
        }
        var top = stack.Top!;
        var topActive = top.Status == IntentLifecycle.Active;
        sb.AppendLine(topActive
            ? "- TOP (act on this until its completion predicate fires):"
            : $"- TOP (status {top.Status} — this objective has reached a terminal state and is NO LONGER active; the stack has no active objective until a `stack_ops` push/replace sets one):");
        sb.Append("    id=").AppendLine(top.Id);
        sb.Append("    kind=").AppendLine(top.Kind);
        if (!string.IsNullOrEmpty(top.TargetName)) sb.Append("    target_name=\"").Append(top.TargetName).AppendLine("\"");
        if (top.TargetGuid is uint tg)             sb.Append("    target_guid=0x").AppendLine(tg.ToString("X8"));
        sb.Append("    until=").AppendLine(top.Completion.Summary());
        sb.Append("    status=").AppendLine(top.Status.ToString());
        if (top.DeadlineUtc is DateTime d)
        {
            var rem = (d - DateTime.UtcNow).TotalSeconds;
            sb.Append("    deadline_in_seconds=").AppendLine(((int)rem).ToString());
        }
        if (!string.IsNullOrEmpty(top.LastFailure)) sb.Append("    last_failure=\"").Append(top.LastFailure).AppendLine("\"");
        if (!string.IsNullOrEmpty(top.Rationale))   sb.Append("    rationale=\"").Append(top.Rationale).AppendLine("\"");

        // History tail — last 3 popped frames, helps the LLM remember
        // what it just finished or abandoned (e.g. "don't re-push the
        // intent you just popped because predicate satisfied").
        if (stack.History.Count > 0)
        {
            sb.AppendLine("- recent history (newest first):");
            foreach (var h in stack.History.Take(3))
            {
                sb.Append("    - ").Append(h.ToString());
                // ToString() omits Rationale; a just-popped frame's rationale is
                // where the LLM recorded any follow-up it planned for after that
                // frame completed, so surface it here to keep that context in the
                // deliberation immediately after the pop instead of relying on it
                // still being in the (capacity-bounded) event ring.
                if (!string.IsNullOrEmpty(h.Rationale)) sb.Append(" rationale=\"").Append(h.Rationale).Append('"');
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
