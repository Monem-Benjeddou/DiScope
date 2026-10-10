using DiScope.Analysis;
using DiScope.Model;
using static DiScope.Model.Lifetimes;

namespace DiScope.Core.Tests;

public class DiagnosticRulesTests
{
    private static List<Diagnostic> Run(TestGraph graph) => DiagnosticRules.Run(graph.Graph);

    private static Diagnostic Single(List<Diagnostic> diagnostics, string id) =>
        Assert.Single(diagnostics, d => d.Id == id);

    [Fact]
    public void Healthy_graph_has_no_diagnostics()
    {
        var graph = new TestGraph()
            .Add("IRepo", "Repo", Scoped)
            .Add("IService", "Service", Scoped).DependsOn("IRepo")
            .Add("IClock", "Clock", Singleton)
            .Add("OrdersController", "OrdersController", Transient, NodeKinds.Controller).DependsOn("IService").DependsOn("IClock");

        Assert.Empty(Run(graph));
    }

    [Fact]
    public void Singleton_depending_on_scoped_is_captive()
    {
        var graph = new TestGraph()
            .Add("IRepo", "Repo", Scoped)
            .Add("ICache", "Cache", Singleton).DependsOn("IRepo")
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("ICache");

        var d = Single(Run(graph), RuleIds.Captive);
        Assert.Equal(Severities.Error, d.Severity);
        Assert.Equal(new[] { "s1", "s0" }, d.Path);
        Assert.Contains("'Cache' depends on scoped 'IRepo' (Repo)", d.Message);
    }

    [Fact]
    public void Captive_dependency_is_found_through_transients()
    {
        var graph = new TestGraph()
            .Add("IUser", "User", Scoped)
            .Add("IFormatter", "Formatter", Transient).DependsOn("IUser")
            .Add("IReport", "Report", Singleton).DependsOn("IFormatter")
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IReport");

        var diagnostics = Run(graph);
        var d = Single(diagnostics, RuleIds.Captive);
        Assert.Equal(new[] { "s2", "s1", "s0" }, d.Path);
        Assert.Contains("Report → Formatter → User", d.Message);
        // The transient edge is part of the captive path, so DI004 doesn't repeat it.
        Assert.DoesNotContain(diagnostics, x => x.Id == RuleIds.TransientInSingleton);
    }

    [Fact]
    public void Captive_search_stops_at_other_singletons()
    {
        var graph = new TestGraph()
            .Add("IRepo", "Repo", Scoped)
            .Add("IInner", "Inner", Singleton).DependsOn("IRepo")
            .Add("IOuter", "Outer", Singleton).DependsOn("IInner")
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IOuter");

        var d = Single(Run(graph), RuleIds.Captive);
        Assert.Equal(new[] { "s1", "s0" }, d.Path); // reported once, on Inner
    }

    [Fact]
    public void Scoped_in_middleware_constructor_is_captive_but_invoke_parameters_are_fine()
    {
        var graph = new TestGraph()
            .Add("IUser", "User", Scoped)
            .Add("IAudit", "Audit", Scoped)
            .Add("AuditMiddleware", "AuditMiddleware", Singleton, NodeKinds.Middleware)
            .DependsOn("IUser")
            .DependsOn("IAudit", site: Sites.Invoke);

        var d = Single(Run(graph), RuleIds.Captive);
        Assert.Equal(new[] { "r2", "s0" }, d.Path);
        Assert.Contains("InvokeAsync", d.Help);
    }

    [Fact]
    public void Framework_only_issues_are_ignored()
    {
        var graph = new TestGraph()
            .Add("IFrameworkScoped", "FrameworkScoped", Scoped, origin: Origins.Framework)
            .Add("IFrameworkSingleton", "FrameworkSingleton", Singleton, origin: Origins.Framework).DependsOn("IFrameworkScoped")
            .Add("IFrameworkOther", "FrameworkOther", Singleton, origin: Origins.Framework).DependsOn("INotRegistered");

        Assert.Empty(Run(graph));
    }

    [Fact]
    public void Unregistered_constructor_parameter_is_missing()
    {
        var graph = new TestGraph()
            .Add("OrdersController", "OrdersController", Transient, NodeKinds.Controller).DependsOn("IEmailSender");

        var d = Single(Run(graph), RuleIds.Missing);
        Assert.Equal("App.IEmailSender", d.MissingType);
        Assert.Contains("'OrdersController' needs 'IEmailSender' (constructor parameter 'emailSender')", d.Message);
    }

