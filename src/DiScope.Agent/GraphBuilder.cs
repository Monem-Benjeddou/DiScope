using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using DiScope.Model;

namespace DiScope.Agent;

/// <summary>One <c>ServiceDescriptor</c>, read through reflection so any Microsoft.Extensions.DependencyInjection version works.</summary>
internal sealed record Registration(
    int Index,
    Type ServiceType,
    string Lifetime,
    bool IsKeyed,
    object? Key,
    Type? ImplementationType,
    Delegate? Factory,
    object? Instance)
{
    public string Id => "s" + Index;
}

/// <summary>Turns a captured <c>IServiceCollection</c> into a <see cref="RawGraph"/>.</summary>
internal sealed class GraphBuilder
{
    private const string DiNamespace = "Microsoft.Extensions.DependencyInjection.";

    private static readonly HashSet<string> BuiltInServices = new(StringComparer.Ordinal)
    {
        "System.IServiceProvider",
        DiNamespace + "IServiceScopeFactory",
        DiNamespace + "IServiceProviderIsService",
        DiNamespace + "IServiceProviderIsKeyedService",
        DiNamespace + "IKeyedServiceProvider",
    };

    private static readonly HashSet<string> ResolveMethods = new(StringComparer.Ordinal)
    {
        "GetService", "GetRequiredService", "GetServices",
        "GetKeyedService", "GetRequiredKeyedService", "GetKeyedServices",
    };

    private readonly List<Registration> _registrations;
    private readonly Dictionary<Type, List<Registration>> _byService = new();
    private readonly HashSet<string> _appAssemblies;
    private readonly HashSet<Type> _registeredImplementations = new();

    private GraphBuilder(List<Registration> registrations, HashSet<string> appAssemblies)
    {
        _registrations = registrations;
        _appAssemblies = appAssemblies;
        foreach (var r in registrations)
        {
            if (!_byService.TryGetValue(r.ServiceType, out var list)) _byService[r.ServiceType] = list = new();
            list.Add(r);
            if (r.ImplementationType is not null) _registeredImplementations.Add(r.ImplementationType);
        }
    }

    public static RawGraph Build(object serviceCollection, Assembly app, HashSet<string> appAssemblies)
    {
        var registrations = ((IEnumerable)serviceCollection).Cast<object>().Select(ReadDescriptor).ToList();
        var builder = new GraphBuilder(registrations, appAssemblies);

        var graph = new RawGraph
        {
            App = new AppInfo
            {
                Assembly = app.GetName().Name ?? "",
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                              ?? "Production",
            },
        };

        foreach (var r in registrations) graph.Services.Add(builder.CreateNode(r));
        graph.Services.AddRange(builder.FindActivatedRoots());
        return graph;
    }

    // ---------------------------------------------------------------- descriptors

    private static Registration ReadDescriptor(object descriptor, int index)
    {
        var serviceType = (Type)Get(descriptor, "ServiceType")!;
        var lifetime = Get(descriptor, "Lifetime")?.ToString() ?? Lifetimes.Transient;
        // The non-keyed getters throw for keyed descriptors (and vice versa), so check first.
        var keyed = Get(descriptor, "IsKeyedService") is true;
        var prefix = keyed ? "Keyed" : "";
        return new Registration(
            index,
            serviceType,
            lifetime,
            keyed,
            keyed ? Get(descriptor, "ServiceKey") : null,
            Get(descriptor, prefix + "ImplementationType") as Type,
            Get(descriptor, prefix + "ImplementationFactory") as Delegate,
            Get(descriptor, prefix + "ImplementationInstance"));
    }

    private static object? Get(object target, string property)
    {
        var info = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (info is null) return null;
        try { return info.GetValue(target); }
        catch (TargetInvocationException) { return null; }
    }

    // ---------------------------------------------------------------- nodes

