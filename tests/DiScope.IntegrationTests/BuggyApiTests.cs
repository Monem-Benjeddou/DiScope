using DiScope.Cli;
using DiScope.Model;

namespace DiScope.IntegrationTests;

/// <summary>
/// Runs the real pipeline (dotnet build → agent inside the app → rules) against samples/BuggyApi,
/// whose every DI mistake is labeled with the rule it should trigger.
/// </summary>
public class BuggyApiTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Sample = Path.Combine(Root, "samples", "BuggyApi", "BuggyApi.csproj");

    private static readonly Dictionary<string, Task<Report>> Cache = new();

    private static Task<Report> Analyze(string framework)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(framework, out var task))
                Cache[framework] = task = new Analyzer(new CliOptions { Target = Sample, Framework = framework }, _ => { }).RunAsync();
            return task;
        }
    }

    public static TheoryData<string> Frameworks => new() { "net8.0", "net10.0" };

    [Theory, MemberData(nameof(Frameworks))]
    public async Task Finds_exactly_the_planted_bugs(string framework)
    {
        var report = await Analyze(framework);

        var found = report.Diagnostics.Select(d => $"{d.Id} {d.Message}").ToList();
        string[] expected =
        {
            "DI001 Middleware 'RequestAuditMiddleware' depends on scoped 'IUserContext'",
            "DI001 Singleton 'MemoryCache' depends on scoped 'IOrderRepository'",
            "DI001 Singleton 'ReportService' captures scoped 'IUserContext' (HttpUserContext) through ReportService → ReportFormatter → HttpUserContext",
            "DI002 'OrdersController' needs 'IEmailSender'",
            "DI003 Circular dependency: AService → BService → AService",
            "DI004 Singleton 'SystemClock' holds transient 'IGuidGenerator'",
            "DI005 'INotifier' is registered 2 times (EmailNotifier, SmsNotifier)",
            "DI006 'IAuditLog' (AuditLog) is registered 2 times",
            "DI007 Nothing in the graph depends on 'ILegacyService'",
            "DI007 Nothing in the graph depends on 'IPaymentGateway'",
            "DI008 'IPaymentGateway' is created by a factory",
        };

        foreach (var e in expected) Assert.Contains(found, f => f.StartsWith(e, StringComparison.Ordinal));
        Assert.Equal(expected.Length, found.Count);
    }

    [Theory, MemberData(nameof(Frameworks))]
    public async Task Captures_app_info_and_framework_services(string framework)
    {
        var report = await Analyze(framework);

        Assert.Equal("BuggyApi", report.App.Assembly);
        Assert.Equal("Development", report.App.Environment);
        Assert.StartsWith(framework == "net8.0" ? ".NET 8." : ".NET 10.", report.App.Runtime);
        Assert.True(report.Services.Count(s => s.Origin == Origins.Framework) > 150);
        Assert.Equal(23, report.Summary.AppServices);
    }

    [Theory, MemberData(nameof(Frameworks))]
    public async Task Resolves_keyed_open_generic_enumerable_and_factory_dependencies(string framework)
    {
        var report = await Analyze(framework);
        var byId = report.Services.ToDictionary(s => s.Id);
        ServiceNode Service(string name) => report.Services.Single(s => s.ServiceType == "BuggyApi.Services." + name);
        string Target(ServiceNode node, string parameter) => byId[node.Dependencies.Single(d => d.Name == parameter).Targets.Single()].ImplementationType!;

        var pricing = Service("PricingService");
        Assert.Equal("keyed", pricing.Dependencies.Single(d => d.Name == "cache").Resolution);
        Assert.Equal("BuggyApi.Services.FastPriceCache", Target(pricing, "cache"));
        Assert.Equal("BuggyApi.Services.Repository<T>", Target(pricing, "products"));

        var reporter = Service("HealthReporter");
        var checks = reporter.Dependencies.Single();
        Assert.Equal("enumerable", checks.Resolution);
        Assert.Equal(2, checks.Targets.Count);

        var rates = Service("IExchangeRates");
        Assert.Equal("Factory", rates.Kind);
        Assert.Contains(rates.Dependencies, d => d.Type == "BuggyApi.Services.IClock" && d.Site == "factory" && d.Targets.Count == 1);
        Assert.Contains(rates.Dependencies, d => d.Type.StartsWith("Microsoft.Extensions.Logging.ILogger<", StringComparison.Ordinal));

        var orders = report.Services.Single(s => s.ServiceType == "BuggyApi.Controllers.OrdersController");
        Assert.Equal("Controller", orders.Kind);
        Assert.Equal("BuggyApi.Controllers.OrdersController", orders.SourceType);

        var middleware = report.Services.Single(s => s.Kind == "Middleware");
        Assert.Equal("builtin", middleware.Dependencies.Single(d => d.Name == "next").Resolution);
        Assert.Equal("invoke", middleware.Dependencies.Single(d => d.Name == "audit").Site);
    }

    [Fact]
    public async Task Class_library_is_rejected_with_a_helpful_message()
    {
        var core = Path.Combine(Root, "src", "DiScope.Core", "DiScope.Core.csproj");
        var ex = await Assert.ThrowsAsync<CliException>(() =>
            new Analyzer(new CliOptions { Target = core, NoBuild = true }, _ => { }).RunAsync());
        Assert.Contains("is a Library project", ex.Message);
    }

    [Fact]
    public async Task Folder_without_a_project_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<CliException>(() =>
            new Analyzer(new CliOptions { Target = Path.Combine(Root, "src") }, _ => { }).RunAsync());
        Assert.Contains("No project file", ex.Message);
    }

    private static string FindRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "DiScope.sln"))) return dir;
        throw new InvalidOperationException("DiScope.sln not found above the test output folder.");
    }
}
