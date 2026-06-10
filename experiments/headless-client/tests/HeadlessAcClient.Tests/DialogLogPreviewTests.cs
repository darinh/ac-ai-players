// SPDX-License-Identifier: AGPL-3.0-or-later
// Unit tests for HandshakeDriver.DialogLogPreview — the diagnostics-only
// formatter that renders server/NPC text into the [observe] deploy log.
//
// Why this exists: criterion-2 (obtain + complete a task an NPC assigns)
// was undiagnosable because the dialog carriers (NpcDialog/PopupString)
// were never logged, and the ServerMessage/HearSpeech log truncated at
// 80 chars — hiding the actionable words a quest compiler must read. The
// preview lifts the cap to DialogLogPreviewMaxChars and normalizes line
// breaks to a single literal "\n" so a multi-line emote stays one
// greppable line. It is a pure formatter (logging only; never read by
// decision-making), so it carries no game knowledge and is safe to
// unit-test directly. Fixtures are deliberately game-agnostic: the
// formatter does not inspect content, only length and line breaks.

using HeadlessAcClient.Protocol;
using Xunit;

namespace HeadlessAcClient.Tests;

public class DialogLogPreviewTests
{
    [Fact]
    public void NullOrEmpty_ReturnsEmptyString()
    {
        Assert.Equal("", HandshakeDriver.DialogLogPreview(null));
        Assert.Equal("", HandshakeDriver.DialogLogPreview(""));
    }

    [Fact]
    public void ShortText_ReturnedVerbatim()
    {
        const string s = "alpha bravo charlie";
        Assert.Equal(s, HandshakeDriver.DialogLogPreview(s));
    }

    [Fact]
    public void Newlines_CollapsedToLiteralBackslashN_OnOneLine()
    {
        // A multi-line message must stay a single greppable log line. CRLF
        // and LF each collapse to exactly ONE literal "\n" boundary.
        var multiline = "line-one\r\nline-two\nline-three";
        var preview = HandshakeDriver.DialogLogPreview(multiline);
        Assert.DoesNotContain("\n", preview);
        Assert.DoesNotContain("\r", preview);
        Assert.Equal("line-one\\nline-two\\nline-three", preview);
    }

    [Fact]
    public void CarriageReturnOnly_CollapsedToLiteralBackslashN_BoundaryKept()
    {
        // A lone "\r" is a line break too — it must become a "\n" boundary,
        // not vanish (which would merge two lines into one token).
        var preview = HandshakeDriver.DialogLogPreview("head\rtail");
        Assert.Equal("head\\ntail", preview);
    }

    [Fact]
    public void LongText_BelowCap_SurvivesInFull_IncludingTailPastOldCap()
    {
        // The whole point of the slice: a message whose tail sits well past
        // the old 80-char cap must now be visible in full. Build a synthetic
        // string that is over 80 chars and under DialogLogPreviewMaxChars,
        // with a recognizable tail token after the old cap boundary.
        var body = new string('x', 200);
        var text = "HEAD " + body + " TAIL_TOKEN";
        Assert.True(text.Length > 80 && text.Length <= HandshakeDriver.DialogLogPreviewMaxChars);
        var preview = HandshakeDriver.DialogLogPreview(text);
        Assert.Equal(text, preview);
        // The tail (previously cut at 80) survives.
        Assert.Contains("TAIL_TOKEN", preview);
    }

    [Fact]
    public void TextAtCap_NotTruncated()
    {
        var s = new string('a', HandshakeDriver.DialogLogPreviewMaxChars);
        var preview = HandshakeDriver.DialogLogPreview(s);
        Assert.Equal(s, preview);
        Assert.DoesNotContain("...", preview);
    }

    [Fact]
    public void TextOverCap_TruncatedWithEllipsis()
    {
        var s = new string('b', HandshakeDriver.DialogLogPreviewMaxChars + 50);
        var preview = HandshakeDriver.DialogLogPreview(s);
        Assert.Equal(new string('b', HandshakeDriver.DialogLogPreviewMaxChars) + "...", preview);
        Assert.Equal(HandshakeDriver.DialogLogPreviewMaxChars + 3, preview.Length);
    }

    [Fact]
    public void CapMeasuredAfterNewlineCollapse_StableOneLineLength()
    {
        // Newlines become two chars ("\\n"), so the cap is applied to the
        // collapsed one-line form — deterministic regardless of how many
        // line breaks the message had.
        var s = new string('\n', HandshakeDriver.DialogLogPreviewMaxChars);
        var preview = HandshakeDriver.DialogLogPreview(s);
        Assert.EndsWith("...", preview);
        Assert.Equal(HandshakeDriver.DialogLogPreviewMaxChars + 3, preview.Length);
    }
}
