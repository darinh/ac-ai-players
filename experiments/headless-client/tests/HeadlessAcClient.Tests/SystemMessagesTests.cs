// SPDX-License-Identifier: AGPL-3.0-or-later
// Low-volume, high-value SYSTEM server messages (rare status lines the server
// sends — e.g. a corpse-location notice after a death) are captured into a
// durable EventStream store and surfaced VERBATIM in the prompt's protected
// tail, so the bot can act on them long after they were sent and even when
// high-volume perception/combat traffic has evicted them from the event ring.

using System;
using System.Linq;
using HeadlessAcClient.Strategy;
using HeadlessAcClient.Strategy.Intent;
using Xunit;

namespace HeadlessAcClient.Tests;

public class SystemMessagesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // Status lines (corpse location, advancement, recall, etc.) arrive on the
    // Broadcast channel (ChatMessageType 0x00) — live-confirmed: the server's
    // "Welcome to Asheron's Call ..." status banner is delivered on chatType 0x0.
    // This is NOT one of the excluded high-frequency combat/spell channels.
    private const int StatusChannelBroadcast = 0x00;
    // Per-action combat/spell feedback channels (ChatMessageType Combat=0x06,
    // Magic=0x07, Spellcasting=0x11) — the high-frequency flood the durable
    // store must exclude so it cannot evict a rare status line.
    private const int CombatChannel = 0x06;
    private const int MagicChannel = 0x07;
    private const int SpellcastingChannel = 0x11;

    private static StreamEvent ServerMsg(string text) => new()
    {
        Sequence = 0,
        Utc = T0,
        Kind = EventKind.ServerMessage,
        Text = text,
        ChatType = StatusChannelBroadcast,
    };

    private static StreamEvent CombatMsg(string text, int channel) => new()
    {
        Sequence = 0,
        Utc = T0,
        Kind = EventKind.ServerMessage,
        Text = text,
        ChatType = channel,
    };

    private static StreamEvent Noise() => new()
    {
        Sequence = 0,
        Utc = T0,
        Kind = EventKind.HealthChanged,
    };

    private static WorldStateProjection MinimalWorld() => new()
    {
        Self = new SelfProjection { Guid = 0x500u, Name = "Headless", HealthFraction = 1.0f },
        Inventory = Array.Empty<InventoryItemProjection>(),
        Visible = Array.Empty<VisibleObjectProjection>(),
    };

    [Fact]
    public void ServerMessages_SurviveRingEvictionByPerceptionTraffic()
    {
        var es = new EventStream();
        es.Append(ServerMsg("Your corpse is located at (43.2N, 36.4E)."));
        for (int i = 0; i < 400; i++) es.Append(Noise());

        // The message is GONE from the perception-dominated ring...
        Assert.DoesNotContain(es.Recent(EventStream.DefaultCapacity),
            e => e.Kind == EventKind.ServerMessage);
        // ...but survives in the dedicated durable store.
        Assert.Contains(es.RecentServerMessages(), e => e.Text!.Contains("corpse is located"));
    }

    [Fact]
    public void ServerMessages_AreDedupedAndCapped()
    {
        var es = new EventStream();
        es.Append(ServerMsg("Your experience has reduced your Vitae penalty!"));
        es.Append(ServerMsg("Your experience has reduced your Vitae penalty!"));
        Assert.Single(es.RecentServerMessages(), e => e.Text!.Contains("Vitae"));

        for (int i = 0; i < 20; i++) es.Append(ServerMsg($"distinct message {i}"));
        Assert.True(es.RecentServerMessages().Count <= 6,
            $"durable server-message store must be bounded, got {es.RecentServerMessages().Count}");
    }

    [Fact]
    public void ServerMessages_HighVolumeCombatChannel_DoesNotEvictStatusLine()
    {
        var es = new EventStream();
        // A rare, high-value status line on the Broadcast channel (0x00).
        es.Append(ServerMsg("Your corpse is located at (43.2N, 36.4E)."));
        // A burst of DISTINCT per-action messages on the real high-frequency
        // combat/magic/spellcasting channels (ChatMessageType 0x06/0x07/0x11) —
        // the kind of feedback that floods every combat tick and would blow past
        // the cap-6 store if it were captured.
        int[] floodChannels = { CombatChannel, MagicChannel, SpellcastingChannel };
        for (int i = 0; i < 18; i++)
            es.Append(CombatMsg($"You hit the target for {i} points of damage.",
                floodChannels[i % floodChannels.Length]));

        // The combat burst must NOT have evicted the status line...
        Assert.Contains(es.RecentServerMessages(), e => e.Text!.Contains("corpse is located"));
        // ...and none of the high-frequency combat/spell channels are retained.
        Assert.DoesNotContain(es.RecentServerMessages(),
            e => e.ChatType == CombatChannel || e.ChatType == MagicChannel
                 || e.ChatType == SpellcastingChannel);
    }

    [Fact]
    public void SystemMessages_RenderVerbatimInPrompt()
    {
        var es = new EventStream();
        es.Append(ServerMsg("Your corpse is located at (43.2N, 36.4E)."));
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            MinimalWorld(), es, currentGoal: null, stack: new IntentStack());
        // Match the section HEADER (rule text also references `## System messages`
        // in backticks as a source to read, so a bare substring is ambiguous).
        Assert.Contains("## System messages (recent", prompt);
        Assert.Contains("Your corpse is located at (43.2N, 36.4E).", prompt);
    }

    [Fact]
    public void SystemMessages_SectionAbsent_WhenNoServerMessages()
    {
        var prompt = LlmGoalPolicy.BuildUserPrompt(
            MinimalWorld(), new EventStream(), currentGoal: null, stack: new IntentStack());
        // The SECTION header must be absent; rule text may still cite the section
        // name in backticks, so assert on the header form only.
        Assert.DoesNotContain("## System messages (recent", prompt);
    }

    [Fact]
    public void DurableSystemMessage_StillTriggersDirectiveCompilerRule_AfterRingEviction()
    {
        var es = new EventStream();
        // An actionable server line arrives, then perception traffic floods the
        // 256-event ring and evicts it from Recent() — but it persists in the
        // durable `## System messages` store.
        es.Append(ServerMsg("Your corpse is located at (43.2N, 36.4E)."));
        for (int i = 0; i < EventStream.DefaultCapacity + 50; i++) es.Append(Noise());

        // Precondition: the line is gone from the perception ring...
        Assert.DoesNotContain(es.Recent(EventStream.DefaultCapacity),
            e => e.Kind == EventKind.ServerMessage);
        // ...but survives in the durable store.
        Assert.Contains(es.RecentServerMessages(), e => e.Text!.Contains("corpse is located"));

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            MinimalWorld(), es, currentGoal: null, stack: new IntentStack());
        // The directive-compiler gate is broadened to the durable store, so the
        // compiler rule still renders for an actionable line the ring dropped —
        // without this, an evicted corpse-location / leave-instruction would be
        // shown but the rule telling the LLM it can act on it would be gone.
        Assert.Contains("QUEST-DIALOG COMPILER", prompt);
    }

    [Fact]
    public void DurableSystemMessage_StaleNonActionableLine_DoesNotPinCompilerRuleOn()
    {
        var es = new EventStream();
        // A non-actionable banner arrives at session start (Broadcast 0x00)...
        es.Append(new StreamEvent
        {
            Sequence = 0,
            Utc = T0,
            Kind = EventKind.ServerMessage,
            Text = "Welcome to the world.",
            ChatType = StatusChannelBroadcast,
        });
        // ...then much-later perception traffic floods the ring, so the banner is
        // far older than the actionable-recency window, with NO recent server text.
        var muchLater = T0.AddMinutes(30);
        for (int i = 0; i < EventStream.DefaultCapacity + 50; i++)
            es.Append(new StreamEvent { Sequence = 0, Utc = muchLater, Kind = EventKind.HealthChanged });

        var prompt = LlmGoalPolicy.BuildUserPrompt(
            MinimalWorld(), es, currentGoal: null, stack: new IntentStack());

        // The banner still SHOWS (the durable store has no TTL)...
        Assert.Contains("## System messages (recent", prompt);
        // ...but it is too stale to keep the heavy QUEST-DIALOG COMPILER rule
        // pinned on for the whole session (the prompt sits at its hard ceiling,
        // so a permanently-on rule would crowd out trimmable world state).
        Assert.DoesNotContain("QUEST-DIALOG COMPILER", prompt);
        // The stale line is not HIDDEN: it is still shown above, and the always-on
        // SERVER-INSTRUCTION PRECEDENCE rule (covering leave/advance/proceed
        // directives) still renders and cites `## System messages`. Only the heavy
        // compiler nudge is recency-bounded.
        Assert.Contains("SERVER-INSTRUCTION PRECEDENCE", prompt);
    }
}
