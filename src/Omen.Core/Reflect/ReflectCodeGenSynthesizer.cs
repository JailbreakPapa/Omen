// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Build;
using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.Core.Reflect;

/// <summary>
/// Turns a module's NF_CLASS-annotated headers into CodeGenSteps that run Tools/ReflectCodeGen/ReflectCodeGen.py.
///
/// The same discovery problem moc has: which headers need generating is a property of their
/// contents, not something anyone writes down, so it is scanned for rather than declared. This is
/// <see cref="Qt.QtCodeGenSynthesizer"/> pointed at a different token, and the output is an
/// ordinary CodeGenStep, so incrementality and step ordering come from the action graph unchanged.
///
/// The generator uses libclang, which means it parses the header for real and therefore needs the
/// module's include paths and definitions - the same argument that puts moc behind
/// <see cref="CompileEnvironment"/>. A drifted include set here does not fail loudly; it fails to
/// resolve a member's type and reflects nothing.
/// </summary>
/// <summary>
/// Process-wide memo of which headers are NF_CLASS-annotated, validated per file by write time
/// and size, loadable from and savable to a JSON file so a build or generation only reads
/// headers that changed since the last one. LoadFrom/SaveTo are called by the orchestrators
/// around graph construction; the memo itself is safe to use without them.
/// </summary>
public static class AnnotationCache
{
    private sealed record Entry(long Ticks, long Length, bool Annotated);
    private sealed class Persisted { public string File { get; set; } = ""; public long Ticks { get; set; } public long Length { get; set; } public bool Annotated { get; set; } }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ValidatedThisRun = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAnnotated(string path, Func<string, bool> scan)
    {
        if (ValidatedThisRun.ContainsKey(path) && Entries.TryGetValue(path, out var validated))
            return validated.Annotated;

        var info = new FileInfo(path);
        if (!info.Exists)
            return false;
        var ticks = info.LastWriteTimeUtc.Ticks;
        if (!Entries.TryGetValue(path, out var entry) || entry.Ticks != ticks || entry.Length != info.Length)
        {
            entry = new Entry(ticks, info.Length, scan(path));
            Entries[path] = entry;
        }
        ValidatedThisRun[path] = 0;
        return entry.Annotated;
    }

    public static void LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            foreach (var e in System.Text.Json.JsonSerializer.Deserialize<List<Persisted>>(File.ReadAllText(path)) ?? [])
                Entries.TryAdd(e.File, new Entry(e.Ticks, e.Length, e.Annotated));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // A corrupt cache costs one full scan, nothing more.
        }
    }

    public static void SaveTo(string path)
    {
        var entries = Entries
            .Select(kv => new Persisted { File = kv.Key, Ticks = kv.Value.Ticks, Length = kv.Value.Length, Annotated = kv.Value.Annotated })
            .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(entries));
    }
}

public sealed class ReflectCodeGenSynthesizer
{
    private static readonly string[] HeaderExtensions = [".h", ".hpp", ".hxx", ".inl"];

    /// <summary>
    /// The annotation that marks a type for generation, from NightFoxCore/RTTI/NightFoxReflect.h.
    /// Only NF_CLASS: NF_COMPONENT is Component.h's own macro, used by 471 headers that reflect by
    /// hand, and scanning for it would claim every one of them.
    /// </summary>
    private const string AnnotationMacro = "NF_CLASS(";

    /// <summary>The generator, relative to the engine root.</summary>
    private static readonly string ScriptRelativePath =
        Path.Combine("Tools", "ReflectCodeGen", "ReflectCodeGen.py");

    /// <summary>
    /// The module's reflection code-generation steps. Empty for a module with no annotated header,
    /// which is nearly all of them - the two styles coexist by design.
    ///
    /// Returns rather than mutating ModuleRules.CodeGenSteps, for the reason QtCodeGenSynthesizer
    /// does: the graph is built once per configuration against the same ModuleRules instances, so
    /// appending would accumulate duplicates across configs.
    /// </summary>
    public IReadOnlyList<CodeGenStep> CreateSteps(ModuleRules module, BuildContext context, CompileEnvironment env)
    {
        if (!module.EnableReflectCodeGen)
        {
            return [];
        }

        // Ordinal sort is a correctness requirement, not tidiness: unsorted scan output makes
        // action digests unstable, which makes no-op rebuilds rerun forever.
        var headers = module.ResolveSourceFiles(context.ProjectRoot, HeaderExtensions)
            .OrderBy(file => file, StringComparer.Ordinal)
            .Where(IsAnnotated)
            .ToList();

        if (headers.Count == 0)
        {
            return [];
        }

        var engineRoot = FindEngineRoot(context.ProjectRoot)
            ?? throw new InvalidOperationException(
                $"Module '{module.Name}' has NF_CLASS-annotated headers, but "
                + $"{ScriptRelativePath} was not found above '{context.ProjectRoot}'.");

        var script = Path.Combine(engineRoot, ScriptRelativePath);
        var python = PythonCommand(engineRoot);
        var flagsFile = Path.Combine(
            context.IntermediateDirectory, ModulePathName.Sanitize(module.Name), "reflect_flags.txt");
        WriteFlagsFile(flagsFile, env);

        // Outputs land in one flat directory, so two same-named headers in different
        // subdirectories would silently overwrite each other - surfacing much later, and far from
        // the cause, as a duplicate or missing symbol.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<CodeGenStep>();

        foreach (var header in headers)
        {
            var output = OutputName(header, headers, module, context);
            if (owners.TryGetValue(output, out var owner))
            {
                throw new InvalidOperationException(
                    $"Module '{module.Name}' has two annotated headers that would both generate " +
                    $"'{output}':{Environment.NewLine}  {owner}{Environment.NewLine}  {header}{Environment.NewLine}" +
                    "Rename one, or split them into separate modules.");
            }

            owners[output] = header;
            steps.Add(new CodeGenStep
            {
                Description = $"reflect {Path.GetFileName(header)}",
                Command =
                    $"{python} \"{script}\" --output-dir \"$(IntermediateDir)\" " +
                    $"--flags-file \"{flagsFile}\" \"{header}\"",
                Inputs = { header, script },
                Outputs = { output }
            });
        }

        return steps;
    }

