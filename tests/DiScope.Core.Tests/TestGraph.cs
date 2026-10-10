using DiScope.Model;

namespace DiScope.Core.Tests;

/// <summary>Tiny DSL for hand-built graphs: <c>new TestGraph().Add("IFoo", "Foo", Scoped).DependsOn("IBar")</c>.</summary>
internal sealed class TestGraph
{
    private readonly RawGraph _graph = new() { App = new AppInfo { Assembly = "TestApp" } };
    private ServiceNode? _last;

    public RawGraph Graph => _graph;

    public TestGraph Add(string service, string? implementation, string lifetime,
        string kind = NodeKinds.Type, string origin = Origins.App, bool disposable = false, string? key = null)
    {
        _last = new ServiceNode
        {
            Id = (NodeKinds.IsRegistration(kind) ? "s" : "r") + _graph.Services.Count,
            ServiceType = "App." + service,
            ImplementationType = implementation is null ? null : "App." + implementation,
            Lifetime = lifetime,
            Kind = kind,
            Origin = origin,
            ServiceOrigin = origin,
            IsDisposable = disposable,
            Key = key,
            RegistrationIndex = NodeKinds.IsRegistration(kind) ? _graph.Services.Count : -1,
            DependenciesKnown = kind != NodeKinds.Factory,
        };
        _graph.Services.Add(_last);
        return this;
    }

    /// <summary>Depend on the last registration of <paramref name="service"/> (or nothing: missing).</summary>
    public TestGraph DependsOn(string service, string resolution = Resolutions.Service, string site = Sites.Constructor)
    {
        var targets = _graph.Services.Where(s => s.ServiceType == "App." + service && NodeKinds.IsRegistration(s.Kind)).ToList();
        var dep = new Dependency
        {
            Name = char.ToLowerInvariant(service.TrimStart('I')[0]) + service.TrimStart('I')[1..],
            Type = "App." + service,
            Site = site,
            Resolution = targets.Count == 0 && resolution == Resolutions.Service ? Resolutions.Missing : resolution,
        };
        if (dep.Resolution == Resolutions.Enumerable) dep.Targets.AddRange(targets.Select(t => t.Id));
        else if (dep.Resolution != Resolutions.Missing) dep.Targets.Add(targets[^1].Id);
        _last!.Dependencies.Add(dep);
        return this;
    }

    /// <summary>Add an edge to a service that is registered later (cycles).</summary>
    public TestGraph Link(string fromService, string toService)
    {
        var from = _graph.Services.Last(s => s.ServiceType == "App." + fromService);
        var to = _graph.Services.Last(s => s.ServiceType == "App." + toService);
        from.Dependencies.Add(new Dependency { Name = "x", Type = to.ServiceType, Resolution = Resolutions.Service, Targets = { to.Id } });
        return this;
    }
}