    private ServiceNode CreateNode(Registration r)
    {
        var node = new ServiceNode
        {
            Id = r.Id,
            ServiceType = TypeNames.Format(r.ServiceType),
            Lifetime = r.Lifetime,
            Key = r.IsKeyed ? KeyToString(r.Key) : null,
            IsOpenGeneric = r.ServiceType.IsGenericTypeDefinition,
            ServiceOrigin = Origin(r.ServiceType.Assembly),
            RegistrationIndex = r.Index,
        };

        Type? concrete = null;
        if (r.ImplementationType is not null)
        {
            node.Kind = NodeKinds.Type;
            concrete = r.ImplementationType;
            (node.Dependencies, node.Problem) = AnalyzeConstructor(concrete, Sites.Constructor);
            node.Origin = Origin(concrete.Assembly);
        }
        else if (r.Factory is not null)
        {
            node.Kind = NodeKinds.Factory;
            node.DependenciesKnown = false;
            node.Dependencies = AnalyzeFactory(r.Factory);
            // A lambda written in the app's Program.cs makes this the app's registration.
            node.Origin = Origin((r.Factory.Method.DeclaringType ?? r.ServiceType).Assembly);
        }
        else
        {
            node.Kind = NodeKinds.Instance;
            concrete = r.Instance?.GetType();
            node.Origin = Origin((concrete ?? r.ServiceType).Assembly);
        }

        if (concrete is not null)
        {
            node.ImplementationType = TypeNames.Format(concrete);
            node.IsDisposable = IsDisposable(concrete);
        }

        var main = concrete ?? r.ServiceType;
        node.ExtendsExternalType = ExtendsExternalType(r.ServiceType);
        node.Assembly = main.Assembly.GetName().Name;
        node.SourceType = TypeNames.SourceName(main);
        return node;
    }

    /// <summary>Base classes and interfaces from outside the app, ignoring System.* basics (IDisposable, IEquatable...).</summary>
    private bool ExtendsExternalType(Type type)
    {
        bool External(Type t)
        {
            if (t.IsGenericType) t = t.GetGenericTypeDefinition();
            return Origin(t.Assembly) != Origins.App && !(t.Namespace ?? "").StartsWith("System", StringComparison.Ordinal);
        }

        try
        {
            for (var b = type.BaseType; b is not null && b != typeof(object) && b != typeof(ValueType); b = b.BaseType)
                if (External(b)) return true;
            return type.GetInterfaces().Any(External);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDisposable(Type type) =>
        typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type);

    private string Origin(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        if (_appAssemblies.Contains(name)) return Origins.App;
        if (name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal)
            || name is "System" or "mscorlib" or "netstandard")
            return Origins.Framework;
        return Origins.Package;
    }

    private static string? KeyToString(object? key) => key switch
    {
        null => null,
        string s => s,
        Enum e => e.GetType().Name + "." + e,
        _ => key.ToString(),
    };

    // ---------------------------------------------------------------- constructors

    /// <summary>
    /// Mirrors Microsoft.Extensions.DependencyInjection: the longest public constructor whose
    /// parameters can all be resolved wins. If none can, report the longest one.
    /// </summary>
    private (List<Dependency>, string?) AnalyzeConstructor(Type type, string site)
    {
        if (type.IsAbstract || type.IsInterface)
            return (new(), $"'{TypeNames.Format(type)}' is abstract or an interface and cannot be constructed.");

        ConstructorInfo[] constructors;
        try
        {
            constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .ToArray();
        }
        catch (Exception ex)
        {
            return (new(), $"Could not inspect '{TypeNames.Format(type)}': {ex.Message}");
        }

        if (constructors.Length == 0)
            return (new(), $"'{TypeNames.Format(type)}' has no public constructor.");

        List<Dependency>? longest = null;
        foreach (var constructor in constructors)
        {
            var deps = constructor.GetParameters().Select(p => ResolveParameter(p, site)).ToList();
            longest ??= deps;
            if (deps.All(d => d.Resolution != Resolutions.Missing)) return (deps, null);
        }

        return (longest!, null);
    }

