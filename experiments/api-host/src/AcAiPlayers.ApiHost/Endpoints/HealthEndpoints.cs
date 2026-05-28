// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AcAiPlayers.ApiHost.Endpoints;

public static class HealthEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/v1/health", () => Results.Json(new
        {
            status = "ok",
            service = "AcAiPlayers.ApiHost",
            version = typeof(HealthEndpoints).Assembly
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? "0.0.0-dev",
        }));
    }
}