    [Fact]
    public void Missing_dependencies_inferred_from_library_factories_are_not_reported()
    {
        var graph = new TestGraph()
            .Add("DiagnosticContext", null, Singleton, NodeKinds.Factory, origin: Origins.Package)
            .DependsOn("RegisteredLogger", site: Sites.Factory)
            .Add("AppFactory", null, Singleton, NodeKinds.Factory)
            .DependsOn("IReallyMissing", site: Sites.Factory);

        var d = Single(Run(graph), RuleIds.Missing);
        Assert.Contains("IReallyMissing", d.Message);
    }

    [Fact]
    public void Two_services_depending_on_each_other_are_a_cycle()
    {
        var graph = new TestGraph()
            .Add("IA", "A", Scoped)
            .Add("IB", "B", Scoped).DependsOn("IA")
            .Link("IA", "IB")
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IA");

        var d = Single(Run(graph), RuleIds.Cycle);
        Assert.Equal(new[] { "s0", "s1", "s0" }, d.Path);
        Assert.Equal("Circular dependency: A → B → A.", d.Message);
    }

    [Fact]
    public void Decorator_resolving_itself_is_a_self_loop()
    {
        var graph = new TestGraph()
            .Add("IStore", "CachingStore", Scoped)
            .Link("IStore", "IStore")
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IStore");

        var d = Single(Run(graph), RuleIds.Cycle);
        Assert.Contains("depends on itself", d.Message);
    }

