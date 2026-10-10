namespace DiScope.Analysis;

public static class Severities
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";

    public static int Rank(string severity) => severity switch
    {
        Error => 0,
        Warning => 1,
        _ => 2,
    };
}

public sealed class Diagnostic
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>How to fix it.</summary>
    public string? Help { get; set; }

    /// <summary>Every node involved, for highlighting.</summary>
    public List<string> Nodes { get; set; } = new();

    /// <summary>Ordered chain of nodes (captive paths, cycles), when the issue is a path.</summary>
    public List<string> Path { get; set; } = new();

    /// <summary>The unregistered type, for DI002.</summary>
    public string? MissingType { get; set; }
}

public static class RuleIds
{
    public const string Captive = "DI001";
    public const string Missing = "DI002";
    public const string Cycle = "DI003";
    public const string TransientInSingleton = "DI004";
    public const string Shadowed = "DI005";
    public const string Duplicate = "DI006";
    public const string Unused = "DI007";
    public const string OpaqueFactory = "DI008";
    public const string NotConstructible = "DI009";
}
