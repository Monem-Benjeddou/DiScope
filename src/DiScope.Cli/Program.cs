using System.Diagnostics;
using System.Reflection;
using DiScope;
using DiScope.Analysis;
using DiScope.Cli;
using DiScope.Export;

return await Cli.RunAsync(args);

namespace DiScope.Cli
{
    internal static class Cli
    {
        public const int ExitOk = 0;
        public const int ExitIssues = 1;
        public const int ExitFailure = 2;

        public static async Task<int> RunAsync(string[] args)
        {
            CliOptions options;
            try
            {
                options = CliOptions.Parse(args);
            }
            catch (CliException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitFailure;
            }

            if (options.Help)
            {
                Console.WriteLine(CliOptions.Usage);
                return ExitOk;
            }

            if (options.Version)
            {
                Console.WriteLine(typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]);
                return ExitOk;
            }

            var toStdout = options.Output == "-";
            // Progress goes to stderr so stdout stays clean for `--out -`.
            void Log(string message)
            {
                if (!options.Quiet) Console.Error.WriteLine(Color.Dim(message));
            }

            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };

            Report report;
            try
            {
                report = await new Analyzer(options, Log).RunAsync(cancel.Token);
            }
            catch (CliException ex)
            {
                Console.Error.WriteLine(Color.Red("error: ") + ex.Message);
                return ExitFailure;
            }
            catch (OperationCanceledException)
            {
                return ExitFailure;
            }

            var content = options.Format switch
            {
                "json" => JsonExporter.Export(report),
                "mermaid" => MermaidExporter.Export(report, options.IncludeFramework),
                "text" => TextExporter.Export(report),
                _ => HtmlExporter.Export(report),
            };

            string? outputPath = null;
            if (toStdout)
            {
                Console.Out.Write(content);
            }
            else
            {
                outputPath = Path.GetFullPath(options.Output ?? "discope-report." + Extension(options.Format));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(outputPath, content);
            }

            if (!toStdout || options.Format != "text") PrintSummary(report, options, outputPath);
            if (options.Open && outputPath is not null) OpenFile(outputPath);

            var failed = options.FailOn switch
            {
                "error" => report.Summary.Errors > 0,
                "warning" => report.Summary.Errors + report.Summary.Warnings > 0,
                _ => false,
            };
            return failed ? ExitIssues : ExitOk;
        }

        private static string Extension(string format) => format switch
        {
            "mermaid" => "mmd",
            "text" => "txt",
            _ => format,
        };

        private static void PrintSummary(Report report, CliOptions options, string? outputPath)
        {
            var output = Console.Error;
            if (!options.Quiet)
            {
                output.WriteLine();
                foreach (var d in report.Diagnostics.Where(d => d.Severity != Severities.Info))
                {
                    var label = d.Severity == Severities.Error ? Color.Red("error " + d.Id) : Color.Yellow("warning " + d.Id);
                    output.WriteLine($"  {label}: {d.Message}");
                }

                if (report.Summary.Infos > 0)
                    output.WriteLine(Color.Dim($"  + {report.Summary.Infos} info message(s) in the report."));
                output.WriteLine();
            }

            output.WriteLine(TextExporter.Summary(report));
            if (outputPath is not null) output.WriteLine(Color.Dim("Report: " + outputPath));
        }

        private static void OpenFile(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not open the report: {ex.Message}");
            }
        }
    }

    internal static class Color
    {
        private static readonly bool Enabled = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

        public static string Red(string s) => Wrap(s, "31");
        public static string Yellow(string s) => Wrap(s, "33");
        public static string Dim(string s) => Wrap(s, "2");

        private static string Wrap(string s, string code) => Enabled ? $"\u001b[{code}m{s}\u001b[0m" : s;
    }
}
