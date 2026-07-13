// SPDX-License-Identifier: AGPL-3.0-or-later
// AutoEquipRetryPolicy — retry timing for the autonomous login/inventory
// auto-equip pass (HandshakeDriver PHASE7F.4).
//
// Why this exists: a GetAndWieldItem for an item that has just been created at
// login can RACE the item-creation ack. The server answers with a silent
// InventoryServerSaveFailed (err=None) and never sends a WieldObject ack, so the
// item's WielderGuid stays null (unwielded). The login pass tracks "already
// sent" guids one-shot to avoid spamming duplicate equip requests while awaiting
// the ack — but that one-shot dedup then NEVER re-sends a raced equip, leaving
// the item stuck unwielded for the whole session (the bot logs in under-armored
// / unarmed). This policy re-eligibles a still-unwielded guid for one more
// GetAndWieldItem after a cooldown (so the retry lands once the creation ack has
// settled), capped at a max attempt count so a genuinely un-equippable item does
// not loop. Mirrors the server-side auto-wield retry cooldown pattern. Pure
// timer/attempt bookkeeping; no game knowledge.

using System;

namespace HeadlessAcClient.Strategy;

internal static class AutoEquipRetryPolicy
{
    // Seconds a raced equip must sit unwielded before it is re-sent — long
    // enough for the item-creation ack to settle so the retry is not itself
    // raced. Env AC_BOTS_AUTO_EQUIP_RETRY_COOLDOWN_SECONDS overrides
    // (clamped 1..120; default 10, matching the server auto-wield retry cooldown).
    internal static readonly TimeSpan RetryCooldown =
        TimeSpan.FromSeconds(ResolveCooldownSeconds(
            Environment.GetEnvironmentVariable("AC_BOTS_AUTO_EQUIP_RETRY_COOLDOWN_SECONDS")));

    // Total GetAndWieldItem attempts per guid before the login pass gives up
    // (a genuinely un-equippable item must not retry forever). Env
    // AC_BOTS_AUTO_EQUIP_MAX_ATTEMPTS overrides (clamped 1..20; default 4).
    // 1 disables the retry entirely (one-shot, byte-identical to the prior
    // behavior).
    internal static readonly int MaxAttempts =
        ResolveMaxAttempts(Environment.GetEnvironmentVariable("AC_BOTS_AUTO_EQUIP_MAX_ATTEMPTS"));

    internal static double ResolveCooldownSeconds(string? envValue)
    {
        const double Default = 10.0;
        const double Min = 1.0;
        const double Max = 120.0;
        if (double.TryParse(envValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= Min)
            return Math.Min(v, Max);
        return Default;
    }

    internal static int ResolveMaxAttempts(string? envValue)
    {
        const int Default = 4;
        const int Min = 1; // 1 = one-shot (retry disabled)
        const int Max = 20;
        if (int.TryParse(envValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= Min)
            return Math.Min(v, Max);
        return Default;
    }

    /// <summary>
    /// True iff a still-unwielded, already-equip-sent guid should be RELEASED for
    /// another GetAndWieldItem now: the prior send is at least
    /// <paramref name="cooldown"/> old (its WieldObject ack was likely lost to the
    /// item-creation race) AND the attempt cap is not yet reached. The caller only
    /// invokes this for an item it can still see is unwielded, so returning true
    /// means "the ack never came — try again"; false means "still awaiting the ack
    /// (within cooldown) or gave up (attempts exhausted)". Pure predicate over the
    /// send timestamp + attempt count; no side effects, no game knowledge.
    /// </summary>
    internal static bool ShouldRetry(
        DateTime sentAt, int attempts, DateTime now, TimeSpan cooldown, int maxAttempts)
        => attempts < maxAttempts && (now - sentAt) >= cooldown;
}
