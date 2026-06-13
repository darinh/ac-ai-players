// SPDX-License-Identifier: AGPL-3.0-or-later
// ContractCatalog — load-once lookup of the game's STATIC contract
// definitions (client_portal.dat ContractTable, file id 0x0E00001D),
// keyed by contract id.
//
// Why this exists: a tracked contract arrives on the wire as just a numeric
// id + stage code (SendClientContractTracker 0x0315 / Table 0x0314). That
// number is meaningless to the LLM on its own — it cannot tell what an opaque
// contract id REQUIRES (what to do, where, who to turn it in to). The
// human-readable
// objective lives in the client_portal.dat ContractTable. This catalog reads
// that authoritative GAME DATA by id and projects it as raw facts the bot can
// surface, exactly like decoding wire bits into named projections or relaying
// NPC dialog. No hardcoded contract knowledge: every fact is read from the dat
// by id; the LLM decides what (if anything) to do with it.

using System;
using System.Collections.Generic;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

namespace HeadlessAcClient.Strategy;

/// <summary>
/// Raw, human-readable definition of one contract, projected from the dat
/// ContractTable. All strings are the dat's own text (may be empty).
/// </summary>
internal sealed record ContractInfo(
    uint ContractId,
    string Name,
    string Description,
    string DescriptionProgress,
    string NpcStart,
    string NpcEnd,
    float? TurnInWorldX = null,
    float? TurnInWorldY = null,
    float? QuestAreaWorldX = null,
    float? QuestAreaWorldY = null);

/// <summary>
/// Lookup of contract definitions keyed by id. Injected into
/// <see cref="WorldStateProjection.FromWorldState"/> alongside the weenie
/// repository so the projection can enrich a tracked contract's opaque id with
/// its objective text. Implementations are immutable after construction.
/// </summary>
internal interface IContractCatalog
{
    /// <summary>Number of known contract definitions.</summary>
    int Count { get; }

    /// <summary>Look up a contract definition by id. False when unknown.</summary>
    bool TryGet(uint contractId, out ContractInfo info);
}

/// <summary>
/// Immutable, id-keyed catalog of contract definitions. Built once at startup
/// from the client_portal.dat ContractTable (see <see cref="FromPortalDat"/>),
/// or directly from a map (tests/diagnostics). An empty catalog (the default)
/// makes every lookup miss, so callers degrade gracefully to the raw stage
/// code when the dat is unavailable.
/// </summary>
internal sealed class ContractCatalog : IContractCatalog
{
    // client_portal.dat ContractTable file id (DatLoader ContractTable.FILE_ID,
    // which is assembly-internal there). A dat-format constant, not game data.
    private const uint ContractTableFileId = 0x0E00001D;

    private readonly IReadOnlyDictionary<uint, ContractInfo> _byId;

    /// <summary>An empty catalog: every lookup misses.</summary>
    public ContractCatalog()
        : this(new Dictionary<uint, ContractInfo>()) { }

    /// <summary>A catalog over a pre-built id→definition map.</summary>
    public ContractCatalog(IReadOnlyDictionary<uint, ContractInfo> byId) =>
        _byId = byId;

    /// <inheritdoc/>
    public int Count => _byId.Count;

    /// <inheritdoc/>
    public bool TryGet(uint contractId, out ContractInfo info) =>
        _byId.TryGetValue(contractId, out info!);

    /// <summary>
    /// Read the ContractTable from the portal dat into a new catalog. Safe to
    /// call with a null/unavailable dat: returns an empty catalog on any
    /// failure (the prompt then shows just the raw stage code). A read failure
    /// on an otherwise-present dat is logged so a format/corruption problem is
    /// observable rather than silently swallowed.
    /// </summary>
    public static ContractCatalog FromPortalDat(DatDatabase? portalDat)
    {
        if (portalDat is null) return new ContractCatalog();
        try
        {
            var table = portalDat.ReadFromDat<ContractTable>(ContractTableFileId);
            if (table?.Contracts is null) return new ContractCatalog();
            var map = new Dictionary<uint, ContractInfo>(table.Contracts.Count);
            foreach (var kv in table.Contracts)
            {
                var c = kv.Value;
                if (c is null) continue;
                // Dat-defined contract locations -> global (worldX, worldY). The
                // dat leaves a location's ObjCellID 0 when unset; skip those.
                float? endX = null, endY = null, areaX = null, areaY = null;
                if (c.LocationNPCEnd is { ObjCellID: not 0 } endLoc)
                    (endX, endY) = AcCoords.ToGlobalXY(endLoc.ObjCellID, endLoc.Frame.Origin);
                if (c.LocationQuestArea is { ObjCellID: not 0 } areaLoc)
                    (areaX, areaY) = AcCoords.ToGlobalXY(areaLoc.ObjCellID, areaLoc.Frame.Origin);
                map[kv.Key] = new ContractInfo(
                    c.ContractId,
                    c.ContractName ?? string.Empty,
                    c.Description ?? string.Empty,
                    c.DescriptionProgress ?? string.Empty,
                    c.NameNPCStart ?? string.Empty,
                    c.NameNPCEnd ?? string.Empty,
                    endX, endY, areaX, areaY);
            }
            return new ContractCatalog(map);
        }
        catch (Exception ex)
        {
            // The dat is present but the table could not be read (unexpected
            // format, corruption, or a custom server without the table). An
            // empty catalog from a real read failure must not be silent, so
            // surface it, then degrade to raw id + stage.
            Console.Error.WriteLine(
                $"[contracts] ContractTable read failed ({ex.GetType().Name}: {ex.Message}); " +
                "continuing with raw contract id + stage only");
            return new ContractCatalog();
        }
    }
}
