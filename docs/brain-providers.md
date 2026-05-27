# Brain providers

The Social layer (chat) is the only place we use a model. Everything else is
classical AI. We want to swap *which* model trivially — local during dev,
hosted for quality runs, scripted for cheapness.

## Interface

```csharp
public interface IBrainProvider
{
    Task<string> GenerateChatAsync(
        BotContext bot,
        ChatRequest request,
        CancellationToken ct);

    // Optional: for archetypes that make tactical-ish decisions via LLM.
    // Most archetypes won't use this.
    Task<TacticalDecision> DecideAsync(
        BotContext bot,
        TacticalPrompt prompt,
        CancellationToken ct);

    BrainProviderCapabilities Capabilities { get; }
}
```

`BotContext` carries: archetype persona, short-term memory, recent chat log
window, current goal, location/zone. Kept small to control prompt cost.

## Providers

### LocalOllamaProvider
- **Use for:** Development, self-hosted production.
- **Backend:** Ollama running locally or on a GPU box on LAN.
- **Models to try first:** `llama3.1:8b-instruct`, `qwen2.5:7b-instruct`,
  `mistral:7b-instruct`.
- **Pros:** Zero per-token cost, fully private, no rate limits.
- **Cons:** Quality ceiling; needs a GPU for decent throughput at scale.

### OpenAIProvider (and OpenAI-compatible)
- **Use for:** Higher-quality social interactions, demos.
- **Backend:** OpenAI API or any OpenAI-compatible endpoint (Groq, Together,
  vLLM, LM Studio, etc).
- **Pros:** Best quality, fast, no infra.
- **Cons:** Per-token cost, rate limits, external dependency.

### ScriptedProvider
- **Use for:** Archetypes that don't need real generation (Buffbot, Trade
  Spammer, Grinder), or as a fallback when the real provider is down.
- **Backend:** Template strings + Markov-ish line shuffling from a corpus
  of canned lines per archetype.
- **Pros:** Free, instant, deterministic, never breaks character.
- **Cons:** Repetitive if used for chatty archetypes.

### NullProvider
- **Use for:** Tests, headless integration runs.
- Returns empty / silence. Bots still exist, they just don't talk.

## Selection policy

`BrainRouter` chooses a provider per request based on:

```
1. Archetype config:
     if archetype.brain_preference == "scripted":
         use ScriptedProvider
2. Budget pressure:
     if global token budget for the hour is exceeded:
         downgrade hosted → local
         downgrade local → scripted
3. Latency target:
     for /tell responses (player is waiting): prefer fastest available
     for unprompted /local chatter: any
4. Fallback chain:
     hosted → local → scripted → null
```

This means a bot can *start* the day on the OpenAI provider and finish on
the scripted provider once the budget burns out, without changing behavior
visibly enough to break immersion (because most archetypes have low chat
propensity anyway).

## Caching and dedup

- **Greeting cache.** Per archetype, cache N pre-generated greetings at
  startup. When a bot needs to say hi, sample from cache instead of calling
  the model. Refresh occasionally.
- **Question→answer cache.** If two players ask the same question to the
  same archetype within a window, reuse the answer.
- **Per-bot LRU.** Last 8 utterances per bot to avoid immediate repeats.

## Cost controls

Rules of thumb to keep this affordable on a hosted provider:

- Hard cap per bot per hour (e.g., 20 generations).
- Hard cap server-wide per hour.
- Truncate context: rolling window of last 6 chat events, not full history.
- Use small models for low-stakes chatter; reserve bigger models for /tell
  responses where the player is directly engaged.

## Prompt structure (sketch)

```
SYSTEM:
You are {bot.display_name}, a player in Asheron's Call (private server, 2026
emulator). Stay in character as a {archetype.display_name}.
{archetype.llm_persona_prompt}
Hard rules:
- This is in-game chat. Keep replies under 25 words.
- Never break character. Never mention being an AI.
- Never claim to do something you can't do in-game.

CONTEXT:
Location: {bot.location_name} ({bot.zone_type})
Recent events: {bot.short_term_memory_summary}
Recent chat:
{rolling_chat_window}

USER:
{trigger_event}    // e.g. "Player Foo said in /local: 'where do I get a better sword?'"

ASSISTANT:
```

## Provider config (example)

```yaml
providers:
  primary:
    type: ollama
    base_url: http://localhost:11434
    model: llama3.1:8b-instruct
  premium:
    type: openai
    base_url: https://api.openai.com/v1
    model: gpt-4o-mini
    api_key_env: OPENAI_API_KEY
  fallback:
    type: scripted

routing:
  default_chain: [primary, fallback]
  tell_chain:    [premium, primary, fallback]
  budget_per_hour_usd: 1.00
```

## Open questions

- Do we want a single shared "world LLM" that hears all bot chat and can
  inject NPC-like commentary? (Probably no — too easy to break immersion.)
- Should bots ever generate *actions* via LLM, or strictly chat? (Default:
  strictly chat for v1; revisit when tactical layer feels limiting.)
- How aggressively can we cache without bots sounding like cardboard cutouts?

See [`../roadmap/open-questions.md`](../roadmap/open-questions.md).
