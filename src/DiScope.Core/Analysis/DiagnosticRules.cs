using DiScope.Model;

namespace DiScope.Analysis;

/// <summary>Runs every rule (DI001–DI009) over a captured graph.</summary>
public static class DiagnosticRules
{
    public static List<Diagnostic> Run(RawGraph graph)
    {
        var index = new GraphIndex(graph);
        var diagnostics = new List<Diagnostic>();

        CaptiveDependencies(index, diagnostics);
        MissingRegistrations(index, diagnostics);
        Cycles(index, diagnostics);
        TransientsInSingletons(index, diagnostics);
        ShadowedRegistrations(index, diagnostics);
        DuplicateRegistrations(index, diagnostics);
        UnusedRegistrations(index, diagnostics);
        OpaqueFactories(index, diagnostics);
        NotConstructible(index, diagnostics);

        return Merge(diagnostics)
            .OrderBy(d => Severities.Rank(d.Severity))
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Several registrations of the same type can produce the very same finding (e.g. a library registering a
    /// lifecycle participant once per provider). Report it once, highlighting every node.
    /// </summary>
    private static IEnumerable<Diagnostic> Merge(List<Diagnostic> diagnostics) =>
        diagnostics
            .GroupBy(d => (d.Id, d.Severity, d.Message))
            .Select(g =>
            {
                var first = g.First();
                first.Nodes = g.SelectMany(d => d.Nodes).Distinct().ToList();
                return first;
            });

    /// <summary>Edges that are part of constructing the consumer (not per-request Invoke parameters).</summary>
    private static bool IsConstructionEdge(GraphIndex.Edge edge) => edge.Dependency.Site != Sites.Invoke;

    // ---------------------------------------------------------------- DI001

    /// <summary>
    /// A singleton (or conventional middleware) that reaches a scoped service, directly or through
    /// transients. Transients created for a singleton live as long as it does, so the scoped service is captured.
    /// </summary>
    private static void CaptiveDependencies(GraphIndex index, List<Diagnostic> diagnostics)
    {
        foreach (var root in index.Nodes.Where(n => n.Lifetime == Lifetimes.Singleton && n.Kind != NodeKinds.Instance))
        {
            var parents = new Dictionary<string, string> { [root.Id] = "" };
            var queue = new Queue<ServiceNode>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var edge in index.Outgoing(current).Where(IsConstructionEdge))
                {
                    var target = edge.To;
                    if (parents.ContainsKey(target.Id)) continue;
                    parents[target.Id] = current.Id;

                    if (target.Lifetime == Lifetimes.Scoped)
                    {
                        var path = BuildPath(parents, target.Id);
                        var nodes = path.Select(id => index.Find(id)!).ToList();
                        if (nodes.Any(GraphIndex.IsUserCode)) diagnostics.Add(Captive(root, target, nodes));
                    }
                    else if (target.Lifetime == Lifetimes.Transient)
                    {
                        queue.Enqueue(target);
                    }
                    // Singletons are checked as roots of their own.
                }
            }
        }
    }

    private static Diagnostic Captive(ServiceNode root, ServiceNode scoped, List<ServiceNode> path)
    {
        var rootKind = root.Kind == NodeKinds.Middleware ? "Middleware" : "Singleton";
        var message = path.Count == 2
            ? $"{rootKind} '{GraphIndex.Name(root)}' depends on scoped {GraphIndex.Label(scoped)}. " +
              "The scoped instance is captured for the lifetime of the app and shared by every request."
            : $"{rootKind} '{GraphIndex.Name(root)}' captures scoped {GraphIndex.Label(scoped)} through " +
              string.Join(" → ", path.Select(GraphIndex.Name)) + ".";

        var help = root.Kind == NodeKinds.Middleware
            ? $"Move the '{TypeDisplay.Short(path[1].ServiceType)}' parameter from the constructor to InvokeAsync(HttpContext, ...), which runs per request."
            : root.ServiceType == "Microsoft.Extensions.Hosting.IHostedService"
                ? "Hosted services are always singletons: inject IServiceScopeFactory and create a scope for each unit of work " +
                  "(using var scope = scopeFactory.CreateScope();)."
                : $"Register '{GraphIndex.Name(root)}' as scoped, make the dependency a singleton if it is stateless, " +
                  "or inject IServiceScopeFactory and create a scope when you need it.";

        return new Diagnostic
        {
            Id = RuleIds.Captive,
            Severity = Severities.Error,
            Title = "Captive dependency",
            Message = message,
            Help = help,
            Nodes = path.Select(n => n.Id).ToList(),
            Path = path.Select(n => n.Id).ToList(),
        };
    }

