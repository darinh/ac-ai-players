// SPDX-License-Identifier: AGPL-3.0-or-later
// CharacterCreateMessage — pack a GameMessageCharacterCreate
// (opcode 0xF656) for transmission. The server's reader is at:
//   Source/ACE.Server/Network/Handlers/CharacterHandler.cs:26-44
//   Source/ACE.Entity/CharacterCreateInfo.cs:37-68
//   Source/ACE.Entity/Appearance.cs:28-50
//
// Wire layout (after the 4-byte opcode):
//   string16L  account              // MUST equal session.Account
//                                   // CharacterHandler.cs:29-32
//   --- begin CharacterCreateInfo (CharacterCreateInfo.Unpack) ---
//   u32        unknown_constant     // server skips with Position += 4
//                                   // CharacterCreateInfo.cs:39
//                                   // We write 1u per the source comment.
//   u32        Heritage             // HeritageGroup enum
//   u32        Gender               // 0=Female, 1=Male
//   --- begin Appearance.Unpack (104 bytes) ---
//   u32        Eyes
//   u32        Nose
//   u32        Mouth
//   u32        HairColor
//   u32        EyeColor
//   u32        HairStyle
//   u32        HeadgearStyle
//   u32        HeadgearColor
//   u32        ShirtStyle
//   u32        ShirtColor
//   u32        PantsStyle
//   u32        PantsColor
//   u32        FootwearStyle
//   u32        FootwearColor
//   f64        SkinHue
//   f64        HairHue
//   f64        HeadgearHue
//   f64        ShirtHue
//   f64        PantsHue
//   f64        FootwearHue
//   --- end Appearance ---
//   i32        TemplateOption
//   u32        StrengthAbility
//   u32        EnduranceAbility
//   u32        CoordinationAbility
//   u32        QuicknessAbility
//   u32        FocusAbility
//   u32        SelfAbility
//   u32        CharacterSlot
//   u32        ClassId
//   u32        numOfSkills          // MUST be 55 - PlayerFactory:166
//   u32 x 55   SkillAdvancementClass
//   string16L  Name
//   u32        StartArea            // index into CharGen.StarterAreas[]
//   u32        IsAdmin              // 0 or 1
//   u32        IsSentinel           // 0 or 1
//
// Validation gates the spike must satisfy (PlayerFactory.cs:33-417):
//   * Heritage in CharGen.HeritageGroups[]                       (line 35)
//   * Gender in heritageGroup.Genders[]                          (line 52)
//   * TemplateOption in heritageGroup.Templates[]                (line 135)
//   * Each attribute in [10, 100]; sum <= heritage.AttributeCredits (line 606-631)
//   * SkillAdvancementClasses.Count == 55                        (line 166)
//   * Each non-Inactive skill index present in DAT SkillBaseHash (line 176)
//   * Name not in taboo table (when enabled)                     (line 51)
//   * Name not already in use                                    (line 63, 147)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace HeadlessAcClient.Protocol.GameMessages;

internal static class CharacterCreateMessage
{
    // Per PlayerFactory.cs:166 — server treats any other count as
    // ClientServerSkillsMismatch and TERMINATES the session.
    public const int RequiredSkillCount = 55;

    // From CharacterHandler.cs:39 - the unknown constant is described
    // as "Unknown constant (1)" in the server source. Server doesn't
    // read it; writing 1 mirrors what the live client appears to send.
    private const uint UnknownConstant = 1u;

    // Heritage 1 = Aluvian. Per HeritageGroup.cs:6.
    public const uint HeritageAluvian = 1;
    // Gender 1 = Male. Per PlayerFactory.cs:47.
    public const uint GenderMale      = 1;
    public const uint GenderFemale    = 0;

    // SkillAdvancementClass enum values (Source/ACE.Entity/Enum/SkillAdvancementClass.cs)
    public const uint SACInactive    = 0;
    public const uint SACUntrained   = 1;
    public const uint SACTrained     = 2;
    public const uint SACSpecialized = 3;