    private Dependency ResolveParameter(ParameterInfo parameter, string site)
    {
        var hasKey = false;
        object? key = null;
        foreach (var attribute in parameter.GetCustomAttributesData())
        {
            switch (attribute.AttributeType.FullName)
            {
                case DiNamespace + "ServiceKeyAttribute":
                    return new Dependency
                    {
                        Name = parameter.Name ?? "", Type = TypeNames.Format(parameter.ParameterType),
                        Resolution = Resolutions.BuiltIn, Site = site,
                    };
                case DiNamespace + "FromKeyedServicesAttribute":
                    hasKey = true;
                    key = attribute.ConstructorArguments.Count > 0 ? attribute.ConstructorArguments[0].Value : null;
                    break;
            }
        }

        return Resolve(parameter.Name ?? "", parameter.ParameterType, hasKey, key, parameter.HasDefaultValue, site);
    }

    private Dependency Resolve(string name, Type type, bool hasKey, object? key, bool optional, string site)
    {
        var dep = new Dependency { Name = name, Type = TypeNames.Format(type), Site = site };

        if (hasKey)
        {
            dep.Key = KeyToString(key);
            var keyed = FindLast(type, r => r.IsKeyed && Equals(r.Key, key));
            if (keyed is not null) return Target(dep, Resolutions.Keyed, keyed);
            dep.Resolution = optional ? Resolutions.Optional : Resolutions.Missing;
            return dep;
        }

        if (BuiltInServices.Contains(type.FullName ?? ""))
        {
            dep.Resolution = Resolutions.BuiltIn;
            return dep;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            dep.Resolution = Resolutions.Enumerable;
            dep.Targets = FindAll(type.GetGenericArguments()[0], r => !r.IsKeyed).Select(r => r.Id).ToList();
            return dep;
        }

        var single = FindLast(type, r => !r.IsKeyed);
        if (single is not null) return Target(dep, Resolutions.Service, single);

        dep.Resolution = optional ? Resolutions.Optional
            : type.IsGenericParameter ? Resolutions.Unknown
            : Resolutions.Missing;
        return dep;
    }

    private static Dependency Target(Dependency dep, string resolution, Registration target)
    {
        dep.Resolution = resolution;
        dep.Targets.Add(target.Id);
        return dep;
    }

    /// <summary>The registration DI would pick for a single <paramref name="type"/>: last closed one, else last open generic.</summary>
    private Registration? FindLast(Type type, Func<Registration, bool> filter)
    {
        if (!type.ContainsGenericParameters && _byService.TryGetValue(type, out var exact))
        {
            var match = exact.LastOrDefault(filter);
            if (match is not null) return match;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition
            && _byService.TryGetValue(type.GetGenericTypeDefinition(), out var open))
            return open.LastOrDefault(filter);

        return null;
    }

    /// <summary>Everything DI returns for <c>IEnumerable&lt;type&gt;</c>: closed and open generic registrations, in order.</summary>
    private IEnumerable<Registration> FindAll(Type type, Func<Registration, bool> filter)
    {
        IEnumerable<Registration> result = Enumerable.Empty<Registration>();
        if (!type.ContainsGenericParameters && _byService.TryGetValue(type, out var exact)) result = exact;
        if (type.IsGenericType && !type.IsGenericTypeDefinition
            && _byService.TryGetValue(type.GetGenericTypeDefinition(), out var open))
            result = result.Concat(open);
        return result.Where(filter).OrderBy(r => r.Index);
    }

    // ---------------------------------------------------------------- factories

    /// <summary>Best-effort: find <c>GetRequiredService&lt;T&gt;()</c>-style calls in the factory's IL (and one level of helpers).</summary>
    private List<Dependency> AnalyzeFactory(Delegate factory)
    {
        var deps = new List<Dependency>();
        try
        {
            ScanMethod(factory.Method, deps, new HashSet<MethodBase>(), depth: 0);
        }
        catch
        {
            // IL inspection is a bonus; never fail the analysis because of it.
        }

        return deps
            .GroupBy(d => (d.Name, d.Type, d.Key))
            .Select(g => g.First())
            .ToList();
    }

