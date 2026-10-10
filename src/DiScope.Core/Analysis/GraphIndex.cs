using DiScope.Model;

namespace DiScope.Analysis;

/// <summary>Adjacency lookups over a <see cref="RawGraph"/>. An edge points from a consumer to what it depends on.</summary>
public sealed class GraphIndex
{
    private readonly Dictionary<string, ServiceNode> _nodes;
    private readonly Dictionary<string, List<Edge>> _incoming = new();

    public readonly record struct Edge(ServiceNode From, Dependency Dependency, ServiceNode To);

    public GraphIndex(RawGraph graph)
    {
        Graph = graph;
        _nodes = graph.Services.ToDictionary(n => n.Id);
        foreach (var edge in graph.Services.SelectMany(Outgoing))
        {
            if (!_incoming.TryGetValue(edge.To.Id, out var list)) _incoming[edge.To.Id] = list = new();
            list.Add(edge);
        }
    }

    public RawGraph Graph { get; }
    public IReadOnlyCollection<ServiceNode> Nodes => _nodes.Values;

    public ServiceNode? Find(string id) => _nodes.GetValueOrDefault(id);

    public IEnumerable<Edge> Outgoing(ServiceNode node)
    {
        foreach (var dep in node.Dependencies)
        foreach (var target in dep.Targets)
            if (_nodes.TryGetValue(target, out var to))
                yield return new Edge(node, dep, to);
    }

    public IReadOnlyList<Edge> Incoming(ServiceNode node) =>
        _incoming.TryGetValue(node.Id, out var list) ? list : Array.Empty<Edge>();

    /// <summary>Framework-to-framework issues are not the user's problem.</summary>
    public static bool IsUserCode(ServiceNode node) => node.Origin != Origins.Framework;

    /// <summary>Short display name: implementation for registrations (falls back to service type).</summary>
    public static string Name(ServiceNode node) => TypeDisplay.Short(node.ImplementationType ?? node.ServiceType);

    /// <summary>"IOrderRepository (OrderRepository)" style label.</summary>
    public static string Label(ServiceNode node)
    {
        var service = TypeDisplay.Short(node.ServiceType);
        var impl = TypeDisplay.Short(node.ImplementationType);
        return string.IsNullOrEmpty(impl) || impl == service ? $"'{service}'" : $"'{service}' ({impl})";
    }
}