    // Skill IDs that we typically train on a fresh bot character so
    // the server's starter-gear loop (PlayerFactory.cs:225) hands the
    // bot items the academy expects. Sourced from starterGear.json:
    //
    //   21 = Healing            -> Handy Healing Kit
    //   22 = Jump               -> Pyreal x10000, Sack, Calling Stone,
    //                              Pathwarden Token, Bread, Ust, and
    //                              the heritage Letter From Home
    //   41 = Two Handed Combat  -> Training Spadone (wcid 41512) - a
    //                              real weapon, so the bot is not
    //                              bare-handed in the academy
    //
    // Total skill-credit cost is well within any heritage's 52-credit
    // budget (Olthoi=68). Verified live in phase7f4-* test runs.
    //
    // Mirrors ACE-bots Source/ACE.Server/Bots/BotPlayerFactory.cs
    // DefaultTrainedSkills (which has 21,22,24) plus skill 41 added
    // for the starter weapon since 24 (Run) grants no gear in the
    // current starterGear.json.
    public static readonly IReadOnlyList<uint> DefaultTrainedSkillIds =
        new uint[] { 21, 22, 41 };

    public sealed record Options(
        string Account,
        string Name,
        uint   Heritage        = HeritageAluvian,
        uint   Gender          = GenderMale,
        int    TemplateOption  = 0,
        // Spend the attribute-credit budget instead of the all-minimum
        // [10,10,10,10,10,10]. Server gate
        // (PlayerFactory.ValidateAttributeCredits): each attribute in [10,100]
        // and their sum <= the selected heritage's AttributeCredits. 6 x 45 =
        // 270 stays under the smallest heritage budget floor (>= 290) with
        // margin and is within the per-attribute range. Even across all six
        // attributes so none is favored by this default.
        uint   StrengthAbility     = 45,
        uint   EnduranceAbility    = 45,
        uint   CoordinationAbility = 45,
        uint   QuicknessAbility    = 45,
        uint   FocusAbility        = 45,
        uint   SelfAbility         = 45,
        uint   CharacterSlot       = 0,
        uint   ClassId             = 0,
        // Per-slot SkillAdvancementClass override. If non-null, every
        // skill index in the collection is flipped from Inactive to
        // SACTrained when packed. All other slots stay Inactive.
        // Replaces an earlier (buggy) SkillsOverride: uint? field that
        // applied a single SAC value to ALL 55 slots indiscriminately
        // - that would either grant nothing or fail PlayerFactory's
        // SkillTable.SkillBaseHash lookup at startup. Indices outside
        // [0, RequiredSkillCount) are silently dropped (defensive -
        // the enum's stable, but a caller-side typo shouldn't
        // terminate the session via ClientServerSkillsMismatch).
        IReadOnlyCollection<uint>? TrainedSkillIds = null,
        uint   StartArea           = 0,    // index into StarterAreas[]
        bool   IsAdmin             = false,
        bool   IsSentinel          = false
    );