    private void ScanMethod(MethodInfo method, List<Dependency> deps, HashSet<MethodBase> visited, int depth)
    {
        if (!visited.Add(method)) return;
        var module = method.Module;
        var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        // A service key is only known when it's a string literal pushed right before the call.
        string? keyLiteral = null;
        Type? lastToken = null;

        foreach (var instruction in ILReader.ReadInstructions(method))
        {
            var previousKey = keyLiteral;
            keyLiteral = null;

            if (instruction.OpCode == OpCodes.Ldstr)
            {
                keyLiteral = SafeResolve(() => module.ResolveString(instruction.Token));
                continue;
            }

            if (instruction.OpCode == OpCodes.Ldtoken)
            {
                lastToken = SafeResolve(() => module.ResolveType(instruction.Token, typeArgs, methodArgs));
                continue;
            }

            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
            if (SafeResolve(() => module.ResolveMethod(instruction.Token, typeArgs, methodArgs)) is not MethodInfo called) continue;

            if (ResolveMethods.Contains(called.Name) && IsServiceProviderApi(called))
            {
                var resolved = called.IsGenericMethod ? called.GetGenericArguments()[0] : lastToken;
                if (resolved is null) continue;
                var keyed = called.Name.Contains("Keyed", StringComparison.Ordinal);
                var many = called.Name.EndsWith("Services", StringComparison.Ordinal);
                var optional = called.Name.StartsWith("GetKeyedService", StringComparison.Ordinal) && !many
                               || called.Name == "GetService";
                var requested = many ? typeof(IEnumerable<>).MakeGenericType(resolved) : resolved;
                var label = called.IsGenericMethod ? $"{called.Name}<{TypeNames.Format(resolved)}>()" : called.Name + "(typeof)";
                lastToken = null;
                if (keyed && previousKey is null)
                {
                    // Key computed at runtime: we can't tell which registration it picks.
                    deps.Add(new Dependency { Name = label, Type = TypeNames.Format(requested), Resolution = Resolutions.Unknown, Site = Sites.Factory });
                    continue;
                }

                deps.Add(Resolve(label, requested, keyed, keyed ? previousKey : null, optional, Sites.Factory));
            }
            else if (called.Name == "CreateInstance" && called.IsGenericMethod
                     && called.DeclaringType?.FullName == DiNamespace + "ActivatorUtilities")
            {
                var (ctorDeps, _) = AnalyzeConstructor(called.GetGenericArguments()[0], Sites.Factory);
                deps.AddRange(ctorDeps.Where(d => d.Resolution != Resolutions.Missing));
            }
            else if (depth < 2 && called.DeclaringType is { } declaring
                     && _appAssemblies.Contains(declaring.Assembly.GetName().Name ?? "")
                     && !called.IsAbstract)
            {
                ScanMethod(called, deps, visited, depth + 1);
            }
        }
    }

    private static bool IsServiceProviderApi(MethodInfo method)
    {
        var declaring = method.DeclaringType?.FullName ?? "";
        return declaring is "System.IServiceProvider"
            || declaring.StartsWith(DiNamespace, StringComparison.Ordinal);
    }

    private static T? SafeResolve<T>(Func<T?> resolve) where T : class
    {
        try { return resolve(); }
        catch { return null; }
    }

    // ---------------------------------------------------------------- activated roots

