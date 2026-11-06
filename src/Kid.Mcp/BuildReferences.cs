using System.IO;
using System.Text.RegularExpressions;

namespace Kid.Mcp;

internal static partial class BuildReferences
{
    internal static IEnumerable<(string Path, string Kind)> Read(string source, string kind, string root, string file)
    {
        var directory = Path.GetDirectoryName(file)!;
        var scriptPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } line)
        {
            var assignment = ShellDirectoryAssignment().Match(line);
            if (assignment.Success)
            {
                var name = assignment.Groups["name"].Value;
                var value = assignment.Groups["value"].Value;
                if (value == "\"$(cd \"$(dirname \"\u0024{BASH_SOURCE[0]}\")\" && pwd)\"")
                {
                    scriptPaths[name] = directory;
                }
                else if ((value == "\"$(dirname \"$SCRIPT_DIR\")\"" || value == "\"$(cd \"$SCRIPT_DIR/..\" && pwd)\"")
                         && scriptPaths.TryGetValue("SCRIPT_DIR", out var scriptDir) && Path.GetDirectoryName(scriptDir) is { } parent)
                {
                    scriptPaths[name] = parent;
                }
                else
                {
                    scriptPaths.Remove(name);
                }
            }

            var command = BuildCommand().Match(line);
            if (!command.Success || kind != "make" && command.Groups["include"].Success) continue;
            var path = command.Groups["path"].Value;
            var basePath = kind == "shell" ? root : directory;
            var variable = ShellPathPrefix().Match(path);
            if (variable.Success && !command.Groups["literal"].Success)
            {
                if (!scriptPaths.TryGetValue(variable.Groups["name"].Value, out var declaredPath)) continue;
                basePath = declaredPath;
                path = path[variable.Length..];
            }

            if (path.IndexOfAny(['$', '\u0060', '*', '?', '|', ';', '&']) >= 0) continue;
            var destination = ProjectGraphTools.ResolvePath(basePath, path);
            if (destination is not null && ProjectGraphTools.Within(root, destination))
            {
                yield return (Path.GetRelativePath(root, destination), kind);
            }
        }

        if (kind != "apk") yield break;
        var sources = ApkSources().Match(source);
        if (!sources.Success) yield break;
        foreach (var name in sources.Groups["paths"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (name.IndexOfAny(['$', '\u0060', ':', '*', '?']) >= 0) continue;
            var destination = ProjectGraphTools.ResolvePath(directory, name);
            if (destination is not null && ProjectGraphTools.Within(root, destination))
            {
                yield return (Path.GetRelativePath(root, destination), kind);
            }
        }
    }

    [GeneratedRegex("""^[ \t]*[@+-]*(?:source|\.|bash|sh|(?<include>-?include|sinclude))[ \t]+(?:"(?<path>[^"]+)"|(?<literal>'(?<path>[^']+)')|(?<path>[^\s;#]+))""",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex BuildCommand();

    [GeneratedRegex(@"^[ \t]*(?<name>[A-Za-z_]\w*)=(?<value>.+)$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex ShellDirectoryAssignment();

    [GeneratedRegex(@"^\$(?:\{(?<name>[A-Za-z_]\w*)\}|(?<name>[A-Za-z_]\w*))/",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex ShellPathPrefix();

    [GeneratedRegex(@"(?m)^[ \t]*source=""(?<paths>[^""]*)""", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex ApkSources();
}
