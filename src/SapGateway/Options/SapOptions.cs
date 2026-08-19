namespace SapGateway.Options;

public sealed class SapOptions
{
    public const string SectionName = "Sap";

    /// <summary>
    /// Base URL of the SAP system, e.g. "https://sap.example.com:44300/sap/opu/odata/sap".
    /// The special value "self" routes to this app's built-in mock SAP endpoints
    /// (/mock-sap/**) so the demo works without a real SAP system.
    /// </summary>
    public string BaseUrl { get; set; } = "self";

    /// <summary>Static headers added to every upstream request (auth, sap-client, ...).</summary>
    public Dictionary<string, string> DefaultHeaders { get; set; } = new();

    /// <summary>Upstream call timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