    /// <summary>
    /// Controllers, hubs, Razor Pages and conventional middleware consume DI without being registered.
    /// Adding them as nodes catches missing registrations in their constructors.
    /// </summary>
    private IEnumerable<ServiceNode> FindActivatedRoots()
    {
        var counter = 0;
        foreach (var assembly in LoadAppAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters) continue;
                if (!type.IsPublic && !type.IsNestedPublic) continue;
                if (_registeredImplementations.Contains(type)) continue;

                var (kind, lifetime) = ClassifyRoot(type);
                if (kind is null) continue;

                var node = new ServiceNode
                {
                    Id = "r" + counter++,
                    ServiceType = TypeNames.Format(type),
                    ImplementationType = TypeNames.Format(type),
                    Kind = kind,
                    Lifetime = lifetime,
                    Origin = Origins.App,
                    ServiceOrigin = Origins.App,
                    Assembly = assembly.GetName().Name,
                    SourceType = TypeNames.SourceName(type),
                    IsDisposable = IsDisposable(type),
                };

                (node.Dependencies, node.Problem) = AnalyzeConstructor(type, Sites.Constructor);
                if (kind == NodeKinds.Middleware) AdjustMiddleware(type, node);
                yield return node;
            }
        }
    }

    private static (string? Kind, string Lifetime) ClassifyRoot(Type type)
    {
        if (InheritsFrom(type, "Microsoft.AspNetCore.Mvc.ControllerBase")
            || HasAttribute(type, "Microsoft.AspNetCore.Mvc.ApiControllerAttribute"))
            return (NodeKinds.Controller, Lifetimes.Transient);
        if (InheritsFrom(type, "Microsoft.AspNetCore.SignalR.Hub"))
            return (NodeKinds.Hub, Lifetimes.Transient);
        if (InheritsFrom(type, "Microsoft.AspNetCore.Mvc.RazorPages.PageModel"))
            return (NodeKinds.PageModel, Lifetimes.Transient);
        // Conventional middleware is created once by UseMiddleware<T>(): effectively a singleton.
        if (InvokeMethod(type) is not null && !Implements(type, "Microsoft.AspNetCore.Http.IMiddleware"))
            return (NodeKinds.Middleware, Lifetimes.Singleton);
        return (null, "");
    }

    private void AdjustMiddleware(Type type, ServiceNode node)
    {
        foreach (var dep in node.Dependencies)
        {
            // `next` is supplied by the pipeline; other unresolved arguments may be passed to UseMiddleware<T>(args).
            if (dep.Type == "Microsoft.AspNetCore.Http.RequestDelegate") dep.Resolution = Resolutions.BuiltIn;
            else if (dep.Resolution == Resolutions.Missing) dep.Resolution = Resolutions.Unknown;
        }

        var invoke = InvokeMethod(type)!;
        // Invoke/InvokeAsync parameters after HttpContext are resolved per request from the request scope.
        node.Dependencies.AddRange(invoke.GetParameters().Skip(1).Select(p => ResolveParameter(p, Sites.Invoke)));
    }

    private static MethodInfo? InvokeMethod(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name is "Invoke" or "InvokeAsync"
                                 && m.GetParameters() is { Length: > 0 } ps
                                 && ps[0].ParameterType.FullName == "Microsoft.AspNetCore.Http.HttpContext"
                                 && typeof(Task).IsAssignableFrom(m.ReturnType));

    private static bool InheritsFrom(Type type, string baseFullName)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.FullName == baseFullName) return true;
        return false;
    }

    private static bool Implements(Type type, string interfaceFullName) =>
        type.GetInterfaces().Any(i => i.FullName == interfaceFullName);

    private static bool HasAttribute(Type type, string attributeFullName)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.GetCustomAttributesData().Any(a => a.AttributeType.FullName == attributeFullName)) return true;
        return false;
    }

    private IEnumerable<Assembly> LoadAppAssemblies()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && _appAssemblies.Contains(a.GetName().Name ?? ""))
            .ToDictionary(a => a.GetName().Name!, StringComparer.Ordinal);

        foreach (var name in _appAssemblies)
        {
            if (loaded.ContainsKey(name)) continue;
            try { loaded[name] = Assembly.Load(new AssemblyName(name)); }
            catch { /* not every project reference is loadable on its own */ }
        }

        return loaded.Values;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
        catch { return Type.EmptyTypes; }
    }
}
