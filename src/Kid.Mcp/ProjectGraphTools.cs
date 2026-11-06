using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Kid.Mcp;

[McpServerToolType]
public static partial class ProjectGraphTools
{
    private const int EntryLimit = 30_000;
    private const int SourceLimit = 64 * 1024;
    private const int ReadBudget = 8 * 1024 * 1024;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "vendor", "bin", "obj", "dist", "build", "out", "target", "coverage",
        "venv", "__pycache__", "generated"
    };

    private static readonly string[] ScriptExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts"];
    private static readonly HashSet<string> SourceExtensions = new(ScriptExtensions.Concat(
        [".cs", ".fs", ".vb", ".py", ".go", ".rs", ".java", ".kt", ".swift", ".c", ".cc", ".cpp", ".h", ".hpp",
         ".rb", ".php", ".vue", ".svelte", ".sql", ".xaml", ".ps1", ".sh", ".bash", ".mk", ".csproj", ".fsproj", ".vbproj", ".slnx", ".sln"]),
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Manifests = new(
        ["package.json", "pyproject.toml", "Cargo.toml", "go.mod", "pom.xml", "Dockerfile", "Makefile", "APKBUILD", "README.md"],
        StringComparer.OrdinalIgnoreCase);

    [McpServerTool(Name = "project_graph", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Map an authorized local project as a compact text graph of directory modules and observed static dependencies. No source bodies are returned. Use scope and depth to drill down. Limits and omissions are reported.")]
    public static string ProjectGraph(
        [Description("Absolute local path to the project directory.")] string root,
        [Description("Optional project-relative folder to map.")] string scope = ".",
        [Description("Directory levels to group within scope, 1-3.")] int depth = 2,
        [Description("Maximum response characters, 1000-16000.")] int maxChars = 4_000,
        CancellationToken cancellationToken = default)
    {
        if (depth is < 1 or > 3 || maxChars is < 1_000 or > 16_000)
        {
            throw new McpException("Use depth 1–3 and maxChars 1000–16000.");
        }

        var project = OpenProject(root);
        var folder = ResolveScope(project, scope);
        var elapsed = Stopwatch.StartNew();
        var modules = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<(string From, string To, string Kind), int>();
        var dirs = new Queue<string>();
        dirs.Enqueue(folder);
        var examined = 0;
        var files = 0;
        var unclassified = 0;
        var skipped = 0;
        var parsed = 0;
        var linkInputs = 0;
        var unresolved = 0;
        var bytesRead = 0;
        var scanLimited = false;
        var referenceLimited = false;

        while (dirs.TryDequeue(out var directory))
        {
            if (AtScanLimit()) break;
            try
            {
                if (!SafePath(project, directory)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (AtScanLimit()) break;
                    examined++;
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Hidden)) continue;
                        var name = Path.GetFileName(entry);
                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            if (!Ignored(name)) dirs.Enqueue(entry);
                            continue;
                        }

                        var extension = Path.GetExtension(entry).ToLowerInvariant();
                        if (name.StartsWith('.')) continue;
                        if (!SourceExtensions.Contains(extension) && !Manifests.Contains(name))
                        {
                            unclassified++;
                            continue;
                        }
                        files++;
                        var module = GetModule(ModuleFor(entry));
                        var from = module.Path;
                        module.Files++;
                        module.Example ??= Relative(project, entry);
                        if (Manifests.Contains(name) || IsProject(extension))
                        {
                            module.Manifest ??= Relative(project, entry);
                        }

                        var buildKind = name.ToUpperInvariant() switch
                        {
                            "APKBUILD" => "apk",
                            "MAKEFILE" => "make",
                            _ => extension is ".sh" or ".bash" ? "shell" : extension == ".mk" ? "make" : null
                        };
                        if (buildKind is null && !IsProject(extension) && extension != ".slnx" && extension != ".py" && !ScriptExtensions.Contains(extension)) continue;
                        linkInputs++;
                        var length = new FileInfo(entry).Length;
                        if (length > SourceLimit || bytesRead + length > ReadBudget)
                        {
                            referenceLimited = true;
                            continue;
                        }

                        bytesRead += (int)length;
                        var source = ReadBounded(entry);
                        if (source is null)
                        {
                            referenceLimited = true;
                            continue;
                        }

                        var links = new HashSet<(string To, string Kind)>();
                        var references = buildKind is null
                            ? ReadReferences(source, extension)
                            : BuildReferences.Read(source, buildKind, project, entry);
                        foreach (var (reference, kind) in references)
                        {
                            if (AtScanLimit()) break;
                            var destination = ResolveReference(project, entry, reference, kind);
                            if (destination is null)
                            {
                                unresolved++;
                                continue;
                            }
                            var to = GetModule(ModuleFor(destination)).Path;
                            if (from != to) links.Add((to, kind));
                        }

                        parsed++;
                        foreach (var (to, kind) in links)
                        {
                            var edge = (from, to, kind);
                            edges[edge] = edges.GetValueOrDefault(edge) + 1;
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return Render(maxChars, Relative(project, folder), modules, edges, files, parsed, skipped,
            scanLimited, referenceLimited, linkInputs, unclassified, unresolved);

        bool AtScanLimit()
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanLimited |= examined >= EntryLimit || elapsed.ElapsedMilliseconds >= 5_000;
            return scanLimited;
        }

        string ModuleFor(string file)
        {
            var parent = Path.GetDirectoryName(file)!;
            var relative = Relative(project, parent);
            if (!Within(folder, parent)) return relative.Split('/')[0];
            var local = Relative(folder, parent);
            if (local == ".") return Relative(project, folder);
            return Relative(project, Path.Combine(folder, Path.Combine(local.Split('/').Take(depth).ToArray())));
        }

        Module GetModule(string name)
        {
            if (!modules.TryGetValue(name, out var module)) modules.Add(name, module = new Module(name));
            return module;
        }
    }

    private static string OpenProject(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)
            || Path.GetPathRoot(root) is not { Length: 3 } drive || drive[1] != ':')
        {
            throw new McpException("Provide an absolute path to a local project directory.");
        }

        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Parent is null || new DriveInfo(drive).DriveType == DriveType.Network)
        {
            throw new McpException("Choose an existing project directory below the drive root.");
        }
        for (DirectoryInfo? parent = directory; parent is not null; parent = parent.Parent)
        {
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new McpException("Project paths through symbolic links or junctions are not supported.");
            }
        }
        return Path.TrimEndingDirectorySeparator(directory.FullName);
    }

    private static string ResolveScope(string root, string scope)
    {
        var folder = ResolvePath(root, scope);
        if (folder is null || !Within(root, folder) || !Directory.Exists(folder) || !SafePath(root, folder))
        {
            throw new McpException("Scope must name a readable project directory without links or excluded folders.");
        }
        return Path.TrimEndingDirectorySeparator(folder);
    }

    internal static string? ResolvePath(string directory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')) return null;
        try
        {
            return Path.GetFullPath(path, directory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    internal static bool Within(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SafePath(string root, string path)
    {
        for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0 || Ignored(Path.GetFileName(current))) return false;
        }
        return true;
    }

    private static bool Ignored(string name) => name.StartsWith('.') || IgnoredDirectories.Contains(name);
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool IsProject(string extension) => extension is ".csproj" or ".fsproj" or ".vbproj";

    private static string? ReadBounded(string path)
    {
        using var reader = File.OpenText(path);
        var buffer = new char[SourceLimit + 1];
        var length = reader.ReadBlock(buffer);
        return length > SourceLimit ? null : new string(buffer, 0, length);
    }

    private static IEnumerable<(string Path, string Kind)> ReadReferences(string source, string extension)
    {
        if (IsProject(extension) || extension == ".slnx")
        {
            using var xml = XmlReader.Create(new StringReader(source), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element) continue;
                var path = xml.LocalName switch
                {
                    "ProjectReference" => xml.GetAttribute("Include"),
                    "Project" when extension == ".slnx" => xml.GetAttribute("Path"),
                    _ => null
                };
                if (path is not null) yield return (path, "project");
            }
            yield break;
        }

        var python = extension == ".py";
        foreach (Match match in (python ? PythonImport() : ScriptImport()).Matches(source))
        {
            var path = match.Groups[1].Value;
            if (python || path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal))
            {
                yield return (path, python ? "python" : "script");
            }
        }
    }

    private static string? ResolveReference(string root, string file, string reference, string kind)
    {
        if (kind is "shell" or "make" or "apk")
        {
            var candidate = ResolvePath(root, reference);
            return candidate is null ? null : Existing(candidate);
        }

        var directory = Path.GetDirectoryName(file)!;
        if (kind == "python")
        {
            var dots = reference.TakeWhile(c => c == '.').Count();
            var name = reference[dots..].Replace('.', Path.DirectorySeparatorChar);
            if (dots == 0)
            {
                return FindPython(Path.Combine(root, name)) ?? FindPython(Path.Combine(root, "src", name));
            }
            var path = ResolvePath(directory, string.Concat(Enumerable.Repeat(@"..\", dots - 1)) + (name.Length > 0 ? name : "."));
            return path is null ? null : FindPython(path);
        }

        var target = ResolvePath(directory, reference);
        if (target is null || !Within(root, target)) return null;
        if (kind == "project")
        {
            return IsProject(Path.GetExtension(target).ToLowerInvariant()) ? Existing(target) : null;
        }
        if (ScriptExtensions.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase) && Existing(target) is { } exact) return exact;
        foreach (var extension in ScriptExtensions)
        {
            if (Existing(target + extension) is { } sibling) return sibling;
            if (Existing(Path.Combine(target, "index" + extension)) is { } index) return index;
        }
        return null;

        string? FindPython(string path) => Existing(path + ".py") ?? Existing(Path.Combine(path, "__init__.py"));
        string? Existing(string path) => Within(root, path) && File.Exists(path) && SafePath(root, path) ? path : null;
    }

    private static string Render(int budget, string scope, Dictionary<string, Module> modules,
        Dictionary<(string From, string To, string Kind), int> edges, int files, int parsed, int skipped,
        bool scanLimited, bool referenceLimited, int linkInputs, int unclassified, int unresolved)
    {
        var output = new StringBuilder();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shownEdges = 0;
        const int footerReserve = 300;
        Add($"scope={JsonSerializer.Serialize(scope)} files={files} sourcesParsed={parsed}");
        Add("M id path file-count sample | E from to kind evidence-files");
        foreach (var (path, module) in modules.OrderByDescending(pair => pair.Value.Files).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var id = ids.Count + 1;
            var sample = module.Manifest ?? module.Example;
            var suffix = sample is null ? "" : " " + JsonSerializer.Serialize(sample);
            if (Add($"M {id} {JsonSerializer.Serialize(path)} {module.Files}{suffix}", edges.Count > 0 ? budget * 2 / 3 : budget))
            {
                ids.Add(path, id);
            }
        }
        foreach (var (edge, count) in edges.OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key.From, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(pair => pair.Key.To, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal))
        {
            if (ids.TryGetValue(edge.From, out var from) && ids.TryGetValue(edge.To, out var to)
                && Add($"E {from} {to} {edge.Kind} {count}")) shownEdges++;
        }
        var coverage = parsed == 0 ? "no-link-analysis"
            : scanLimited || referenceLimited || skipped > 0 || unclassified > 0 || unresolved > 0 ? "partial-static"
            : "static-only";
        output.Append($"modules={ids.Count}/{modules.Count} edges={shownEdges}/{edges.Count} skipped={skipped} ")
            .Append($"coverage={coverage} linkInputs={linkInputs} unclassified={unclassified} unresolved={unresolved} ")
            .Append($"scanLimited={scanLimited} referenceLimited={referenceLimited} outputLimited={ids.Count < modules.Count || shownEdges < edges.Count}");
        return output.ToString();

        bool Add(string line, int? limit = null)
        {
            if (output.Length + line.Length + 2 > (limit ?? budget) - footerReserve) return false;
            output.AppendLine(line);
            return true;
        }
    }

    [GeneratedRegex("""(?m)(?:^[ \t]*(?:import|export)[ \t]+(?:[^'";]*?\sfrom\s+)?|(?:\brequire|\bimport)\s*\(\s*)['"]([^'"\r\n]+)['"]""",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptImport();

    [GeneratedRegex(@"(?m)^[ \t]*(?:from|import)[ \t]+([.\w]+)", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex PythonImport();

    private sealed class Module(string path)
    {
        internal string Path { get; } = path;
        internal int Files { get; set; }
        internal string? Example { get; set; }
        internal string? Manifest { get; set; }
    }
}
