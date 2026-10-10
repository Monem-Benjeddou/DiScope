using System.Text;
using System.Text.Json;
using DiScope.Analysis;
using DiScope.Model;

namespace DiScope.Export;

public static class JsonExporter
{
    public static string Export(Report report) => JsonSerializer.Serialize(report, Report.JsonOptions);
}

/// <summary>Compiler-style lines (<c>error DI001: ...</c>) that CI systems pick up as annotations.</summary>
public static class TextExporter
{
    public static string Export(Report report, bool includeInfo = true)
    {
        var text = new StringBuilder();
        foreach (var d in report.Diagnostics.Where(d => includeInfo || d.Severity != Severities.Info))
        {
            text.Append(d.Severity).Append(' ').Append(d.Id).Append(": ").AppendLine(d.Message);
            if (d.Help is not null) text.Append("    help: ").AppendLine(d.Help);
        }

        if (text.Length > 0) text.AppendLine();
        text.AppendLine(Summary(report));
        return text.ToString();
    }

    public static string Summary(Report report)
    {
        var s = report.Summary;
        return $"{report.App.Assembly}: {s.Services} services ({s.AppServices} from the app), {s.Roots} controllers/middleware — " +
               $"{s.Errors} error(s), {s.Warnings} warning(s), {s.Infos} info.";
    }
}

/// <summary>Mermaid flowchart of the app's services, for READMEs and pull requests.</summary>
public static class MermaidExporter
{
    public static string Export(Report report, bool includeFramework = false)
    {
        var visible = report.Services
            .Where(n => includeFramework || n.Origin != Origins.Framework)
            .ToDictionary(n => n.Id);
        var captive = report.Diagnostics
            .Where(d => d.Id == RuleIds.Captive)
            .SelectMany(d => d.Path.Zip(d.Path.Skip(1)))
            .ToHashSet();

        var text = new StringBuilder("flowchart LR\n");
        foreach (var node in visible.Values)
        {
            var service = TypeDisplay.Short(node.ServiceType);
            var impl = TypeDisplay.Short(node.ImplementationType);
            var label = Escape(service);
            if (impl.Length > 0 && impl != service) label += "<br/><small>" + Escape(impl) + "</small>";
            text.Append("  ").Append(node.Id).Append("[\"").Append(label).Append("\"]:::")
                .AppendLine(node.Kind is NodeKinds.Controller or NodeKinds.Hub or NodeKinds.PageModel or NodeKinds.Middleware
                    ? "root" : node.Lifetime.ToLowerInvariant());
        }

        var edgeIndex = 0;
        var captiveEdges = new List<int>();
        var missing = new Dictionary<string, string>();
        foreach (var node in visible.Values)
        {
            foreach (var dep in node.Dependencies)
            {
                if (dep.Resolution == Resolutions.Missing)
                {
                    if (!missing.TryGetValue(dep.Type, out var missingId))
                    {
                        missingId = "m" + missing.Count;
                        missing[dep.Type] = missingId;
                        text.Append("  ").Append(missingId).Append("[\"").Append(Escape(TypeDisplay.Short(dep.Type)))
                            .AppendLine(" (missing)\"]:::missing");
                    }

                    text.Append("  ").Append(node.Id).Append(" -.-> ").AppendLine(missingId);
                    edgeIndex++;
                    continue;
                }

                foreach (var target in dep.Targets.Where(visible.ContainsKey).Distinct())
                {
                    if (captive.Contains((node.Id, target))) captiveEdges.Add(edgeIndex);
                    text.Append("  ").Append(node.Id).Append(" --> ").AppendLine(target);
                    edgeIndex++;
                }
            }
        }

        text.AppendLine("  classDef singleton fill:#e8f0fe,stroke:#3b6fd8");
        text.AppendLine("  classDef scoped fill:#e6f6ec,stroke:#2f8f57");
        text.AppendLine("  classDef transient fill:#fdf3e1,stroke:#c98a16");
        text.AppendLine("  classDef root fill:#f1ecfb,stroke:#7a5ccf");
        text.AppendLine("  classDef missing fill:#fdecec,stroke:#d24545,stroke-dasharray: 4 3");
        if (captiveEdges.Count > 0)
            text.Append("  linkStyle ").Append(string.Join(",", captiveEdges)).AppendLine(" stroke:#d24545,stroke-width:3px");
        return text.ToString();
    }

    // Mermaid entity codes, so generic types like IRepository<T> survive inside labels.
    private static string Escape(string value) => value.Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;");
}

/// <summary>The self-contained interactive viewer with the report embedded.</summary>
public static class HtmlExporter
{
    private const string Placeholder = "/*__DISCOPE_DATA__*/null";

    public static string Export(Report report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(Report.JsonOptions) { WriteIndented = false })
            .Replace("</", "<\\/", StringComparison.Ordinal); // never close the <script> early
        return LoadViewer().Replace(Placeholder, json, StringComparison.Ordinal);
    }

    /// <summary>The viewer without data, for hosts (VS Code, Visual Studio) that post the report via postMessage.</summary>
    public static string LoadViewer()
    {
        using var stream = typeof(HtmlExporter).Assembly.GetManifestResourceStream("DiScope.Viewer.html")
                           ?? throw new InvalidOperationException("The viewer resource is missing from DiScope.Core.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
