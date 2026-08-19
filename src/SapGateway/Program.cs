using SapGateway.Endpoints;
using SapGateway.Middleware;
using SapGateway.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SapOptions>(builder.Configuration.GetSection(SapOptions.SectionName));
builder.Services.AddHttpClient("sap", (sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SapOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Built-in fake SAP system for local development (Sap:BaseUrl = "self").
app.MapMockSapEndpoints();

// Gateway: intercepts /sap/**, forwards to SAP, inspects + validates + transforms
// the payload, and only then sets the HTTP status code.
app.UseMiddleware<SapGatewayMiddleware>();

app.Run();
