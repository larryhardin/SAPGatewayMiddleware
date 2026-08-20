using SapGateway.Endpoints;
using SapGateway.Middleware;
using SapGateway.Options;

var builder = WebApplication.CreateBuilder(args);

// Load .env from the project directory (gitignored, local secrets only).
// Variables already set in the real environment take precedence.
var envFile = Path.Combine(builder.Environment.ContentRootPath, ".env");
if (File.Exists(envFile))
{
    foreach (var line in File.ReadAllLines(envFile))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
        var idx = trimmed.IndexOf('=');
        if (idx <= 0) continue;
        var key = trimmed[..idx].Trim();
        if (Environment.GetEnvironmentVariable(key) is null)
            Environment.SetEnvironmentVariable(key, trimmed[(idx + 1)..].Trim());
    }
}

var sapOptions = builder.Configuration.GetSection(SapOptions.SectionName).Get<SapOptions>() ?? new SapOptions();
sapOptions.EnsureMockDestination();
foreach (var destination in sapOptions.Destinations)
    destination.ExpandEnvironmentVariables(builder.Configuration);

builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(sapOptions));
builder.Services.AddHttpClient("sap", client =>
{
    client.Timeout = TimeSpan.FromSeconds(sapOptions.TimeoutSeconds);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Safety net: if an upstream still responds compressed, decompress before
    // the XML inspection/validation pipeline sees the bytes.
    AutomaticDecompression = System.Net.DecompressionMethods.All,
});
// SAP ICM answers the "Expect: 100-continue" handshake with a 400 HTML page
// before the body is read; curl does not send it, HttpClient does.
builder.Services.AddHttpClient("sap").ConfigureHttpClient(
    c => c.DefaultRequestHeaders.ExpectContinue = false);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Built-in fake SAP system for local development (destination "self").
app.MapMockSapEndpoints();

// Lists the configured SAP destinations for the UI (no secrets are exposed).
app.MapGet("/api/destinations", () => sapOptions.Destinations.Select(d => new
{
    d.Name,
    d.Description,
    isMock = string.Equals(d.BaseUrl, "self", StringComparison.OrdinalIgnoreCase),
    hasCredentials = d.HasCredentials,
}));

// Gateway: intercepts /sap/{destination}/**, forwards to that SAP system with its
// basic-auth credentials, inspects + validates + transforms the payload, and only
// then sets the HTTP status code.
app.UseMiddleware<SapGatewayMiddleware>();

app.Run();
