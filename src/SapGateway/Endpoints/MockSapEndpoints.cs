using System.Text;

namespace SapGateway.Endpoints;

/// <summary>
/// Built-in fake "SAP system" so the gateway can be exercised end-to-end
/// without a real SAP backend. Used when Sap:BaseUrl = "self".
/// </summary>
public static class MockSapEndpoints
{
    private static readonly byte[] ValidXml = Utf8("""
        <?xml version="1.0" encoding="utf-8"?>
        <order id="500123" currency="USD">
          <customer>Acme GmbH</customer>
          <items>
            <item sku="A-1" qty="2">Widget</item>
            <item sku="B-7" qty="1">Gadget</item>
            <item sku="C-9" qty="5">Sprocket</item>
          </items>
          <delivery express="true">
            <address>
              <street>Hauptstrasse 12</street>
              <city>Walldorf</city>
            </address>
          </delivery>
          <note>Deliver before Friday</note>
        </order>
        """);

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

        group.MapGet("/valid", (HttpContext ctx) => WriteXml(ctx, ValidXml, 200));

        group.MapGet("/invalid-char", (HttpContext ctx) => WriteXml(ctx, InvalidCharXml, 200));

        group.MapGet("/malformed", (HttpContext ctx) => WriteXml(ctx, MalformedXml, 200));

        group.MapGet("/error", (HttpContext ctx) => WriteXml(ctx, ErrorXml, 500));

        group.MapGet("/large", (HttpContext ctx) => WriteXml(ctx, LargeXml.Value, 200));
    }

    private static async Task WriteXml(HttpContext ctx, byte[] xml, int statusCode)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.Body.WriteAsync(xml, ctx.RequestAborted);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
