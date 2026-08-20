using System.Security;
using System.Text;
using System.Xml.Linq;

namespace SapGateway.Endpoints;

/// <summary>
/// Built-in fake "SAP system" so the gateway can be exercised end-to-end
/// without a real SAP backend. Used when Sap:BaseUrl = "self".
/// </summary>
public static class MockSapEndpoints
{
    // The "valid" scenario serves the captured real /ENSX/BUSOBJ_GET response
    // (MockSap/sales-document.xml) via SalesDocumentResultXml below.

    // '\v' = 0x0B vertical tab — a classic SAP interface payload offender.
    private static readonly byte[] InvalidCharXml = Utf8(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<order id=\"500124\">\n" +
        "  <customer>Contoso\u000BLtd</customer>\n" +
        "  <note>First line\u000BSecond line</note>\n" +
        "</order>");

    private static readonly byte[] MalformedXml = Utf8("""
        <?xml version="1.0" encoding="utf-8"?>
        <order id="500125">
          <customer>Missing close tags
          <items><item sku="X-1">Broken</item>
        """);

    private static readonly byte[] ErrorXml = Utf8("""
        <?xml version="1.0" encoding="utf-8"?>
        <error>
          <code>SY/530</code>
          <message>User locked on SAP system</message>
        </error>
        """);

    private static readonly Lazy<byte[]> LargeXml = new(() =>
    {
        var sb = new StringBuilder(2_500_000);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><catalog>");
        for (int i = 0; i < 20_000; i++)
            sb.Append($"<product sku=\"P-{i}\"><name>Product {i}</name><price>{i * 1.7m:F2}</price></product>");
        sb.Append("</catalog>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    });

    public static void MapMockSapEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/mock-sap").WithTags("Mock SAP");

        // enosix Link style endpoint: /mock-sap/?sap_client=800&sap-language=EN&function=/ENSX/BUSOBJ_GET
        // with an XML body of <PARAM><I_TYPE>...</I_TYPE><I_KEY>...</I_KEY></PARAM>.
        // NOTE: the <RESULT> shape below is a best guess — replace it with a captured
        // real response once one is available.
        group.MapMethods("/", new[] { "GET", "POST" }, HandleEnosixRequestAsync);

        // Well-formed payload shaped like a real sales document response.
        group.MapGet("/valid", (HttpContext ctx) => WriteXml(ctx, SalesDocumentResultXml("29039"), 200));

        group.MapGet("/invalid-char", (HttpContext ctx) => WriteXml(ctx, InvalidCharXml, 200));

        group.MapGet("/malformed", (HttpContext ctx) => WriteXml(ctx, MalformedXml, 200));

        group.MapGet("/error", (HttpContext ctx) => WriteXml(ctx, ErrorXml, 500));

        group.MapGet("/large", (HttpContext ctx) => WriteXml(ctx, LargeXml.Value, 200));
    }

    private static async Task HandleEnosixRequestAsync(HttpContext ctx)
    {
        var query = ctx.Request.Query;
        string function = query["function"].ToString();
        string sessionCmd = query["sap-sessioncmd"].ToString();
        string client = query["sap_client"].FirstOrDefault()
            ?? query["sap-client"].FirstOrDefault()
            ?? "800";
        string language = string.IsNullOrEmpty(query["sap-language"]) ? "EN" : query["sap-language"].ToString();

        // SAP-style session cookies, mirroring the Insomnia capture.
        ctx.Response.Headers.Append("Set-Cookie", $"sap-usercontext=sap-language%3D{language}; path=/");
        ctx.Response.Headers.Append("Set-Cookie", string.Equals(sessionCmd, "cancel", StringComparison.OrdinalIgnoreCase)
            ? $"SAP_SESSIONID_MCK_{client}=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT"
            : $"SAP_SESSIONID_MCK_{client}={Guid.NewGuid():N}%3d; path=/");

        if (string.IsNullOrEmpty(function))
        {
            await WriteXml(ctx, EnosixErrorXml("Missing 'function' query parameter."), 400);
            return;
        }

        if (function.Equals("/ENSX/BUSOBJ_GET", StringComparison.OrdinalIgnoreCase))
        {
            var (type, key, error) = await ReadBusObjParamsAsync(ctx);
            if (error is not null)
            {
                await WriteXml(ctx, EnosixErrorXml(error), 400);
                return;
            }
            if (!string.Equals(type, "EnosixSalesDocument", StringComparison.OrdinalIgnoreCase))
            {
                await WriteXml(ctx, EnosixErrorXml($"Unsupported business object '{type}'."), 400);
                return;
            }
            await WriteXml(ctx, SalesDocumentResultXml(key!), 200);
            return;
        }

        await WriteXml(ctx, EnosixErrorXml($"Unknown function '{function}'."), 400);
    }

    /// <summary>Reads I_TYPE / I_KEY out of the PARAM request body.</summary>
    private static async Task<(string? Type, string? Key, string? Error)> ReadBusObjParamsAsync(HttpContext ctx)
    {
        string body;
        using (var sr = new StreamReader(ctx.Request.Body))
            body = await sr.ReadToEndAsync(ctx.RequestAborted);

        const string shapeError = "Request body must be <PARAM><I_TYPE>...</I_TYPE><I_KEY>...</I_KEY></PARAM>.";
        if (string.IsNullOrWhiteSpace(body))
            return (null, null, shapeError);

        try
        {
            var param = XElement.Parse(body);
            string? type = param.Element("I_TYPE")?.Value;
            string? key = param.Element("I_KEY")?.Value;
            if (type is null || key is null)
                return (type, key, shapeError);
            return (type, key, null);
        }
        catch (Exception ex)
        {
            return (null, null, $"Invalid request XML: {ex.Message}");
        }
    }

    // Captured real /ENSX/BUSOBJ_GET response for sales document 29039.
    // The requested I_KEY is substituted for 29039 (E_KEY, VBELN, EnosixObjKey).
    private static readonly Lazy<string> SalesDocumentTemplate = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MockSap", "sales-document.xml")));

    private static byte[] SalesDocumentResultXml(string key)
    {
        string xml = SalesDocumentTemplate.Value.Replace("29039", SecurityElement.Escape(key.Trim()));
        return Utf8(xml);
    }

    private static byte[] EnosixErrorXml(string message) => Utf8($"""
        <?xml version="1.0" encoding="utf-8"?>
        <error>
          <code>/ENSX/400</code>
          <message>{SecurityElement.Escape(message)}</message>
        </error>
        """);

    private static async Task WriteXml(HttpContext ctx, byte[] xml, int statusCode)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.Body.WriteAsync(xml, ctx.RequestAborted);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