    [Fact]
    public void Transient_in_singleton_is_info_unless_disposable()
    {
        var graph = new TestGraph()
            .Add("IIds", "Ids", Transient)
            .Add("IConnection", "Connection", Transient, disposable: true)
            .Add("IClock", "Clock", Singleton).DependsOn("IIds").DependsOn("IConnection")
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IClock");

        var diagnostics = Run(graph).Where(d => d.Id == RuleIds.TransientInSingleton).ToList();
        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Severity == Severities.Warning && d.Message.Contains("Connection"));
        Assert.Contains(diagnostics, d => d.Severity == Severities.Info && d.Message.Contains("Ids"));
    }

    [Fact]
    public void Single_injection_of_a_multiply_registered_service_is_shadowed()
    {
        var graph = new TestGraph()
            .Add("INotifier", "EmailNotifier", Scoped)
            .Add("INotifier", "SmsNotifier", Scoped)
            .Add("IOrders", "Orders", Scoped).DependsOn("INotifier")
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IOrders");

        var d = Single(Run(graph), RuleIds.Shadowed);
        Assert.Contains("only the last one ('SmsNotifier') is used", d.Message);
        Assert.Contains("'Orders' injects", d.Message);
    }

    [Fact]
    public void Replacing_a_framework_default_is_not_shadowing()
    {
        // e.g. the Blazor template replacing ServerAuthenticationStateProvider.
        var graph = new TestGraph()
            .Add("AuthStateProvider", "ServerAuthStateProvider", Scoped, origin: Origins.Framework)
            .Add("AuthStateProvider", "RevalidatingAuthStateProvider", Scoped)
            .Add("Supplier", "Supplier", Scoped, origin: Origins.Framework).DependsOn("AuthStateProvider");

        Assert.DoesNotContain(Run(graph), d => d.Id == RuleIds.Shadowed);
    }

    [Fact]
    public void App_registrations_replacing_each_other_are_reported_even_without_constructor_consumers()
    {
        // e.g. three modules each registering their own OutboxPersistence factory.
        var graph = new TestGraph()
            .Add("Outbox", null, Scoped, NodeKinds.Factory)
            .Add("Outbox", null, Scoped, NodeKinds.Factory)
            .Add("Outbox", null, Scoped, NodeKinds.Factory);

        var diagnostics = Run(graph);
        var d = Single(diagnostics, RuleIds.Shadowed);
        Assert.Equal(Severities.Info, d.Severity);
        Assert.Contains("registered 3 times (factory #0, factory #1, factory #2)", d.Message);
        Assert.Contains("only the last one ('factory #2')", d.Message);
        Assert.DoesNotContain(diagnostics, x => x.Id == RuleIds.Unused);
    }

    [Fact]
    public void Multiple_registrations_consumed_as_enumerable_are_fine()
    {
        var graph = new TestGraph()
            .Add("ICheck", "DbCheck", Singleton)
            .Add("ICheck", "DiskCheck", Singleton)
            .Add("IReporter", "Reporter", Singleton).DependsOn("ICheck", Resolutions.Enumerable)
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IReporter");

        Assert.Empty(Run(graph));
    }

    [Fact]
    public void Identical_registrations_are_duplicates_not_shadowed()
    {
        var graph = new TestGraph()
            .Add("IAudit", "Audit", Scoped)
            .Add("IAudit", "Audit", Scoped)
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IAudit");

        var diagnostics = Run(graph);
        Assert.Contains("registered 2 times as scoped", Single(diagnostics, RuleIds.Duplicate).Message);
        Assert.DoesNotContain(diagnostics, d => d.Id == RuleIds.Shadowed);
        Assert.DoesNotContain(diagnostics, d => d.Id == RuleIds.Unused); // the shadowed copy isn't "unused"
    }

    [Fact]
    public void Duplicates_added_by_a_library_are_ignored()
    {
        var graph = new TestGraph()
            .Add("IPostConfigureOptions", "Configurator", Transient, origin: Origins.Package)
            .Add("IPostConfigureOptions", "Configurator", Transient, origin: Origins.Package);

        Assert.DoesNotContain(Run(graph), d => d.Id == RuleIds.Duplicate);
    }

    [Fact]
    public void Identical_findings_on_several_registrations_are_merged()
    {
        var graph = new TestGraph()
            .Add("ILifecycleParticipant", "StorageParticipant", Singleton, origin: Origins.Package).DependsOn("IGrainStorage")
            .Add("ILifecycleParticipant", "StorageParticipant", Singleton, origin: Origins.Package).DependsOn("IGrainStorage");

        var d = Single(Run(graph), RuleIds.Missing);
        Assert.Equal(new[] { "s0", "s1" }, d.Nodes);
    }

    [Fact]
    public void App_service_nobody_depends_on_is_possibly_unused()
    {
        var graph = new TestGraph().Add("ILegacy", "Legacy", Transient);

        var d = Single(Run(graph), RuleIds.Unused);
        Assert.Equal(Severities.Info, d.Severity);
    }

    [Fact]
    public void Services_extending_library_types_are_not_unused()
    {
        // e.g. FluentValidation registering each validator as its own type.
        var graph = new TestGraph().Add("CreateOrderValidator", "CreateOrderValidator", Scoped);
        graph.Graph.Services[0].ExtendsExternalType = true;

        Assert.Empty(Run(graph));
    }

    [Fact]
    public void App_implementation_of_a_framework_contract_is_not_unused()
    {
        var graph = new TestGraph().Add("IHostedService", "Worker", Singleton);
        graph.Graph.Services[0].ServiceOrigin = Origins.Framework;

        Assert.Empty(Run(graph));
    }

    [Fact]
    public void Factory_without_visible_dependencies_is_reported_once_its_dependencies_are_unknown()
    {
        var graph = new TestGraph()
            .Add("IGateway", null, Scoped, NodeKinds.Factory)
            .Add("IRates", null, Singleton, NodeKinds.Factory).DependsOn("IGateway", site: Sites.Factory)
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IRates");

        var diagnostics = Run(graph);
        var d = Single(diagnostics, RuleIds.OpaqueFactory);
        Assert.Equal(new[] { "s0" }, d.Nodes);
        // A singleton factory resolving a scoped service is captive too.
        Assert.Equal(new[] { "s1", "s0" }, Single(diagnostics, RuleIds.Captive).Path);
    }

    [Fact]
    public void Problem_on_a_node_is_not_constructible()
    {
        var graph = new TestGraph().Add("IThing", "AbstractThing", Scoped).Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IThing");
        graph.Graph.Services[0].Problem = "'App.AbstractThing' is abstract or an interface and cannot be constructed.";

        var d = Single(Run(graph), RuleIds.NotConstructible);
        Assert.Equal("'AbstractThing' is abstract or an interface and cannot be constructed.", d.Message);
    }

    [Fact]
    public void Diagnostics_are_sorted_by_severity()
    {
        var graph = new TestGraph()
            .Add("ILegacy", "Legacy", Transient)
            .Add("C", "C", Transient, NodeKinds.Controller).DependsOn("IMissing");

        var severities = Run(graph).Select(d => d.Severity).ToList();
        Assert.Equal(new[] { Severities.Error, Severities.Info }, severities);
    }

    [Theory]
    [InlineData("App.IRepository<App.Order>", "IRepository<Order>")]
    [InlineData("System.Collections.Generic.IEnumerable<App.Models.IHealthCheck>", "IEnumerable<IHealthCheck>")]
    [InlineData("Microsoft.Extensions.Logging.ILogger<App.Services.OrderService>", "ILogger<OrderService>")]
    [InlineData("App.Outer.Inner", "Inner")]
    [InlineData("int", "int")]
    public void Type_names_are_shortened(string full, string expected) => Assert.Equal(expected, TypeDisplay.Short(full));
}
