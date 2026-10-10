using DiScope.Export;
using DiScope.Model;
using static DiScope.Model.Lifetimes;

namespace DiScope.Core.Tests;

public class ExporterTests
{
    private static Report SampleReport() => Report.Create(new TestGraph()
        .Add("IRepo", "Repo", Scoped)
        .Add("ICache", "Cache", Singleton).DependsOn("IRepo")
        .Add("OrdersController", "OrdersController", Transient, NodeKinds.Controller).DependsOn("ICache").DependsOn("IMailer")
        .Graph);

    [Fact]
    public void Summary_counts_registrations_roots_and_severities()
    {
        var s = SampleReport().Summary;
        Assert.Equal((2, 2, 1, 2, 0, 0), (s.Services, s.AppServices, s.Roots, s.Errors, s.Warnings, s.Infos));
    }

    [Fact]
    public void Text_output_uses_compiler_style_lines()
    {
        var text = TextExporter.Export(SampleReport());
        Assert.Contains("error DI001: Singleton 'Cache' depends on scoped 'IRepo' (Repo).", text);
        Assert.Contains("error DI002: 'OrdersController' needs 'IMailer'", text);
        Assert.Contains("TestApp: 2 services", text);
    }

    [Fact]
    public void Html_output_embeds_the_report_safely()
    {
        var report = SampleReport();
        report.App.Assembly = "</script><script>alert(1)</script>";
        var html = HtmlExporter.Export(report);

        Assert.DoesNotContain("/*__DISCOPE_DATA__*/null", html);
        Assert.DoesNotContain("</script><script>alert(1)", html);
        Assert.Contains("\\u003C/script\\u003E", html); // System.Text.Json escapes < and > by default
    }

    [Fact]
    public void Viewer_without_data_is_available_for_editor_hosts()
    {
        Assert.Contains("/*__DISCOPE_DATA__*/null", HtmlExporter.LoadViewer());
    }

    [Fact]
    public void Mermaid_marks_captive_edges_and_missing_services()
    {
        var mermaid = MermaidExporter.Export(SampleReport());
        Assert.StartsWith("flowchart LR", mermaid);
        Assert.Contains("s1 --> s0", mermaid);
        Assert.Contains("IMailer (missing)", mermaid);
        Assert.Contains("linkStyle 0 stroke:#d24545", mermaid);
    }

    [Fact]
    public void Json_round_trips_through_the_raw_graph_loader()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonExporter.Export(SampleReport()));
            var graph = Report.LoadRawGraph(path);
            Assert.Equal(3, graph.Services.Count);
            Assert.Equal("missing", graph.Services[2].Dependencies[1].Resolution);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ViewerSyncTests
{
    [Fact]
    public void VsCode_extension_ships_the_current_viewer()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DiScope.sln"))) dir = Path.GetDirectoryName(dir)!;
        var shipped = Path.Combine(dir, "editors", "vscode", "media", "viewer.html");
        Assert.Equal(Export.HtmlExporter.LoadViewer(), File.ReadAllText(shipped));
    }
}
