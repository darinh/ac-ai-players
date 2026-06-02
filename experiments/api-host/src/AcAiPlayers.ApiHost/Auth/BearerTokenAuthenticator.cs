// SPDX-License-Identifier: AGPL-3.0-or-later
// Minimal bearer-token gate. Constant-time string compare.
// Hard-coded shared secret for now per architecture doc;
// per-bot identity passed in X-Bot-Id (advisory only — server
// trusts whoever holds the token).

using System;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;

namespace AcAiPlayers.ApiHost.Auth;

public sealed class BearerTokenAuthenticator
{
    private readonly byte[] _expected;

    public BearerTokenAuthenticator(string token)
    {
        _expected = Encoding.UTF8.GetBytes(token);
    }

    public bool TryAuthenticate(HttpContext ctx, out string? failureReason)
    {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var values) || values.Count == 0)
        {
            failureReason = "missing Authorization header";
            return false;
        }
        var header = values[0];
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.Ordinal))
        {
            failureReason = "Authorization header is not a Bearer token";
            return false;
        }
        var supplied = Encoding.UTF8.GetBytes(header[prefix.Length..]);
        if (!CryptographicOperations.FixedTimeEquals(supplied, _expected))
        {
            failureReason = "Bearer token does not match";
            return false;
        }
        failureReason = null;
        return true;
    }
}
