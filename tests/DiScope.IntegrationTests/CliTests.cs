using DiScope.Cli;

namespace DiScope.IntegrationTests;

public class CliTests
{
    [Fact]
    public void Defaults_are_html_debug_development_fail_on_error()
    {
        var o = CliOptions.Parse(Array.Empty<string>());
        Assert.Equal((".", "html", "Debug", "Development", "error"), (o.Target, o.Format, o.Configuration, o.Environment, o.FailOn));
    }

    [Fact]
    public void Options_and_app_arguments_are_parsed()
    {
        var o = CliOptions.Parse(new[] { "src/Api", "--format", "JSON", "-o", "-", "-f", "net9.0", "--fail-on", "warning", "--", "--urls", "x" });
        Assert.Equal(("src/Api", "json", "-", "net9.0", "warning"), (o.Target, o.Format, o.Output, o.Framework, o.FailOn));
        Assert.Equal(new[] { "--urls", "x" }, o.AppArgs);
    }

    [Theory]
    [InlineData("--format", "pdf")]
    [InlineData("--fail-on", "sometimes")]
    [InlineData("--timeout", "0")]
    [InlineData("--nope")]
    [InlineData("a.csproj", "b.csproj")]
    [InlineData("--out")]
    public void Invalid_arguments_are_rejected(params string[] args) =>
        Assert.Throws<CliException>(() => CliOptions.Parse(args));
}
