using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Options;
using SapGateway.Buffers;
using SapGateway.Options;
using SapGateway.Services;
using SapGateway.Streams;

namespace SapGateway.Middleware;

/// <summary>
/// Gateway middleware for /sap/** requests.
///
/// Pipeline per request:
///   1. Forward the incoming request to the configured SAP system (streaming body).
///   2. Read the SAP response through an <see cref="InspectingStream"/> — every byte
///      is scanned for invalid XML control characters (0x00-0x1F except TAB/LF/CR)
///      while it flows, with zero copies.
///   3. An <see cref="XmlReader"/> validates well-formedness and streams the content
///      into <see cref="XmlToJsonTransformer"/>, which writes JSON into a pooled buffer.
///   4. ONLY THEN is the HTTP status code decided — after the full payload was read
///      and validated. The JSON exists exactly once (pooled) and is written to the
///      client as a raw value inside a small envelope.
/// </summary>
public sealed class SapGatewayMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SapOptions _options;
    private readonly ILogger<SapGatewayMiddleware> _logger;

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Transfer-Encoding", "Keep-Alive",
        "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Upgrade",
    };

    public SapGatewayMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        IOptions<SapOptions> options,
        ILogger<SapGatewayMiddleware> logger)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/sap", out var remainder))
        {
            await _next(context);
            return;
        }

        string upstreamPath = remainder.Value?.TrimStart('/') ?? string.Empty;
        Uri target = ResolveTarget(context, upstreamPath);

        using var upstreamRequest = BuildUpstreamRequest(context, target);

        var client = _httpClientFactory.CreateClient("sap");
        using var upstreamResponse = await client.SendAsync(
            upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

        var inspection = new XmlInspectionLog();
        using var jsonBuffer = new PooledBufferWriter(initialCapacity: 16 * 1024);
        string? validationError = null;
        bool transformed = false;

        await using (var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted))
        await using (var inspectingStream = new InspectingStream(upstreamStream, inspection))
        {
            try
            {
                await XmlToJsonTransformer.TransformAsync(inspectingStream, jsonBuffer, context.RequestAborted);
                transformed = true;
            }
            catch (XmlException ex)
            {
                validationError = $"XML validation failed at line {ex.LineNumber}, position {ex.LinePosition}: {ex.Message}";
                _logger.LogWarning("Invalid XML from {Target}: {Error}", target, validationError);
            }
        }

        // ------------------------------------------------------------------
        // The whole point of this middleware: the status code is set HERE,
        // after the payload has been fully read, inspected and validated.
        // ------------------------------------------------------------------
        int sapStatus = (int)upstreamResponse.StatusCode;
        int statusCode = sapStatus;
        var messages = new List<string>(inspection.Messages);

        if (inspection.InvalidCharacterCount > 0)
        {
            messages.Insert(0, $"Payload contains {inspection.InvalidCharacterCount} invalid XML control character(s).");
            statusCode = StatusCodes.Status502BadGateway;
        }
        if (validationError is not null)
        {
            messages.Add(validationError);
            statusCode = StatusCodes.Status502BadGateway;
        }
        if (messages.Count == 0)
        {
            messages.Add($"Payload inspected, validated and transformed ({inspection.BytesScanned:N0} bytes scanned).");
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["X-Upstream-Status"] = sapStatus.ToString();

        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WriteNumber("statusCode", statusCode);
        writer.WriteNumber("sapStatusCode", sapStatus);
        writer.WriteString("upstream", target.ToString());
        writer.WritePropertyName("messages");
        writer.WriteStartArray();
        foreach (string message in messages)
            writer.WriteStringValue(message);
        writer.WriteEndArray();
        writer.WritePropertyName("payload");
        if (transformed)
            writer.WriteRawValue(jsonBuffer.WrittenSpan, skipInputValidation: true);
        else
            writer.WriteNullValue();
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted);
    }

    private HttpRequestMessage BuildUpstreamRequest(HttpContext context, Uri target)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue; // set on content below
            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        foreach (var (key, value) in _options.DefaultHeaders)
            request.Headers.TryAddWithoutValidation(key, value);

        if (context.Request.ContentLength > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            // Streamed through — the request body is never buffered either.
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return request;
    }

    private Uri ResolveTarget(HttpContext context, string path)
    {
        string baseUrl = _options.BaseUrl;
        if (string.Equals(baseUrl, "self", StringComparison.OrdinalIgnoreCase))
            baseUrl = $"{context.Request.Scheme}://{context.Request.Host}/mock-sap";

        return new Uri($"{baseUrl.TrimEnd('/')}/{path}{context.Request.QueryString}");
    }
}
