// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Tests for CombatFeelStore — durable per-character persistence of the
// CombatFeelLedger. Pins the round-trip (save then load preserves the bot's
// recorded outcomes), the dirty-gated write, graceful handling of a
// missing/corrupt file, and per-character path sanitization.

using System;
using System.IO;
using System.Linq;

using HeadlessAcClient.Strategy;

using Xunit;

namespace HeadlessAcClient.Tests;

public class CombatFeelStoreTests : IDisposable
{
    private readonly string _dir;

    public CombatFeelStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "combat-feel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string PathFor(string name) => Path.Combine(_dir, $"{name}.json");

    private static CombatFeelLedger.MobIdentity Wcid(uint w, string? name = null) => new(w, name);

    [Fact]
    public void SaveThenLoad_RoundTripsRecordedOutcomes()
    {
        var path = PathFor("roundtrip");
        var ledger = new CombatFeelLedger();
        ledger.RecordDeath(Wcid(211u, "Mudlurk Mosswart"));
        ledger.RecordKill(Wcid(24937u, "The Chicken"));

        CombatFeelStore.SaveIfDirty(ledger, path);
        Assert.True(File.Exists(path));

        var loaded = CombatFeelStore.LoadOrNew(path);
        var rows = loaded.Snapshot()!;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Name == "Mudlurk Mosswart" && r.Deaths == 1);
        Assert.Contains(rows, r => r.Name == "The Chicken" && r.Kills == 1);
    }

    [Fact]
    public void SaveIfDirty_NoOp_WhenNotDirty()
    {
        var path = PathFor("notdirty");
        var ledger = new CombatFeelLedger(); // never recorded => not dirty
        CombatFeelStore.SaveIfDirty(ledger, path);
        Assert.False(File.Exists(path)); // nothing written
    }

    [Fact]
    public void SaveIfDirty_ClearsDirty_AndSkipsSecondWrite()
    {
        var path = PathFor("clears");
        var ledger = new CombatFeelLedger();
        ledger.RecordKill(Wcid(1u, "A"));
        Assert.True(ledger.Dirty);

        CombatFeelStore.SaveIfDirty(ledger, path);
        Assert.False(ledger.Dirty);

        var firstWrite = File.GetLastWriteTimeUtc(path);
        // A second save with no new changes must not rewrite the file.
        CombatFeelStore.SaveIfDirty(ledger, path);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Save_OverwritesExistingFile_Atomically()
    {
        var path = PathFor("overwrite");
        var first = new CombatFeelLedger();
        first.RecordKill(Wcid(1u, "A"));
        CombatFeelStore.SaveIfDirty(first, path);

        var second = CombatFeelStore.LoadOrNew(path);
        second.RecordDeath(Wcid(2u, "B"));
        CombatFeelStore.SaveIfDirty(second, path);

        var loaded = CombatFeelStore.LoadOrNew(path);
        var rows = loaded.Snapshot()!;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Name == "A" && r.Kills == 1);
        Assert.Contains(rows, r => r.Name == "B" && r.Deaths == 1);
        Assert.False(File.Exists(path + ".tmp")); // temp cleaned up by replace/move
    }

    [Fact]
    public void LoadOrNew_MissingFile_ReturnsEmptyLedger()
    {
        var loaded = CombatFeelStore.LoadOrNew(PathFor("does-not-exist"));
        Assert.True(loaded.IsEmpty);
    }

    [Fact]
    public void LoadOrNew_CorruptFile_ReturnsEmptyLedger()
    {
        var path = PathFor("corrupt");
        File.WriteAllText(path, "this is not valid json {{{{");
        var loaded = CombatFeelStore.LoadOrNew(path);
        Assert.True(loaded.IsEmpty);
    }

    [Fact]
    public void ResolvePath_SanitizesCharacterName_AndHonorsStateDir()
    {
        var prev = Environment.GetEnvironmentVariable("AC_BOTS_STATE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("AC_BOTS_STATE_DIR", _dir);
            // A name with path separators must collapse to a single safe file.
            var p = CombatFeelStore.ResolvePath("Bad/Name:01");
            Assert.Equal(_dir, Path.GetDirectoryName(p));
            var file = Path.GetFileName(p);
            Assert.DoesNotContain('/', file);
            Assert.DoesNotContain('\\', file);
            Assert.DoesNotContain(':', file);
            Assert.StartsWith("combat-feel-", file);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AC_BOTS_STATE_DIR", prev);
        }
    }

    [Fact]
    public void SaveIfDirty_MergesConcurrentOnDiskChanges_NoLostUpdate()
    {
        var path = PathFor("concurrent");

        // Our in-memory ledger (loaded earlier) recorded a kill.
        var mine = new CombatFeelLedger();
        mine.RecordKill(Wcid(1u, "A"));

        // Meanwhile another writer persisted a DIFFERENT kind's outcome.
        var theirs = new CombatFeelLedger();
        theirs.RecordDeath(Wcid(2u, "B"));
        CombatFeelStore.SaveIfDirty(theirs, path);

        // Our save must fold in the on-disk "B" instead of blindly clobbering it.
        CombatFeelStore.SaveIfDirty(mine, path);

        var loaded = CombatFeelStore.LoadOrNew(path);
        var rows = loaded.Snapshot()!;
        Assert.Contains(rows, r => r.Name == "A" && r.Kills == 1);
        Assert.Contains(rows, r => r.Name == "B" && r.Deaths == 1); // not lost
    }

    [Fact]
    public void ResolveDirectory_BadStateDir_FallsBackWithoutThrowing()
    {
        var prev = Environment.GetEnvironmentVariable("AC_BOTS_STATE_DIR");
        try
        {
            // An invalid path (illegal characters) must not throw — it falls
            // back to a writable default.
            Environment.SetEnvironmentVariable("AC_BOTS_STATE_DIR", "\0:::*?<>|invalid");
            var dir = CombatFeelStore.ResolveDirectory();
            Assert.False(string.IsNullOrWhiteSpace(dir));
            Assert.True(Directory.Exists(dir)); // a usable directory was produced

            // ResolvePath must likewise not throw and yields a usable file path.
            var path = CombatFeelStore.ResolvePath("Headless01");
            var ledger = new CombatFeelLedger();
            ledger.RecordKill(Wcid(1u, "A"));
            CombatFeelStore.SaveIfDirty(ledger, path); // must not throw
        }
        finally
        {
            Environment.SetEnvironmentVariable("AC_BOTS_STATE_DIR", prev);
        }
    }
}
