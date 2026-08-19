namespace SapGateway.Options;

/// <summary>
/// One named SAP destination, addressable via /sap/{name}/**.
/// </summary>
public sealed class SapDestination
{
    /// <summary>Destination name as used in the URL (first /sap/ path segment).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short human-readable description shown in the UI.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Base URL of the SAP system, e.g. "https://sap-host:44300/sap/opu/odata/sap".
    /// The special value "self" routes to this app's built-in mock SAP endpoints.
    /// </summary>
    public string BaseUrl { get; set; } = "self";

    /// <summary>Basic-auth user name. Supports env-var expansion: ${SAP_USER}.</summary>
    public string? UserName { get; set; }

    /// <summary>Basic-auth password. Supports env-var expansion: ${SAP_PASSWORD}.</summary>
    public string? Password { get; set; }

    /// <summary>Static headers added to every upstream request (e.g. x-sap-client).</summary>
    public Dictionary<string, string> DefaultHeaders { get; set; } = new();

    /// <summary>Upstream call timeout in seconds. 0 = use the gateway default.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>True when basic-auth credentials are configured.</summary>
    public bool HasCredentials =>
        !string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(Password);

    /// <summary>
    /// Resolves ${ENV_VAR} placeholders from the process environment, falling back
    /// to configuration (e.g. dotnet user-secrets), so secrets never have to be
    /// committed to appsettings.json.
    /// </summary>
    public void ExpandEnvironmentVariables(IConfiguration? configuration = null)
    {
        BaseUrl = Expand(BaseUrl) ?? BaseUrl;
        UserName = Expand(UserName);
        Password = Expand(Password);
        foreach (var key in DefaultHeaders.Keys.ToList())
            DefaultHeaders[key] = Expand(DefaultHeaders[key]) ?? string.Empty;

        string? Expand(string? value)
        {
            if (value is not { Length: > 3 } v) return value;
            if (!v.StartsWith("${", StringComparison.Ordinal) || !v.EndsWith('}')) return value;
            string envVar = v[2..^1];
            return Environment.GetEnvironmentVariable(envVar)
                ?? configuration?[envVar]
                ?? value;
        }
    }
}
