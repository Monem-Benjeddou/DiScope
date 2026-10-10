using System.Text.Json;
using DiScope.Model;

namespace DiScope.Cli;

/// <summary>Builds the target, runs the agent inside it and turns the captured graph into a <see cref="Report"/>.</summary>
internal sealed class Analyzer
{
    private static readonly string[] ProjectExtensions = { ".csproj", ".fsproj", ".vbproj" };

    private readonly CliOptions _options;
    private readonly Action<string> _log;

    public Analyzer(CliOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
    }

    public static string AgentPath => Path.Combine(AppContext.BaseDirectory, "agent", "DiScope.Agent.dll");

    public async Task<Report> RunAsync(CancellationToken cancellationToken = default)
    {
        var assembly = await ResolveAssemblyAsync(cancellationToken);
        var graph = await CaptureAsync(assembly, cancellationToken);
        return Report.Create(graph);
    }

    // ---------------------------------------------------------------- locate & build

    private async Task<string> ResolveAssemblyAsync(CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(_options.Target);

        if (File.Exists(target) && target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return target;

        if (File.Exists(target) && target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            throw new CliException("Pass the startup project (the .csproj of your app), not the solution.");

        var project = File.Exists(target) ? target : FindProject(target);
        var properties = await GetPropertiesAsync(project, _options.Framework, cancellationToken);

        var framework = _options.Framework;
        if (framework is null && string.IsNullOrEmpty(properties.TargetPath) && !string.IsNullOrEmpty(properties.TargetFrameworks))
        {
            framework = properties.TargetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            _log($"'{Path.GetFileName(project)}' targets {properties.TargetFrameworks}; analyzing {framework} (choose with -f).");
            properties = await GetPropertiesAsync(project, framework, cancellationToken);
        }

        if (!string.Equals(properties.OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(properties.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase))
            throw new CliException($"'{Path.GetFileName(project)}' is a {properties.OutputType} project. Point DiScope at the app that builds the host (the one with Program.cs).");

        if (!_options.NoBuild) await BuildAsync(project, framework, cancellationToken);

        if (string.IsNullOrEmpty(properties.TargetPath) || !File.Exists(properties.TargetPath))
            throw new CliException($"Could not find the build output '{properties.TargetPath}'." + (_options.NoBuild ? " Build the project first or drop --no-build." : ""));
        return properties.TargetPath;
    }

    private static string FindProject(string directory)
    {
        if (!Directory.Exists(directory)) throw new CliException($"'{directory}' does not exist.");
        var projects = Directory.EnumerateFiles(directory)
            .Where(f => ProjectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        return projects.Count switch
        {
            1 => projects[0],
            0 => throw new CliException($"No project file in '{directory}'. Pass the path of your app's .csproj."),
            _ => throw new CliException($"Several projects in '{directory}': {string.Join(", ", projects.Select(Path.GetFileName))}. Pass one of them."),
        };
    }

    private sealed record ProjectProperties(string TargetPath, string TargetFrameworks, string OutputType);

    private async Task<ProjectProperties> GetPropertiesAsync(string project, string? framework, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "msbuild", project, "-nologo",
            "-getProperty:TargetPath", "-getProperty:TargetFrameworks", "-getProperty:OutputType",
            "-p:Configuration=" + _options.Configuration,
        };
        if (framework is not null) args.Add("-p:TargetFramework=" + framework);

        var result = await ProcessRunner.RunAsync(ProcessRunner.DotNet, args, Path.GetDirectoryName(project)!, TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
            throw new CliException($"Could not evaluate '{Path.GetFileName(project)}':\n{Tail(result.StdOut + result.StdErr)}");

        try
        {
            using var json = JsonDocument.Parse(result.StdOut);
            var p = json.RootElement.GetProperty("Properties");
            string Read(string name) => p.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            return new ProjectProperties(Read("TargetPath"), Read("TargetFrameworks"), Read("OutputType"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CliException("This needs the .NET 8 SDK or newer ('dotnet msbuild -getProperty' was not understood).");
        }
    }

    private async Task BuildAsync(string project, string? framework, CancellationToken cancellationToken)
    {
        _log($"Building {Path.GetFileName(project)}...");
        var args = new List<string> { "build", project, "-c", _options.Configuration, "-nologo", "-v", _options.Verbose ? "minimal" : "quiet" };
        if (framework is not null) args.AddRange(new[] { "-f", framework });

        var result = await ProcessRunner.RunAsync(ProcessRunner.DotNet, args, Path.GetDirectoryName(project)!, TimeSpan.FromMinutes(10),
            onOutput: _options.Verbose ? line => _log("  " + line) : null, cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
            throw new CliException($"Build failed:\n{Tail(result.StdOut + result.StdErr, 30)}");
    }

    // ---------------------------------------------------------------- capture

    private async Task<RawGraph> CaptureAsync(string assembly, CancellationToken cancellationToken)
    {
        var runtimeConfig = Path.ChangeExtension(assembly, ".runtimeconfig.json");
        var depsFile = Path.ChangeExtension(assembly, ".deps.json");
        if (!File.Exists(runtimeConfig))
            throw new CliException($"'{Path.GetFileName(runtimeConfig)}' not found. DiScope analyzes framework-dependent executables (the app's build output).");
        if (!File.Exists(AgentPath))
            throw new CliException($"The DiScope agent is missing from '{AgentPath}'. Reinstall the tool.");

        var output = Path.Combine(Path.GetTempPath(), $"discope-{Guid.NewGuid():N}.json");
        var args = new List<string> { "exec", "--runtimeconfig", runtimeConfig };
        if (File.Exists(depsFile)) args.AddRange(new[] { "--depsfile", depsFile });
        args.AddRange(new[] { AgentPath, "--assembly", assembly, "--out", output, "--timeout", _options.TimeoutSeconds.ToString() });
        if (_options.AppArgs.Count > 0)
        {
            args.Add("--");
            args.AddRange(_options.AppArgs);
        }

        // Run from the project folder, like `dotnet run`, so appsettings.json and content roots resolve.
        var workingDirectory = FindProjectDirectory(assembly);
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = _options.Environment,
            ["DOTNET_ENVIRONMENT"] = _options.Environment,
            ["DISCOPE"] = "1",
        };

        _log($"Capturing services from {Path.GetFileName(assembly)} ({_options.Environment})...");
        try
        {
            var result = await ProcessRunner.RunAsync(ProcessRunner.DotNet, args, workingDirectory,
                TimeSpan.FromSeconds(_options.TimeoutSeconds + 20), environment,
                onOutput: _options.Verbose ? line => _log("  app> " + line) : null, cancellationToken: cancellationToken);

            if (result.TimedOut)
                throw new CliException($"The app did not build its host within {_options.TimeoutSeconds}s. Is it waiting on something before builder.Build()?");
            // The file is written atomically before the host is stopped. Apps that catch the stop and call
            // Environment.Exit(n) themselves end with their own exit code, so the file is what counts.
            if (!File.Exists(output))
                throw new CliException($"Could not capture the service collection (exit code {result.ExitCode}):\n{Tail(result.StdErr.Length > 0 ? result.StdErr : result.StdOut)}");

            return Report.LoadRawGraph(output);
        }
        finally
        {
            try { File.Delete(output); } catch { /* temp file */ }
        }
    }

    /// <summary>bin/Debug/net8.0/App.dll → the folder holding App.csproj, when it can be found.</summary>
    private static string FindProjectDirectory(string assembly)
    {
        var name = Path.GetFileNameWithoutExtension(assembly);
        for (var dir = Path.GetDirectoryName(assembly); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (ProjectExtensions.Any(ext => File.Exists(Path.Combine(dir, name + ext)))) return dir;
            if (Directory.Exists(Path.Combine(dir, ".git"))) break;
        }

        return Path.GetDirectoryName(assembly)!;
    }

    private static string Tail(string text, int lines = 15)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToArray();
        return string.Join("\n", all.TakeLast(lines).Select(l => "  " + l));
    }
}
