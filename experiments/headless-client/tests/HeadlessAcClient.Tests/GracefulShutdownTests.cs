// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for GracefulShutdown — the pure path/existence helpers behind the
// file-triggered clean-logoff. The poll loop + cancellation live in Program.cs
// (entry code, not unit-tested by convention); the resolve + existence decision
// is what's testable and what determines whether the watch fires.

using System;
using System.IO;
using HeadlessAcClient;
using Xunit;

namespace HeadlessAcClient.Tests;

public class GracefulShutdownTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ResolveShutdownFilePath_BlankOrUnset_IsNull(string? env)
    {
        Assert.Null(GracefulShutdown.ResolveShutdownFilePath(env));
    }

    [Fact]
    public void ResolveShutdownFilePath_NonBlank_ReturnsTrimmedPath()
    {
        Assert.Equal(@"C:\state\shutdown.request",
            GracefulShutdown.ResolveShutdownFilePath(@"  C:\state\shutdown.request  "));
    }

    [Fact]
    public void IsShutdownRequested_NullPath_False()
    {
        // Watch disabled: never a shutdown request, even though no file is named.
        Assert.False(GracefulShutdown.IsShutdownRequested(null, DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void IsShutdownRequested_BlankPath_False()
    {
        Assert.False(GracefulShutdown.IsShutdownRequested("   ", DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void IsShutdownRequested_ConfiguredButMissingFile_False()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "ac-bots-shutdown-test-" + Guid.NewGuid().ToString("N") + ".request");
        Assert.False(File.Exists(path)); // precondition: file absent
        Assert.False(GracefulShutdown.IsShutdownRequested(path, DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void IsShutdownRequested_FileWrittenAfterStart_True()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "ac-bots-shutdown-test-" + Guid.NewGuid().ToString("N") + ".request");
        try
        {
            // Process started a minute ago; the request is written NOW → honoured.
            var processStartUtc = DateTime.UtcNow.AddMinutes(-1);
            File.WriteAllText(path, "stop");
            Assert.True(GracefulShutdown.IsShutdownRequested(path, processStartUtc));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void IsShutdownRequested_StaleFileWrittenBeforeStart_Ignored()
    {
        // The core safety guard: a leftover request file written by a PRIOR run
        // (write time before this process started) must be IGNORED, so a stale or
        // undeletable file cannot shut down or crash-loop a fresh run.
        var path = Path.Combine(Path.GetTempPath(),
            "ac-bots-shutdown-test-" + Guid.NewGuid().ToString("N") + ".request");
        try
        {
            File.WriteAllText(path, "stop");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10)); // stale
            var processStartUtc = DateTime.UtcNow.AddMinutes(-1);           // started later
            Assert.False(GracefulShutdown.IsShutdownRequested(path, processStartUtc));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void IsShutdownRequested_ReflectsWriteThenDelete()
    {
        // The watch must see a fresh file appear (deploy writes it) and, once
        // cleared, report false again so a re-armed run isn't tripped by a consumed
        // file. processStartUtc is fixed in the past so the fresh write is honoured.
        var path = Path.Combine(Path.GetTempPath(),
            "ac-bots-shutdown-test-" + Guid.NewGuid().ToString("N") + ".request");
        var processStartUtc = DateTime.UtcNow.AddMinutes(-1);
        try
        {
            Assert.False(GracefulShutdown.IsShutdownRequested(path, processStartUtc));
            File.WriteAllText(path, "stop");
            Assert.True(GracefulShutdown.IsShutdownRequested(path, processStartUtc));
            File.Delete(path);
            Assert.False(GracefulShutdown.IsShutdownRequested(path, processStartUtc));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ShutdownFileEnvVar_IsTheDocumentedName()
    {
        // Pin the env-var name: the deploy script and the docs reference it, so a
        // rename must be a deliberate, test-visible change.
        Assert.Equal("AC_BOTS_SHUTDOWN_FILE", GracefulShutdown.ShutdownFileEnvVar);
    }
}
