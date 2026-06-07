namespace HeadlessAcClient.World;

/// <summary>
/// Pure decision for classifying a just-completed Use/Pickup action
/// cycle as out-of-reach (FAILED) rather than a completed visit.
///
/// The Motor's walk-tick arrival check is XY-only, so the bot can
/// "arrive" (distXY ~1u) at a target it is still far from in 3D —
/// e.g. a chest ~20u directly below a ledge. When that happens the
/// dispatched Use/Pickup does not land and the server answers with a
/// MoveToObject for the bot's OWN guid ("you are not in range; I am
/// walking you to it"). That server reply is a reliable, protocol-
/// grounded out-of-range signal (a successful in-range Use instead
/// draws a TurnToObject). Treating the cycle as FAILED lets the LLM
/// (via ## Recent goal outcomes) stop re-picking an unreachable
/// target instead of looping on it forever.
///
/// Encodes no game knowledge and no Z-magnitude threshold: it keys
/// only on whether the server told US to approach OUR interaction
/// target AFTER we dispatched the interaction.
/// </summary>
public static class InteractReachClassifier
{
    /// <summary>
    /// True when a dispatched Use/Pickup on <paramref name="targetGuid"/>
    /// should be classified as out-of-reach. Requires that an
    /// interaction was actually dispatched this cycle
    /// (<paramref name="worldInteractDispatched"/>), that the server
    /// issued a MoveToObject for our own object toward this same
    /// target, and that the reply arrived AT OR AFTER the dispatch (so
    /// an approach-phase MoveToObject can never mis-trigger it).
    /// </summary>
    public static bool IsOutOfReach(
        bool worldInteractDispatched,
        uint? targetGuid,
        uint? lastSelfMoveToObjectGuid,
        System.DateTime? useSentAt,
        System.DateTime? lastSelfMoveToObjectAt)
    {
        if (!worldInteractDispatched) return false;
        if (targetGuid is not uint tg) return false;
        if (lastSelfMoveToObjectGuid != tg) return false;
        if (useSentAt is not System.DateTime sent) return false;
        if (lastSelfMoveToObjectAt is not System.DateTime moved) return false;
        return moved >= sent;
    }
}
