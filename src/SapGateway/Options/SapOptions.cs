namespace SapGateway.Options;

public sealed class SapOptions
{
    public const string SectionName = "Sap";

    /// <summary>Gateway-wide upstream call timeout in seconds (per-destination override possible).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Named SAP destinations, addressable via /sap/{name}/**.
    /// The name "self" is reserved for the built-in mock SAP system.
    /// </summary>
    public List<SapDestination> Destinations { get; set; } = new();

    /// <summary>
    /// Ensures the built-in mock destination exists (added last if the
    /// configuration did not define one).
    /// </summary>
    public void EnsureMockDestination()
    {
        if (Find("self") is null)
            Destinations.Add(new SapDestination { Name = "self", Description = "Built-in mock SAP system", BaseUrl = "self" });
    }

    /// <summary>Looks up a destination by name (case-insensitive).</summary>
    public SapDestination? Find(string name) =>
        Destinations.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}