    /// <summary>
    /// The engine root at or above <paramref name="start"/>, or null.
    ///
    /// ProjectRoot is not it: RuleCompiler.ResolveProjectRoot deliberately roots a single-plugin
    /// pilot at the plugin, so a plugin build would look for the generator under Plugins/&lt;Plugin&gt;/.
    /// The engine is identified by holding the generator, which is the only thing being asked.
    /// </summary>
    internal static string? FindEngineRoot(string start)
    {
        for (var candidate = new DirectoryInfo(start); candidate != null; candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, ScriptRelativePath)))
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// The engine's own Python if it has one, otherwise whatever is on PATH.
    ///
    /// Not a preference: the generator imports libclang, which is pinned in
    /// python/requirements.txt and installed into the engine's venv. A bare `python` is
    /// whichever interpreter happens to be first on PATH, and usually does not have it.
    /// </summary>
    private static string PythonCommand(string engineRoot)
    {
        var launcher = Path.Combine(
            engineRoot, "python", OperatingSystem.IsWindows() ? "python.cmd" : "python.sh");
        return File.Exists(launcher) ? $"\"{launcher}\"" : "python";
    }

    /// <summary>
    /// Generated output name per header, disambiguated only where it has to be.
    ///
    /// Matches ReflectCodeGen.py's own naming (&lt;stem&gt;.generated.cpp) for the common case, so
    /// the file the tool writes is the file the graph tracks. A contested basename is qualified
    /// with its directory - and then for every header sharing it, so the result does not depend on
    /// which one was seen first.
    /// </summary>
    private static string OutputName(
        string header, IReadOnlyList<string> headers, ModuleRules module, BuildContext context)
    {
        var stem = Path.GetFileNameWithoutExtension(header);
        var contested = headers.Count(other =>
            string.Equals(Path.GetFileNameWithoutExtension(other), stem, StringComparison.OrdinalIgnoreCase)) > 1;

        if (!contested)
        {
            return $"{stem}.generated.cpp";
        }

        var moduleDirectory = Path.Combine(
            context.ProjectRoot, module.SourceDirectory ?? $"Source/{module.Name}");
        return $"{Qualifier(header, moduleDirectory)}{stem}.generated.cpp";
    }

    /// <summary>
    /// A header's directory relative to its module, flattened into a name fragment
    /// ("Hotspot/" becomes "Hotspot_"). Empty when the header sits at the module root.
    /// </summary>
    private static string Qualifier(string file, string moduleDirectory)
    {
        var directory = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(directory))
        {
            return string.Empty;
        }

        var relative = Path.GetRelativePath(moduleDirectory, directory);
        if (relative is "." or ".." || Path.IsPathRooted(relative))
        {
            return string.Empty;
        }

        return relative.Replace(Path.DirectorySeparatorChar, '_').Replace('/', '_') + "_";
    }

    /// <summary>
    /// The compile environment as one -I/-D per line. A file rather than the command line because
    /// an engine module has enough include paths to overflow one.
    /// </summary>
    private static void WriteFlagsFile(string path, CompileEnvironment env)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = env.IncludePaths.Concat(env.ExternalIncludePaths).Select(p => $"-I{p}")
            .Concat(env.Definitions.Select(d => $"-D{d}"));
        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// Whether each header carries the annotation macro, keyed by the file's write time and
    /// size and persisted across runs (see <see cref="AnnotationCache"/>): every module's
    /// every header was being read in full on every build and every generation, and headers
    /// in a directory several modules share were read once per module.
    /// </summary>
    private static bool IsAnnotated(string headerPath) => AnnotationCache.IsAnnotated(headerPath, ScanForAnnotation);

    private static bool ScanForAnnotation(string headerPath)
    {
        try
        {
            // #define lines are skipped so NightFoxReflect.h, which defines the macros, is not
            // mistaken for a header using them; // lines because a commented-out annotation is the
            // realistic way this scan and the generator's own would disagree, and the generator
            // errors on a header it was handed and found nothing in.
            return File.ReadLines(headerPath).Any(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith('#')
                    && !trimmed.StartsWith("//", StringComparison.Ordinal)
                    && line.Contains(AnnotationMacro, StringComparison.Ordinal);
            });
        }
        catch (IOException)
        {
            // A header that cannot be read cannot be scanned; leaving it out is the same outcome
            // as it containing nothing, and the compile will report it properly.
            return false;
        }
    }
}
