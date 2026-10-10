using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiScope.Analysis;
using DiScope.Model;

namespace DiScope;

/// <summary>A captured graph plus its diagnostics: what every exporter and editor extension consumes.</summary>
public sealed class Report
{
    public int SchemaVersion { get; set; } = 1;
    public string Tool { get; set; } = "DiScope";
    public string ToolVersion { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public AppInfo App { get; set; } = new();
    public ReportSummary Summary { get; set; } = new();
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public List<ServiceNode> Services { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static Report Create(RawGraph graph)
    {
        var diagnostics = DiagnosticRules.Run(graph);
        return new Report
        {
            ToolVersion = typeof(Report).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0] ?? "0.0.0",
            GeneratedAt = DateTimeOffset.UtcNow,
            App = graph.App,
            Services = graph.Services,
            Diagnostics = diagnostics,
            Summary = new ReportSummary
            {
                Services = graph.Services.Count(s => NodeKinds.IsRegistration(s.Kind)),
                AppServices = graph.Services.Count(s => NodeKinds.IsRegistration(s.Kind) && s.Origin == Origins.App),
                Roots = graph.Services.Count(s => !NodeKinds.IsRegistration(s.Kind)),
                Errors = diagnostics.Count(d => d.Severity == Severities.Error),
                Warnings = diagnostics.Count(d => d.Severity == Severities.Warning),
                Infos = diagnostics.Count(d => d.Severity == Severities.Info),
            },
        };
    }

    public static RawGraph LoadRawGraph(string path) =>
        JsonSerializer.Deserialize<RawGraph>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"'{path}' does not contain a DiScope graph.");
}

public sealed class ReportSummary
{
    public int Services { get; set; }
    public int AppServices { get; set; }
    public int Roots { get; set; }
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public int Infos { get; set; }
}