    private static List<string> BuildPath(Dictionary<string, string> parents, string last)
    {
        var path = new List<string>();
        for (var id = last; id.Length > 0; id = parents[id]) path.Add(id);
        path.Reverse();
        return path;
    }

    // ---------------------------------------------------------------- DI002

    private static void MissingRegistrations(GraphIndex index, List<Diagnostic> diagnostics)
    {
        foreach (var node in index.Nodes.Where(GraphIndex.IsUserCode))
        foreach (var dep in node.Dependencies.Where(d => d.Resolution == Resolutions.Missing))
        {
            // Factory dependencies are read from every branch of the factory's IL. Inside a library, a branch the
            // app never takes (e.g. Serilog's "logger was passed in" path) would look like a missing registration.
            if (dep.Site == Sites.Factory && node.Origin != Origins.App) continue;

            var keyText = dep.Key is null ? "" : $" with key \"{dep.Key}\"";
            var where = dep.Site switch
            {
                Sites.Invoke => $"its InvokeAsync parameter '{dep.Name}'",
                Sites.Factory => $"its factory ({TypeDisplay.Short(dep.Name)})",
                _ => $"constructor parameter '{dep.Name}'",
            };

            diagnostics.Add(new Diagnostic
            {
                Id = RuleIds.Missing,
                Severity = Severities.Error,
                Title = "Missing registration",
                Message = $"'{GraphIndex.Name(node)}' needs '{TypeDisplay.Short(dep.Type)}'{keyText} ({where}), but it is not registered.",
                Help = dep.Key is null
                    ? $"Register it, for example: services.AddScoped<{TypeDisplay.Short(dep.Type)}, ...>();"
                    : $"Register it, for example: services.AddKeyedScoped<{TypeDisplay.Short(dep.Type)}, ...>(\"{dep.Key}\");",
                Nodes = { node.Id },
                MissingType = dep.Type,
            });
        }
    }

    // ---------------------------------------------------------------- DI003

    /// <summary>Tarjan's strongly connected components; every component with a loop is a cycle.</summary>
    private static void Cycles(GraphIndex index, List<Diagnostic> diagnostics)
    {
        var counter = 0;
        var indices = new Dictionary<string, int>();
        var lowLinks = new Dictionary<string, int>();
        var stack = new Stack<ServiceNode>();
        var onStack = new HashSet<string>();

        // Iterative to stay safe on deep graphs.
        foreach (var start in index.Nodes)
        {
            if (indices.ContainsKey(start.Id)) continue;
            var work = new Stack<(ServiceNode Node, IEnumerator<GraphIndex.Edge> Edges)>();
            Visit(start);

            while (work.Count > 0)
            {
                var (node, edges) = work.Peek();
                if (edges.MoveNext())
                {
                    var next = edges.Current.To;
                    if (!indices.ContainsKey(next.Id)) Visit(next);
                    else if (onStack.Contains(next.Id)) lowLinks[node.Id] = Math.Min(lowLinks[node.Id], indices[next.Id]);
                    continue;
                }

                work.Pop();
                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    lowLinks[parent.Id] = Math.Min(lowLinks[parent.Id], lowLinks[node.Id]);
                }

                if (lowLinks[node.Id] != indices[node.Id]) continue;

                var component = new List<ServiceNode>();
                ServiceNode member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member.Id);
                    component.Add(member);
                } while (member.Id != node.Id);