    /// <summary>
    /// Pack a CharacterCreate fragment payload (including the 4-byte
    /// opcode prefix) into <paramref name="dest"/>. Returns the
    /// number of bytes written.
    /// </summary>
    public static int Pack(Span<byte> dest, Options opt)
    {
        if (opt.Account is null) throw new ArgumentException("Account required", nameof(opt));
        if (opt.Name is null || opt.Name.Length == 0)
            throw new ArgumentException("Name required", nameof(opt));

        // Per-attribute range gate (server ValidateAttributeCredits:622-623).
        ValidateAttribute(opt.StrengthAbility,     nameof(opt.StrengthAbility));
        ValidateAttribute(opt.EnduranceAbility,    nameof(opt.EnduranceAbility));
        ValidateAttribute(opt.CoordinationAbility, nameof(opt.CoordinationAbility));
        ValidateAttribute(opt.QuicknessAbility,    nameof(opt.QuicknessAbility));
        ValidateAttribute(opt.FocusAbility,        nameof(opt.FocusAbility));
        ValidateAttribute(opt.SelfAbility,         nameof(opt.SelfAbility));

        var pos = 0;

        // Opcode (4 bytes) - GameMessage.cs:25-27
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), (uint)GameMessageOpcode.CharacterCreate);
        pos += 4;

        // Account string16L - CharacterHandler.cs:29
        pos += AcStrings.WriteString16L(dest.Slice(pos), opt.Account);

        // CharacterCreateInfo.Unpack starts here.

        // Unknown constant (server skips, we write 1)
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), UnknownConstant); pos += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.Heritage);   pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.Gender);     pos += 4;

        // Appearance.Unpack - 104 bytes (14 u32 + 6 f64)
        // All indices zero is the conservative "default newbie" choice
        // per the rubber-duck plan. SkinHue/HairHue/etc. zero = first
        // palette entry on the chosen heritage/sex.
        for (var i = 0; i < 14; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), 0u); pos += 4;
        }
        for (var i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(dest.Slice(pos), 0.0); pos += 8;
        }

        // TemplateOption is i32 - CharacterCreateInfo.cs:46
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(pos), opt.TemplateOption); pos += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.StrengthAbility);     pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.EnduranceAbility);    pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.CoordinationAbility); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.QuicknessAbility);    pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.FocusAbility);        pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.SelfAbility);         pos += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.CharacterSlot); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.ClassId);       pos += 4;

        // Skill count + 55 entries. Server invariant:
        // SkillAdvancementClasses.Count != 55 -> session termination.
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), (uint)RequiredSkillCount); pos += 4;

        // Build the SAC vector: start all Inactive, then flip each
        // index in TrainedSkillIds to Trained. Out-of-range indices
        // are dropped (see Options.TrainedSkillIds doc-comment).
        Span<uint> sac = stackalloc uint[RequiredSkillCount];
        for (var i = 0; i < RequiredSkillCount; i++) sac[i] = SACInactive;
        if (opt.TrainedSkillIds is not null)
        {
            foreach (var id in opt.TrainedSkillIds)
            {
                if (id < (uint)RequiredSkillCount)
                    sac[(int)id] = SACTrained;
            }
        }
        for (var i = 0; i < RequiredSkillCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), sac[i]); pos += 4;
        }

        pos += AcStrings.WriteString16L(dest.Slice(pos), opt.Name);

        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.StartArea);          pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.IsAdmin    ? 1u : 0u); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(pos), opt.IsSentinel ? 1u : 0u); pos += 4;

        return pos;
    }

    /// <summary>
    /// Compute the exact serialized size for the given Options
    /// without actually packing. Useful for pre-flight sizing
    /// (single-fragment check, buffer allocation).
    /// </summary>
    public static int MeasurePackedSize(Options opt)
    {
        var size = 4;                                          // opcode
        size += AcStrings.MeasureString16L(opt.Account);       // account
        size += 4;                                             // unknown constant
        size += 4;                                             // Heritage
        size += 4;                                             // Gender
        size += 14 * 4 + 6 * 8;                                // Appearance
        size += 4;                                             // TemplateOption
        size += 6 * 4;                                         // 6 abilities
        size += 4 + 4;                                         // CharacterSlot, ClassId
        size += 4 + RequiredSkillCount * 4;                    // skill count + 55 entries
        size += AcStrings.MeasureString16L(opt.Name);          // name
        size += 4 + 4 + 4;                                     // StartArea, IsAdmin, IsSentinel
        return size;
    }

    private static void ValidateAttribute(uint value, string name)
    {
        // PlayerFactory.cs:622 - "if (attributeValue < 10 || attributeValue > 100)"
        if (value < 10 || value > 100)
            throw new ArgumentException(
                $"{name}={value} out of range [10,100]; server would reject as InvalidSkillRequested",
                nameof(value));
    }
}
