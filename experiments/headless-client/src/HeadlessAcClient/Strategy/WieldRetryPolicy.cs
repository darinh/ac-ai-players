// SPDX-License-Identifier: AGPL-3.0-or-later

namespace HeadlessAcClient.Strategy;

using System;

/// <summary>
/// Retry timing for an LLM-authored Wield transaction. The policy only
/// operates on a guid and slot already selected by Strategy.
/// </summary>
internal static class WieldRetryPolicy
{
    internal const int ConclusiveExplicitRejectionCount = 2;

    internal static readonly TimeSpan RetryCooldown =
        TimeSpan.FromSeconds(ResolveCooldownSeconds(
            Environment.GetEnvironmentVariable("AC_BOTS_WIELD_RETRY_COOLDOWN_SECONDS")));

    internal static readonly int MaxAttempts =
        ResolveMaxAttempts(Environment.GetEnvironmentVariable("AC_BOTS_WIELD_MAX_ATTEMPTS"));

    internal static double ResolveCooldownSeconds(string? envValue)
    {
        const double Default = 10.0;
        const double Min = 1.0;
        const double Max = 120.0;
        if (double.TryParse(
                envValue,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) &&
            value >= Min)
        {
            return Math.Min(value, Max);
        }

        return Default;
    }

    internal static int ResolveMaxAttempts(string? envValue)
    {
        const int Default = 4;
        const int Min = 1;
        const int Max = 20;
        if (int.TryParse(
                envValue,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) &&
            value >= Min)
        {
            return Math.Min(value, Max);
        }

        return Default;
    }

    internal static int RecordExplicitRejection(int previousCount, uint errorType)
        => errorType != 0
            ? ConclusiveExplicitRejectionCount
            : Math.Min(previousCount + 1, ConclusiveExplicitRejectionCount);

    internal static bool ShouldRetry(
        DateTime sentAt,
        int attempts,
        int explicitRejections,
        DateTime now,
        TimeSpan cooldown,
        int maxAttempts)
        => attempts < maxAttempts
           && explicitRejections < ConclusiveExplicitRejectionCount
           && (now - sentAt) >= cooldown;

    internal static bool TimedOut(
        DateTime sentAt,
        int attempts,
        int explicitRejections,
        DateTime now,
        TimeSpan cooldown,
        int maxAttempts)
        => attempts >= maxAttempts
           && explicitRejections < ConclusiveExplicitRejectionCount
           && (now - sentAt) >= cooldown;
}
