using System.Text.RegularExpressions;

namespace Kid.Mcp;

internal static partial class SensitiveContentRedactor
{
    [GeneratedRegex(
        @"(^|[?&#])((?:access_token|refresh_token|id_token|token|code|password|passwd|client_secret|secret|authorization|api_key|apikey|session|signature|sig|state)=)[^&#\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveUrlParameter();

    internal static string? Redact(string? value) =>
        value is null ? null : SensitiveUrlParameter().Replace(value, "$1$2[REDACTED]");
}
