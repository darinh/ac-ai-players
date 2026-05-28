// SPDX-License-Identifier: AGPL-3.0-or-later
// ApiClient — HTTP/2 h2c, MessagePack hot paths, JSON health.
// One instance per bot host process; shared across all bots.
// Per-call cost target: < 200 µs on localhost.

using System;
using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AcAiPlayers.Services.Contracts;

using MessagePack;

namespace AcAiPlayers.Services;

public sealed class ApiClientOptions
{
    /// <summary>
    /// HTTP/1.1 base URL for debug endpoints (/v1/health, etc.).
    /// </summary>
    public required Uri DebugBaseUrl { get; init; }

    /// <summary>
    /// HTTP/2 h2c base URL for hot-path endpoints (MessagePack).
    /// </summary>
    public required Uri HotBaseUrl { get; init; }

    public required string BearerToken { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public static ApiClientOptions FromEnvironment()
    {
        var debug = Environment.GetEnvironmentVariable("AC_BOTS_API_URL")
                    ?? "http://127.0.0.1:9100/";
        var hot = Environment.GetEnvironmentVariable("AC_BOTS_API_H2C_URL")
                  ?? "http://127.0.0.1:9101/";
        var token = Environment.GetEnvironmentVariable("AC_BOTS_API_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("[api] WARNING: AC_BOTS_API_TOKEN not set; using dev-insecure-token");
            token = "dev-insecure-token";
        }
        return new ApiClientOptions
        {
            DebugBaseUrl = new Uri(debug),
            HotBaseUrl = new Uri(hot),
            BearerToken = token,
        };
    }
}

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _debugHttp;
    private readonly HttpClient _hotHttp;
    private readonly ApiClientOptions _options;

    private static readonly MessagePackSerializerOptions MsgPackOpts =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.None)
            .WithSecurity(MessagePackSecurity.UntrustedData);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly MediaTypeHeaderValue MsgPackMedia =
        new("application/x-msgpack");
    private static readonly MediaTypeWithQualityHeaderValue MsgPackAccept =
        new("application/x-msgpack");

    public ApiClient(ApiClientOptions options)
    {
        _options = options;
        _debugHttp = BuildClient(options.DebugBaseUrl, options, HttpVersion.Version11);
        _hotHttp = BuildClient(options.HotBaseUrl, options, HttpVersion.Version20);
    }

    private static HttpClient BuildClient(Uri baseUrl, ApiClientOptions options, Version httpVersion)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.None,
        };
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUrl,
            Timeout = options.RequestTimeout,
            DefaultRequestVersion = httpVersion,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.BearerToken);
        return http;
    }

    public async Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/health")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var resp = await _debugHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return (await JsonSerializer.DeserializeAsync<HealthResponse>(stream, JsonOpts, ct).ConfigureAwait(false))
               ?? new HealthResponse();
    }

    public Task<PlanResponse> PlanAsync(PlanRequest body, Guid botId, CancellationToken ct = default)
        => PostMsgpackAsync<PlanRequest, PlanResponse>("v1/llm/plan", body, botId, ct);

    private async Task<TResp> PostMsgpackAsync<TReq, TResp>(
        string path, TReq body, Guid botId, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 1024);
        MessagePackSerializer.Serialize(buffer, body, MsgPackOpts, ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ReadOnlyMemoryContent(buffer.WrittenMemory),
        };
        req.Content.Headers.ContentType = MsgPackMedia;
        req.Headers.Accept.Add(MsgPackAccept);
        req.Headers.TryAddWithoutValidation("X-Bot-Id", botId.ToString("N"));
        req.Headers.TryAddWithoutValidation("X-Request-Id", Guid.NewGuid().ToString("N"));

        using var resp = await _hotHttp.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<TResp>(bytes, MsgPackOpts, ct);
    }

    public void Dispose()
    {
        _debugHttp.Dispose();
        _hotHttp.Dispose();
    }
}

public sealed class HealthResponse
{
    public string Status { get; set; } = "";
    public string Service { get; set; } = "";
    public string Version { get; set; } = "";
}
