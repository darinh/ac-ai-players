namespace HeadlessAcClient.Strategy;

/// <summary>
/// Pure helper for the tick-path decision-starvation watchdog.
///
/// The headless client's receive loop runs goal re-deliberation AND the
/// motor-gate reset cascade ONLY inside the packet-receive try body. A
/// tick-wake (the socket receive timed out with no packet) skips that body and
/// runs only the walk-tick. So when motion has stopped in a quiet area — the
/// server sends nothing because the bot is not moving — neither re-deliberation
/// nor the reset cascade ever runs and the bot livelocks idle forever (no
/// packet -> no decision -> no movement -> no packet).
///
/// This helper decides, from mechanical timing + motor state only, whether the
/// walk-tick should:
///   - <see cref="Action.Poke"/>: re-assert the bot's current position to
///     elicit a server ack, which re-enters the packet path and re-arms the
///     reset cascade + deliberation; or
///   - <see cref="Action.Reconnect"/>: escalate to a reconnect after repeated
///     unanswered pokes, so recovery never depends on the server actually
///     replying to a no-op position.
///
/// It carries no game knowledge: inputs are timers, counters, and motor flags.
/// </summary>
public static class DecisionStarvationWatchdog
{
    public enum Action
    {
        /// <summary>No starvation detected, or not yet due — do nothing.</summary>
        None,
        /// <summary>Re-assert position to elicit an inbound packet.</summary>
        Poke,
        /// <summary>Too many unanswered pokes — request a reconnect.</summary>
        Reconnect,
    }

    /// <summary>
    /// Decide the watchdog action for the current walk-tick.
    /// </summary>
    /// <param name="motionStopped">
    /// motionDone — true when no motion is in progress. The watchdog only acts
    /// while idle; an active walk already sends position packets every tick.
    /// </param>
    /// <param name="inCombat">
    /// True when a combat target is locked. Combat keeps packets flowing and
    /// owns its own no-progress watchdog, so the starvation watchdog stays out.
    /// </param>
    /// <param name="recallQuiescing">
    /// True while a lifestone recall is in flight. Sending an autonomous
    /// position then would move the bot mid-animation and abort the teleport.
    /// </param>
    /// <param name="actionQuiesceActive">
    /// True while a just-dispatched action (USE / portal windup / give /
    /// attack) is still inside its expected post-action cooldown. The motor
    /// must stay quiescent then — a competing position packet can abort a
    /// portal/teleport or move the bot off an interaction target. Once the
    /// cooldown has clearly over-run (caller decides), this goes false so a
    /// genuinely stuck post-action wedge can still be poked free.
    /// </param>
    /// <param name="haveSelfCell">
    /// True when a self snapshot with a non-zero cell exists (required to pack
    /// a position packet at all).
    /// </param>
    /// <param name="msSinceInboundPacket">Milliseconds since the last received datagram.</param>
    /// <param name="msSinceLastPoke">Milliseconds since the last self-poke.</param>
    /// <param name="consecutivePokes">
    /// Self-pokes not yet answered by an inbound packet (the caller resets this
    /// to 0 on any receive). This is the PRE-increment count for this tick.
    /// </param>
    /// <param name="starvationMs">Idle threshold before the link is considered starved.</param>
    /// <param name="pokeIntervalMs">Minimum spacing between pokes.</param>
    /// <param name="reconnectThreshold">
    /// Unanswered-poke count at which to escalate to a reconnect.
    /// </param>
    public static Action Evaluate(
        bool motionStopped,
        bool inCombat,
        bool recallQuiescing,
        bool actionQuiesceActive,
        bool haveSelfCell,
        double msSinceInboundPacket,
        double msSinceLastPoke,
        int consecutivePokes,
        int starvationMs,
        int pokeIntervalMs,
        int reconnectThreshold)
    {
        if (!motionStopped || inCombat || recallQuiescing || actionQuiesceActive || !haveSelfCell)
            return Action.None;
        if (msSinceInboundPacket < starvationMs)
            return Action.None;
        if (msSinceLastPoke < pokeIntervalMs)
            return Action.None;
        // Starved and due. This tick would be poke number (consecutivePokes+1);
        // once that reaches the threshold, escalate to a reconnect instead.
        return (consecutivePokes + 1) >= reconnectThreshold
            ? Action.Reconnect
            : Action.Poke;
    }
}
