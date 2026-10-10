using DiScope.Cli;

namespace DiScope.IntegrationTests;

/// <summary>
/// samples/LegacyWorker: classic Host.CreateDefaultBuilder, a Main that swallows every exception and calls
/// Environment.Exit, and EF Core 9 packages that are newer than the .NET 8 shared framework.
/// </summary>
public class LegacyWorkerTests
{
    [Fact]
    public async Task Captures_classic_host_and_finds_the_hosted_service_captive_dependency()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "DiScope.sln"))) root = Path.GetDirectoryName(root)!;
        var project = Path.Combine(root, "samples", "LegacyWorker", "LegacyWorker.csproj");

        var report = await new Analyzer(new CliOptions { Target = project }, _ => { }).RunAsync();

        Assert.Equal("HostBuilder", report.App.CapturedFrom);
        var d = Assert.Single(report.Diagnostics);
        Assert.Equal("DI001", d.Id);
        Assert.Equal("Singleton 'SyncWorker' captures scoped 'ShopContext' through SyncWorker → SyncService → ShopContext.", d.Message);
        Assert.StartsWith("Hosted services are always singletons", d.Help);
    }
}
