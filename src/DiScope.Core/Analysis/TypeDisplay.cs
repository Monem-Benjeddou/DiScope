using System.Text.RegularExpressions;

namespace DiScope.Analysis;

public static partial class TypeDisplay
{
    /// <summary><c>Ns.IRepository&lt;Ns.Order&gt;</c> → <c>IRepository&lt;Order&gt;</c>.</summary>
    public static string Short(string? fullName) =>
        string.IsNullOrEmpty(fullName) ? "" : NamespacePrefix().Replace(fullName, "");

    [GeneratedRegex(@"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)+(?=[A-Za-z_])")]
    private static partial Regex NamespacePrefix();
}
