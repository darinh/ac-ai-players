// SPDX-License-Identifier: AGPL-3.0-or-later
// FellowshipGoalGuard — pure validity checks for fellowship goals against the bot's
// CURRENT fellowship state, so the Motor never sends a wire action that would destroy
// the team it is forming. No game knowledge: purely mechanical invariants about the
// fellowship wire protocol.

namespace HeadlessAcClient.Strategy;

internal static class FellowshipGoalGuard
{
    /// <summary>
    /// True when a <see cref="GoalKind.FellowshipCreate"/> goal must be REJECTED because
    /// the bot is already in a fellowship. The server disbands the bot's current
    /// fellowship in order to create a new one, so re-issuing create collapses a team the
    /// bot is still forming (observed live: a rotated/weaker model re-emitted
    /// FellowshipCreate and dropped a multi-member fellowship back to one member). The
    /// valid in-fellowship actions are <see cref="GoalKind.FellowshipRecruit"/> (add a
    /// teammate) and <see cref="GoalKind.FellowshipQuit"/> (leave). Returns false for any
    /// other goal kind, and for FellowshipCreate when NOT in a fellowship (the normal
    /// create path).
    /// </summary>
    internal static bool IsRedundantFellowshipCreate(GoalKind kind, bool inFellowship)
        => kind == GoalKind.FellowshipCreate && inFellowship;

    /// <summary>
    /// True when a <see cref="GoalKind.FellowshipRecruit"/> must be preceded by a
    /// FellowshipCreate: the recruit wire action (0x00A5) only works once the bot already
    /// LEADS a fellowship, but a model may emit Recruit directly (skipping the Create the
    /// prompt names), which would hit a non-existent fellowship and never form the group.
    /// So when auto-team is enabled AND the bot is NOT yet in a fellowship, the Motor sends
    /// the FellowshipCreate the recruit presupposes first — decomposing the LLM's explicit
    /// "recruit &lt;player&gt;" intent into its two required wire steps. Gated on the
    /// operator's auto-team opt-in (like the allegiance auto-approve): the bot forms no
    /// group unprompted, it only fulfils an already-issued recruit. False when already in a
    /// fellowship (recruit sends alone) or auto-team is off (unchanged behavior).
    /// </summary>
    internal static bool ShouldCreateBeforeRecruit(bool autoTeamEnabled, bool inFellowship)
        => autoTeamEnabled && !inFellowship;
}
