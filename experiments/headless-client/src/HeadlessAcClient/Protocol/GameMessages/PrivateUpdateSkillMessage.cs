// SPDX-License-Identifier: AGPL-3.0-or-later
// PrivateUpdateSkill (0x02DD) — server tells the receiving session that
// one of its own skills changed (a RaiseSkill 0x0046 rank-raise, or a
// full re-sync). "Private" = no guid on the wire; implicitly the bot's
// own player, like the rest of the PrivateUpdate* family.
//
// Server encoder:
//   Source/ACE.Server/Network/GameMessages/Messages/GameMessagePrivateUpdateSkill.cs
//
// Wire layout (37 bytes total, packed):
//   u32  opcode  = 0x02DD
//   u8   sequence (per-skill ByteSequence; keyed by
//                  (SequenceType.UpdateSkill, skillId) so EACH skill has
//                  an INDEPENDENT 1-byte counter — gate per skill id)
//   u32  skillId (Protocol.Skill ordinal)
//   u16  Ranks   (raised ranks; CreatureSkill.Ranks is a ushort)
//   u16  adjustPP (=1; consumed but not surfaced — see server note: a
//                  non-1 value treats InitLevel as extra applied XP)
//   u32  AdvancementClass (SkillAdvancementClass: Trained=2/Specialized=3)
//   u32  ExperienceSpent
//   u32  InitLevel
//   u32  resistance (ResistanceAtLastCheck; unused here)
//   f64  lastUsed   (LastUsedTime; unused here)
//
// We surface skillId/Ranks/AdvancementClass/ExperienceSpent/InitLevel —
// exactly the fields the login PlayerDescription skill vector carries —
// so WorldState can upsert the matching PdSkill and keep raised ranks
// live after a RaiseSkill (the login bundle is otherwise stale until
// relogin).

namespace HeadlessAcClient.Protocol.GameMessages;

internal sealed record PrivateUpdateSkillMessage(
    byte Sequence,
    uint Skill,
    ushort Ranks,
    uint AdvancementClass,
    uint ExperienceSpent,
    uint InitLevel)
{
    /// <summary>37 = u32 opcode + u8 seq + u32 id + u16 ranks + u16
    /// adjustPP + u32 sac + u32 xp + u32 init + u32 resist + f64 lastUsed.</summary>
    public const int PackedSize = 37;
}
