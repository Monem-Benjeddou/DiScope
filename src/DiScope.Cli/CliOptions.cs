namespace DiScope.Cli;

internal sealed class CliOptions
{
    public string Target { get; set; } = ".";
    public string Format { get; set; } = "html";
    public string? Output { get; set; }
    public bool Open { get; set; }
    public bool NoBuild { get; set; }
    public string Configuration { get; set; } = "Debug";
    public string? Framework { get; set; }
    public string Environment { get; set; } = "Development";
    public bool IncludeFramework { get; set; }
    public string FailOn { get; set; } = "error";
    public int TimeoutSeconds { get; set; } = 60;
    public bool Verbose { get; set; }
    public bool Quiet { get; set; }
    public bool Help { get; set; }
    public bool Version { get; set; }
    public List<string> AppArgs { get; } = new();

    public static readonly string[] Formats = { "html", "json", "mermaid", "text" };

    public const string Usage = """
        DiScope: see your .NET dependency injection graph.

        Usage:
          discope [project | folder | app.dll] [options] [-- app arguments]

        Options:
          --format <html|json|mermaid|text>  Output format (default: html)
          -o, --out <path>                   Output file (default: discope-report.<ext>; '-' writes to stdout)
          --open                             Open the report when done
          --no-build                         Don't run 'dotnet build' first
          -c, --configuration <name>         Build configuration (default: Debug)
          -f, --framework <tfm>              Target framework, for multi-targeted projects
          -e, --environment <name>           ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT (default: Development)
          --include-framework                Show Microsoft.*/System.* services in Mermaid output
          --fail-on <error|warning|none>     Exit with code 1 when issues of this severity exist (default: error)
          --timeout <seconds>                How long to wait for the app to build its host (default: 60)
          -v, --verbose                      Show build and app output
          -q, --quiet                        Only print the summary line
          --version                          Show the version
          -h, --help                         Show this help

        Examples:
          discope                                   Analyze the project in the current folder, write discope-report.html
          discope src/MyApi --open                  Analyze and open the interactive graph
          discope MyApi.csproj --format text        Compiler-style diagnostics, e.g. for CI
          discope MyApi.csproj -f net9.0 --format json -o di.json
        """;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        var targetSet = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Value()
            {
                if (i + 1 >= args.Length) throw new CliException($"Option '{arg}' needs a value.");
                return args[++i];
            }

            switch (arg)
            {
                case "--": options.AppArgs.AddRange(args.Skip(i + 1)); i = args.Length; break;
                case "-h" or "--help" or "-?": options.Help = true; break;
                case "--version": options.Version = true; break;
                case "--format": options.Format = Value().ToLowerInvariant(); break;
                case "-o" or "--out" or "--output": options.Output = Value(); break;
                case "--open": options.Open = true; break;
                case "--no-build": options.NoBuild = true; break;
                case "-c" or "--configuration": options.Configuration = Value(); break;
                case "-f" or "--framework": options.Framework = Value(); break;
                case "-e" or "--environment": options.Environment = Value(); break;
                case "--include-framework": options.IncludeFramework = true; break;
                case "--fail-on": options.FailOn = Value().ToLowerInvariant(); break;
                case "--timeout":
                    if (!int.TryParse(Value(), out var seconds) || seconds <= 0) throw new CliException("--timeout must be a positive number of seconds.");
                    options.TimeoutSeconds = seconds;
                    break;
                case "-v" or "--verbose": options.Verbose = true; break;
                case "-q" or "--quiet": options.Quiet = true; break;
                default:
                    if (arg.StartsWith('-')) throw new CliException($"Unknown option '{arg}'. Run 'discope --help'.");
                    if (targetSet) throw new CliException($"Only one project can be analyzed at a time (got '{options.Target}' and '{arg}').");
                    options.Target = arg;
                    targetSet = true;
                    break;
            }
        }

        if (!Formats.Contains(options.Format))
            throw new CliException($"Unknown format '{options.Format}'. Use one of: {string.Join(", ", Formats)}.");
        if (options.FailOn is not ("error" or "warning" or "none"))
            throw new CliException("--fail-on must be error, warning or none.");
        return options;
    }
}

internal sealed class CliException(string message) : Exception(message);
