using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiScope.Model;

namespace DiScope.Agent;

/// <summary>
/// Listens to the "Microsoft.Extensions.Hosting" diagnostic events that every generic host
/// (HostBuilder, HostApplicationBuilder, WebApplicationBuilder) raises while it builds.
///
/// On <c>HostBuilding</c> we append a ConfigureServices callback. It runs after all of the app's own
/// registrations, snapshots the IServiceCollection, writes the graph, and then aborts the build so the
/// app never starts (no Kestrel, no hosted services, no service provider validation).
/// </summary>
internal sealed class HostCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private const string HostingListener = "Microsoft.Extensions.Hosting";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly Assembly _app;
    private readonly HashSet<string> _appAssemblies;
    private readonly string _outputPath;
    private readonly ManualResetEventSlim _done = new();
    private int _captured;
    private object? _fallbackServices;
    private string? _builderType;

    public HostCapture(Assembly app, HashSet<string> appAssemblies, string outputPath)
    {
        _app = app;
        _appAssemblies = appAssemblies;
        _outputPath = outputPath;
    }

    public string? Error { get; private set; }
    public int ServiceCount { get; private set; }

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    // ---------------------------------------------------------------- DiagnosticListener plumbing

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == HostingListener) listener.Subscribe(this);
    }

    public void OnNext(KeyValuePair<string, object?> evt)
    {
        switch (evt.Key)
        {
            case "HostBuilding" when Volatile.Read(ref _captured) == 0 && evt.Value is not null:
                OnHostBuilding(evt.Value);
                break;
            case "HostBuilt":
                // Only reached if our ConfigureServices callback never ran (unusual builders).
                if (_fallbackServices is not null) Capture(_fallbackServices);
                throw CreateAbortException();
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    /// <summary>Called when the app's entry point returns or throws.</summary>
    public void EntryFinished(Exception? error)
    {
        if (Volatile.Read(ref _captured) == 1) return;
        if (_fallbackServices is not null)
        {
            Capture(_fallbackServices);
            return;
        }

        Error = error is null
            ? "The app's entry point finished without building a generic host (HostBuilder, HostApplicationBuilder or WebApplicationBuilder)."
            : $"The app's entry point failed before its host was built: {error.GetType().Name}: {error.Message}";
        _done.Set();
    }

    // ---------------------------------------------------------------- capture

    private void OnHostBuilding(object builder)
    {
        _builderType = builder.GetType().Name;

        // HostApplicationBuilder exposes Services directly; keep it as a fallback.
        var servicesProperty = builder.GetType().GetProperty("Services", BindingFlags.Public | BindingFlags.Instance);
        if (servicesProperty?.PropertyType.FullName == "Microsoft.Extensions.DependencyInjection.IServiceCollection")
            _fallbackServices = servicesProperty.GetValue(builder);

        // HostBuilder and the IHostBuilder adapter of HostApplicationBuilder: run last in ConfigureServices.
        var hostBuilder = builder.GetType().GetInterfaces()
            .FirstOrDefault(i => i.FullName == "Microsoft.Extensions.Hosting.IHostBuilder");
        var configure = hostBuilder?.GetMethod("ConfigureServices");
        if (configure is null) return;

        var delegateType = configure.GetParameters()[0].ParameterType; // Action<HostBuilderContext, IServiceCollection>
        var args = delegateType.GetGenericArguments();
        var context = Expression.Parameter(args[0], "context");
        var services = Expression.Parameter(args[1], "services");
        var body = Expression.Call(
            Expression.Constant(this),
            typeof(HostCapture).GetMethod(nameof(OnServicesConfigured), BindingFlags.NonPublic | BindingFlags.Instance)!,
            Expression.Convert(services, typeof(object)));
        var callback = Expression.Lambda(delegateType, body, context, services).Compile();
        configure.Invoke(builder, new object[] { callback });
    }

    private void OnServicesConfigured(object services)
    {
        Capture(services);
        throw CreateAbortException();
    }

    private void Capture(object services)
    {
        if (Interlocked.Exchange(ref _captured, 1) == 1) return;
        try
        {
            // Write the result right here: the app may catch our abort exception and call Environment.Exit.
            var graph = GraphBuilder.Build(services, _app, _appAssemblies);
            graph.App.CapturedFrom = _builderType;
            ServiceCount = graph.Services.Count;
            // Write then rename, so the CLI never sees a half-written file even if the app exits right after.
            var temp = _outputPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(graph, Json));
            File.Move(temp, _outputPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Error = "DiScope failed to analyze the service collection: " + ex;
        }
        finally
        {
            _done.Set();
        }
    }

    /// <summary>
    /// Prefer the framework's own HostAbortedException (.NET 7+): app templates filter it out of
    /// their "Host terminated unexpectedly" logging, exactly as they do for `dotnet ef`.
    /// </summary>
    private static Exception CreateAbortException()
    {
        const string message = "DiScope captured the service registrations and stopped the host. This is expected.";
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Microsoft.Extensions.Hosting.HostAbortedException", throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (type is not null && Activator.CreateInstance(type, message) is Exception aborted) return aborted;
        return new OperationCanceledException(message);
    }
}