                var selfLoop = component.Count == 1
                               && index.Outgoing(node).Any(e => IsConstructionEdge(e) && e.To.Id == node.Id);
                if ((component.Count > 1 || selfLoop) && component.Any(GraphIndex.IsUserCode))
                    diagnostics.Add(Cycle(index, component));
            }

            void Visit(ServiceNode node)
            {
                indices[node.Id] = lowLinks[node.Id] = counter++;
                stack.Push(node);
                onStack.Add(node.Id);
                work.Push((node, index.Outgoing(node).Where(IsConstructionEdge).GetEnumerator()));
            }
        }
    }

    private static Diagnostic Cycle(GraphIndex index, List<ServiceNode> component)
    {
        var members = component.Select(n => n.Id).ToHashSet();
        var start = component.OrderBy(n => n.RegistrationIndex < 0 ? int.MaxValue : n.RegistrationIndex).First();
        var path = FindLoop(index, start, members);
        var names = path.Select(id => GraphIndex.Name(index.Find(id)!));

        return new Diagnostic
        {
            Id = RuleIds.Cycle,
            Severity = Severities.Error,
            Title = "Circular dependency",
            Message = path.Count == 2
                ? $"'{GraphIndex.Name(start)}' depends on itself. If it is a decorator, register the inner service separately."
                : "Circular dependency: " + string.Join(" → ", names) + ".",
            Help = "Break the cycle: extract the shared logic into a third service, or depend on an event/mediator instead of each other.",
            Nodes = members.ToList(),
            Path = path,
        };
    }

    /// <summary>Shortest path from <paramref name="start"/> back to itself, staying inside the component.</summary>
    private static List<string> FindLoop(GraphIndex index, ServiceNode start, HashSet<string> members)
    {
        var parents = new Dictionary<string, string>();
        var queue = new Queue<ServiceNode>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in index.Outgoing(current).Where(IsConstructionEdge))
            {
                if (!members.Contains(edge.To.Id)) continue;
                if (edge.To.Id == start.Id)
                {
                    var path = new List<string> { start.Id };
                    for (var id = current.Id; id != start.Id; id = parents[id]) path.Insert(1, id);
                    path.Add(start.Id);
                    return path;
                }

                if (parents.TryAdd(edge.To.Id, current.Id)) queue.Enqueue(edge.To);
            }
        }

        return members.Append(start.Id).ToList();
    }

    // ---------------------------------------------------------------- DI004

    private static void TransientsInSingletons(GraphIndex index, List<Diagnostic> diagnostics)
    {
        // Edges already reported as part of a captive path (DI001) say more than this rule would.
        var captiveEdges = diagnostics
            .Where(d => d.Id == RuleIds.Captive)
            .SelectMany(d => d.Path.Zip(d.Path.Skip(1)))
            .ToHashSet();

        foreach (var singleton in index.Nodes.Where(n => n.Lifetime == Lifetimes.Singleton && n.Kind != NodeKinds.Instance))
        foreach (var edge in index.Outgoing(singleton).Where(IsConstructionEdge).DistinctBy(e => e.To.Id))
        {
            var transient = edge.To;
            if (transient.Lifetime != Lifetimes.Transient || transient.Origin != Origins.App) continue;
            if (captiveEdges.Contains((singleton.Id, transient.Id))) continue;

            diagnostics.Add(new Diagnostic
            {
                Id = RuleIds.TransientInSingleton,
                Severity = transient.IsDisposable ? Severities.Warning : Severities.Info,
                Title = transient.IsDisposable ? "Disposable transient captured by singleton" : "Transient captured by singleton",
                Message = $"Singleton '{GraphIndex.Name(singleton)}' holds transient {GraphIndex.Label(transient)}, so a single instance lives for the whole app" +
                          (transient.IsDisposable ? " and is only disposed at shutdown." : "."),
                Help = transient.IsDisposable
                    ? "Inject a factory (Func<T> or IServiceScopeFactory) and dispose each instance after use, or register it as a singleton."
                    : "Fine if it is stateless. Otherwise register it as a singleton to make the lifetime explicit.",
                Nodes = { singleton.Id, transient.Id },
                Path = { singleton.Id, transient.Id },
            });
        }
    }

    // ---------------------------------------------------------------- DI005 / DI006

    private static IEnumerable<IGrouping<(string ServiceType, string? Key), ServiceNode>> RegistrationGroups(GraphIndex index) =>
        index.Nodes
            .Where(n => NodeKinds.IsRegistration(n.Kind))
            .GroupBy(n => (n.ServiceType, n.Key))
            .Where(g => g.Count() > 1 && g.Any(GraphIndex.IsUserCode));

    private static void ShadowedRegistrations(GraphIndex index, List<Diagnostic> diagnostics)
    {
        foreach (var group in RegistrationGroups(index))
        {
            var registrations = group.OrderBy(n => n.RegistrationIndex).ToList();
            var implementations = registrations.Select(Identity).Distinct().ToList();
            if (implementations.Count < 2) continue; // identical duplicates are DI006

            var winner = registrations[^1];
            // Replacing a framework or library default with your own implementation is intentional.
            // It's only a problem when one of the app's own registrations is silently replaced.
            if (!registrations.SkipLast(1).Any(n => n.Origin == Origins.App)) continue;

            var incoming = registrations.SelectMany(index.Incoming).ToList();
            if (incoming.Any(e => e.Dependency.Resolution == Resolutions.Enumerable)) continue; // IEnumerable<T>: intentional

            var consumers = index.Incoming(winner)
                .Where(e => e.Dependency.Resolution is Resolutions.Service or Resolutions.Keyed)
                .Select(e => e.From)
                .DistinctBy(n => n.Id)
                .ToList();

            var service = TypeDisplay.Short(winner.ServiceType);
            var names = string.Join(", ", registrations.Select(n => n.Kind == NodeKinds.Factory ? "factory #" + n.RegistrationIndex : GraphIndex.Name(n)));
            var winnerName = winner.Kind == NodeKinds.Factory ? "factory #" + winner.RegistrationIndex : GraphIndex.Name(winner);

            if (consumers.Count > 0)
            {
                diagnostics.Add(new Diagnostic
                {
                    Id = RuleIds.Shadowed,
                    Severity = Severities.Warning,
                    Title = "Shadowed registration",
                    Message = $"'{service}' is registered {registrations.Count} times ({names}), " +
                              $"but {string.Join(", ", consumers.Select(c => $"'{GraphIndex.Name(c)}'"))} {(consumers.Count == 1 ? "injects" : "inject")} a single '{service}', " +
                              $"so only the last one ('{winnerName}') is used.",
                    Help = $"Inject IEnumerable<{service}> to use all of them, use keyed services, or remove the registrations you don't need.",
                    Nodes = registrations.Select(n => n.Id).Concat(consumers.Select(n => n.Id)).ToList(),
                });
            }
            else if (winner.ServiceOrigin == Origins.App)
            {
                // Nothing injects it through a constructor, so it's resolved by hand (GetRequiredService<T>) or not at all.
                diagnostics.Add(new Diagnostic
                {
                    Id = RuleIds.Shadowed,
                    Severity = Severities.Info,
                    Title = "Shadowed registration",
                    Message = $"'{service}' is registered {registrations.Count} times ({names}). " +
                              $"Wherever a single '{service}' is resolved, only the last one ('{winnerName}') is used.",
                    Help = "If each registration is meant for a different consumer, use keyed services or distinct types. Otherwise, remove the extra registrations.",
                    Nodes = registrations.Select(n => n.Id).ToList(),
                });
            }
        }
    }

    private static void DuplicateRegistrations(GraphIndex index, List<Diagnostic> diagnostics)
    {
        foreach (var group in RegistrationGroups(index))
        foreach (var duplicates in group.Where(n => n.Kind == NodeKinds.Type).GroupBy(n => (Identity(n), n.Lifetime)).Where(g => g.Count() > 1))
        {
            // Libraries often add the same IConfigureOptions/IPostConfigureOptions per provider: harmless, and not yours to fix.
            if (!duplicates.Any(n => n.Origin == Origins.App)) continue;
            var first = duplicates.First();
            diagnostics.Add(new Diagnostic
            {
                Id = RuleIds.Duplicate,
                Severity = Severities.Warning,
                Title = "Duplicate registration",
                Message = $"{GraphIndex.Label(first)} is registered {duplicates.Count()} times as {first.Lifetime.ToLowerInvariant()}.",
                Help = "Remove the extra registration, or use TryAdd*/TryAddEnumerable in library code so it is only added once.",
                Nodes = duplicates.Select(n => n.Id).ToList(),
            });
        }
    }

    private static string Identity(ServiceNode node) => node.Kind + ":" + (node.ImplementationType ?? node.Id);

    // ---------------------------------------------------------------- DI007

    private static void UnusedRegistrations(GraphIndex index, List<Diagnostic> diagnostics)
    {
        foreach (var node in index.Nodes)
        {
            // Only the app's own contracts: framework contracts (IHostedService, IStartupFilter...) are consumed by the framework.
            if (!NodeKinds.IsRegistration(node.Kind) || node.Origin != Origins.App || node.ServiceOrigin != Origins.App) continue;
            if (index.Incoming(node).Count > 0) continue;
            // Validators, authentication handlers, notification handlers...: the library resolves them dynamically.
            if (node.ExtendsExternalType) continue;
            // Services registered more than once are reported by DI005/DI006 instead.
            if (index.Nodes.Any(n => n.Id != node.Id && NodeKinds.IsRegistration(n.Kind) && n.ServiceType == node.ServiceType && n.Key == node.Key)) continue;

            diagnostics.Add(new Diagnostic
            {
                Id = RuleIds.Unused,
                Severity = Severities.Info,
                Title = "Possibly unused registration",
                Message = $"Nothing in the graph depends on {GraphIndex.Label(node)}.",
                Help = "It may still be resolved manually (GetRequiredService) or by minimal API handlers. Otherwise, remove it.",
                Nodes = { node.Id },
            });
        }
    }

    // ---------------------------------------------------------------- DI008 / DI009

    private static void OpaqueFactories(GraphIndex index, List<Diagnostic> diagnostics)
    {
        foreach (var node in index.Nodes.Where(n => n.Kind == NodeKinds.Factory && n.Origin == Origins.App
                                                     && !n.DependenciesKnown && n.Dependencies.Count == 0))
        {
            diagnostics.Add(new Diagnostic
            {
                Id = RuleIds.OpaqueFactory,
                Severity = Severities.Info,
                Title = "Factory with unknown dependencies",
                Message = $"'{TypeDisplay.Short(node.ServiceType)}' is created by a factory whose dependencies DiScope cannot see, so its part of the graph may be incomplete.",
                Help = "Resolve dependencies with sp.GetRequiredService<T>() inside the lambda, or register the implementation type instead.",
                Nodes = { node.Id },
            });
        }
    }

    private static void NotConstructible(GraphIndex index, List<Diagnostic> diagnostics)
    {
        foreach (var node in index.Nodes.Where(n => n.Problem is not null && GraphIndex.IsUserCode(n)))
        {
            diagnostics.Add(new Diagnostic
            {
                Id = RuleIds.NotConstructible,
                Severity = Severities.Error,
                Title = "Cannot be constructed",
                Message = TypeDisplay.Short(node.Problem),
                Help = "Register a concrete class with a public constructor, or use a factory.",
                Nodes = { node.Id },
            });
        }
    }
}
