// Shared between DiScope.Agent (which produces the graph) and DiScope.Core (which analyzes it).
// Keep this file free of dependencies beyond the BCL.

namespace DiScope.Model;

#if DISCOPE_AGENT
internal
#else
public
#endif
sealed class RawGraph
{
    public int SchemaVersion { get; set; } = 1;
    public AppInfo App { get; set; } = new();
    public List<ServiceNode> Services { get; set; } = new();
}

#if DISCOPE_AGENT
internal
#else
public
#endif
sealed class AppInfo
{
    public string Assembly { get; set; } = "";
    public string? Environment { get; set; }
    public string? Runtime { get; set; }

    /// <summary>The builder type the services were captured from, e.g. HostApplicationBuilder.</summary>
    public string? CapturedFrom { get; set; }
}

#if DISCOPE_AGENT
internal
#else
public
#endif
sealed class ServiceNode
{
    public string Id { get; set; } = "";
    public string ServiceType { get; set; } = "";
    public string? ImplementationType { get; set; }

    /// <summary>Singleton, Scoped or Transient.</summary>
    public string Lifetime { get; set; } = "";

    /// <summary>See <see cref="NodeKinds"/>.</summary>
    public string Kind { get; set; } = "";

    public string? Key { get; set; }
    public bool IsOpenGeneric { get; set; }
    public bool IsDisposable { get; set; }

    /// <summary>Where the implementation comes from: app, package or framework. See <see cref="Origins"/>.</summary>
    public string Origin { get; set; } = "";

    /// <summary>Where the service (contract) type comes from.</summary>
    public string ServiceOrigin { get; set; } = "";

    public string? Assembly { get; set; }

    /// <summary>Position in the IServiceCollection, or -1 for activated roots such as controllers.</summary>
    public int RegistrationIndex { get; set; } = -1;

    /// <summary>Type to navigate to in an editor (CLR full name, no generic arity).</summary>
    public string? SourceType { get; set; }

    public List<Dependency> Dependencies { get; set; } = new();

    /// <summary>False when dependencies could not be fully determined (e.g. factory registrations).</summary>
    public bool DependenciesKnown { get; set; } = true;

    /// <summary>Why the service cannot be constructed, if it cannot.</summary>
    public string? Problem { get; set; }
}

#if DISCOPE_AGENT
internal
#else
public
#endif
sealed class Dependency
{
    /// <summary>Constructor parameter name, or the call used inside a factory.</summary>
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";

    /// <summary>See <see cref="Resolutions"/>.</summary>
    public string Resolution { get; set; } = "";

    /// <summary>See <see cref="Sites"/>.</summary>
    public string Site { get; set; } = Sites.Constructor;

    public string? Key { get; set; }
    public List<string> Targets { get; set; } = new();
}

#if DISCOPE_AGENT
internal
#else
public
#endif
static class Lifetimes
{
    public const string Singleton = "Singleton";
    public const string Scoped = "Scoped";
    public const string Transient = "Transient";
}

#if DISCOPE_AGENT
internal
#else
public
#endif
static class NodeKinds
{
    public const string Type = "Type";
    public const string Factory = "Factory";
    public const string Instance = "Instance";

    // Activated roots: consume DI but are not registered in it.
    public const string Controller = "Controller";
    public const string Hub = "Hub";
    public const string PageModel = "PageModel";
    public const string Middleware = "Middleware";

    public static bool IsRegistration(string kind) => kind is Type or Factory or Instance;
}

#if DISCOPE_AGENT
internal
#else
public
#endif
static class Origins
{
    public const string App = "app";
    public const string Package = "package";
    public const string Framework = "framework";
}

#if DISCOPE_AGENT
internal
#else
public
#endif
static class Resolutions
{
    public const string Service = "service";
    public const string Enumerable = "enumerable";
    public const string Keyed = "keyed";
    public const string BuiltIn = "builtin";
    public const string Optional = "optional";
    public const string Missing = "missing";
    public const string Unknown = "unknown";
}

#if DISCOPE_AGENT
internal
#else
public
#endif
static class Sites
{
    public const string Constructor = "ctor";
    public const string Invoke = "invoke";
    public const string Factory = "factory";
}
