using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace DiScope.Agent;

/// <summary>
/// Launched by the DiScope CLI as:
///   dotnet exec --runtimeconfig App.runtimeconfig.json --depsfile App.deps.json DiScope.Agent.dll --assembly App.dll --out graph.json
/// so it runs on the app's own frameworks and dependency versions.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 64;
    private const int ExitNoHost = 2;
    private const int ExitTimeout = 3;

    private static int Main(string[] args)
    {
        string? assemblyPath = null, outputPath = null;
        var timeout = TimeSpan.FromSeconds(30);
        var appArgs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--assembly" when i + 1 < args.Length: assemblyPath = args[++i]; break;
                case "--out" when i + 1 < args.Length: outputPath = args[++i]; break;
                case "--timeout" when i + 1 < args.Length: timeout = TimeSpan.FromSeconds(int.Parse(args[++i])); break;
                case "--": appArgs.AddRange(args.Skip(i + 1)); i = args.Length; break;
            }
        }

        if (assemblyPath is null || outputPath is null)
        {
            Console.Error.WriteLine("usage: DiScope.Agent --assembly <app.dll> --out <graph.json> [--timeout <s>] [-- app args]");
            return ExitUsage;
        }

        assemblyPath = Path.GetFullPath(assemblyPath);
        outputPath = Path.GetFullPath(outputPath);
        RegisterResolvers(assemblyPath);

        var app = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var entryPoint = app.EntryPoint;
        if (entryPoint is null)
        {
            Console.Error.WriteLine($"'{Path.GetFileName(assemblyPath)}' has no entry point. DiScope analyzes executable projects.");
            return ExitNoHost;
        }

        var capture = new HostCapture(app, ReadProjectAssemblies(assemblyPath, app), outputPath);
        using var subscription = DiagnosticListener.AllListeners.Subscribe(capture);

        var thread = new Thread(() =>
        {
            Exception? error = null;
            try
            {
                var parameters = entryPoint.GetParameters().Length == 0 ? null : new object[] { appArgs.ToArray() };
                entryPoint.Invoke(null, parameters);
            }
            catch (TargetInvocationException ex) { error = ex.InnerException ?? ex; }
            catch (Exception ex) { error = ex; }
            capture.EntryFinished(error);
        })
        {
            IsBackground = true,
            Name = "DiScope: app entry point",
        };
        thread.Start();

        int exitCode;
        if (!capture.Wait(timeout))
        {
            Console.Error.WriteLine($"Timed out after {timeout.TotalSeconds:0}s waiting for the app to build its host.");
            exitCode = ExitTimeout;
        }
        else if (capture.Error is not null)
        {
            Console.Error.WriteLine(capture.Error);
            exitCode = ExitNoHost;
        }
        else
        {
            Console.Error.WriteLine($"DiScope.Agent: captured {capture.ServiceCount} services.");
            exitCode = ExitOk;
        }

        // The app may have started foreground threads; don't let them keep the process alive.
        Environment.Exit(exitCode);
        return exitCode;
    }

    /// <summary>Resolve the app's own and package assemblies (and native libraries) from its output folder.</summary>
    private static void RegisterResolvers(string assemblyPath)
    {
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        var directory = Path.GetDirectoryName(assemblyPath)!;

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = resolver.ResolveAssemblyToPath(name);
            if (path is null)
            {
                var local = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(local)) path = local;
            }

            return path is null ? null : context.LoadFromAssemblyPath(path);
        };

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
        };
    }

    /// <summary>The app assembly plus every project reference listed in its deps.json.</summary>
    private static HashSet<string> ReadProjectAssemblies(string assemblyPath, Assembly app)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { app.GetName().Name! };
        var depsFile = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (!File.Exists(depsFile)) return names;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(depsFile));
            if (doc.RootElement.TryGetProperty("libraries", out var libraries))
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Value.TryGetProperty("type", out var type) && type.GetString() == "project")
                        names.Add(library.Name.Split('/')[0]);
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to the app assembly only.
        }

        return names;
    }
}
