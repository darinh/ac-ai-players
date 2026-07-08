// SPDX-License-Identifier: AGPL-3.0-or-later
// Tests for CrashDiagnostics — the top-level crash/exit log formatters that make a
// run-ending crash diagnosable (full type + message + stack) instead of silent.
// The pure formatters are tested here; the process-global handler wiring (Install)
// is exercised implicitly by the app.

using System;
using HeadlessAcClient;
using Xunit;

namespace HeadlessAcClient.Tests;

public class CrashDiagnosticsTests
{
    [Fact]
    public void FormatUnhandled_IncludesTypeMessageAndTerminating()
    {
        Exception caught;
        try { throw new InvalidOperationException("boom-42"); }
        catch (Exception ex) { caught = ex; }

        var line = CrashDiagnostics.FormatUnhandled(caught, isTerminating: true);
        Assert.Contains("[crash] UNHANDLED EXCEPTION", line);
        Assert.Contains("terminating=True", line);
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains("boom-42", line);
        // ToString() on a THROWN exception includes the stack trace marker.
        Assert.Contains("at ", line);
    }

    [Fact]
    public void FormatUnhandled_HandlesNullExceptionObject()
    {
        var line = CrashDiagnostics.FormatUnhandled(null, isTerminating: false);
        Assert.Contains("(null ExceptionObject)", line);
        Assert.Contains("terminating=False", line);
    }

    [Fact]
    public void FormatUnhandled_HandlesNonExceptionObject()
    {
        var line = CrashDiagnostics.FormatUnhandled("raw-error-payload", isTerminating: true);
        Assert.Contains("raw-error-payload", line);
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("hostile ToString");
    }

    [Fact]
    public void FormatUnhandled_DoesNotThrowOnHostileToString()
    {
        // A buggy payload whose ToString() throws must NOT defeat the crash logger —
        // the formatter is total and yields a typed fallback marker.
        var line = CrashDiagnostics.FormatUnhandled(new ThrowingToString(), isTerminating: true);
        Assert.Contains("[crash] UNHANDLED EXCEPTION", line);
        Assert.Contains("ToString threw InvalidOperationException", line);
    }

    [Fact]
    public void FormatUnobservedTask_IncludesDetail()
    {
        Exception caught;
        try { throw new TimeoutException("stuck-call"); }
        catch (Exception ex) { caught = ex; }

        var line = CrashDiagnostics.FormatUnobservedTask(caught);
        Assert.Contains("[crash] UNOBSERVED TASK EXCEPTION", line);
        Assert.Contains("TimeoutException", line);
        Assert.Contains("stuck-call", line);
    }

    [Fact]
    public void FormatUnobservedTask_HandlesNull()
    {
        var line = CrashDiagnostics.FormatUnobservedTask(null);
        Assert.Contains("[crash] UNOBSERVED TASK EXCEPTION", line);
        Assert.Contains("(null)", line);
    }
}
