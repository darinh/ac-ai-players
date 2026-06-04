// SPDX-License-Identifier: AGPL-3.0-or-later
// CharacterNameFallback — deterministic alternate-name generator for
// the login handshake.
//
// THE BUG THIS FIXES (smoke-charlist-quirk, smoke-run-03/04):
//   When the server's CharacterList reports zero characters but the
//   account already owns a character with the desired name, the
//   CharacterCreate dispatched in Phase 3.2 comes back NameInUse (3).
//   The old flow had no recovery: createResponse was non-Ok and the
//   list was empty, so chosenCharacterGuid stayed 0 and the bot wedged
//   without ever entering the world.
//
//   The recovery is to pick a fresh, deterministic alternate name and
//   re-issue CharacterCreate (HandshakeDriver resets characterCreateSent
//   on a retryable response). This helper owns the name math so it can
//   be unit-tested without the giant async receive loop.
//
// Schema-only, no game knowledge: this is pure string bookkeeping for
// the login transport, not a gameplay decision.

using System;
using System.Text;

namespace HeadlessAcClient.Protocol;

internal static class CharacterNameFallback
{
    /// <summary>Maximum character-name length we will emit. ACE accepts
    /// longer, but keeping it short leaves headroom under the 448-byte
    /// single-fragment CharacterCreate cap and avoids server name-rule
    /// edge cases.</summary>
    public const int MaxNameLength = 30;

    private const string DefaultBase = "Headless";

    /// <summary>
    /// True when a CharacterCreate failure is plausibly resolved by
    /// retrying with a different name. NameInUse and NameBanned are
    /// name-specific; every other failure (Corrupt, DatabaseDown,
    /// AdminPrivilegeDenied, Pending, Undef) is transient or fatal and
    /// a rename would not help.
    /// </summary>
    public static bool IsNameRetryable(GameMessages.CharacterCreateResponse response)
        => response == GameMessages.CharacterCreateResponse.NameInUse
        || response == GameMessages.CharacterCreateResponse.NameBanned;

    /// <summary>
    /// Produce a deterministic alternate name derived from
    /// <paramref name="baseName"/> for the given 1-based
    /// <paramref name="attempt"/>. The result is letters-only,
    /// non-empty, and at most <see cref="MaxNameLength"/> characters.
    /// Distinct attempts yield distinct names. attempt 1 → base+"a",
    /// 2 → base+"b", ..., 26 → base+"z", 27 → base+"aa", etc.
    /// </summary>
    /// <param name="baseName">The originally requested name. Non-letter
    /// characters are stripped; if nothing remains, a built-in default
    /// is used.</param>
    /// <param name="attempt">1-based retry counter. Values &lt; 1 are
    /// treated as 1.</param>
    public static string NextName(string? baseName, int attempt)
    {
        if (attempt < 1) attempt = 1;

        var root = LettersOnly(baseName);
        if (root.Length == 0) root = DefaultBase;

        var suffix = LetterSuffix(attempt);

        // Reserve room for the suffix; trim the root if the combined
        // length would exceed the cap.
        var maxRoot = Math.Max(1, MaxNameLength - suffix.Length);
        if (root.Length > maxRoot) root = root.Substring(0, maxRoot);

        return root + suffix;
    }

    private static string LettersOnly(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Base-26 lowercase-letter encoding of a 1-based index, in the
    /// spreadsheet-column style: 1→a, 26→z, 27→aa, 52→az, 53→ba.
    /// </summary>
    private static string LetterSuffix(int attempt)
    {
        var sb = new StringBuilder(4);
        var n = attempt;
        while (n > 0)
        {
            n--;                       // shift to 0-based for the modulo
            sb.Insert(0, (char)('a' + (n % 26)));
            n /= 26;
        }
        return sb.ToString();
    }
}
