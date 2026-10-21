// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Xml;
using System.Xml.Linq;
using Omen.Core.Build;
using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.Core.Qt;

/// <summary>
/// Turns a Qt module into CodeGenSteps by scanning its own files - the AUTOMOC/AUTOUIC/AUTORCC
/// role.
///
/// These inputs cannot be declared. moc's are whichever headers contain Q_OBJECT; uic's and
/// rcc's are whichever .ui and .qrc files the module has. All are discovered rather than written
/// down: nobody hand-writes a step per file (the NightFox tree has 813 Q_OBJECT headers, 196 .ui
/// and 58 .qrc), and a hand-written list rots the first time someone adds one.
///
/// Everything emitted is an ordinary CodeGenStep, so incrementality, step ordering and .cpp
/// compilation all come from the existing action graph unchanged - it needed no modification.
/// </summary>
public sealed class QtCodeGenSynthesizer
{
    private static readonly string[] HeaderExtensions = [".h", ".hpp", ".hxx"];

    /// <summary>
    /// The module's Qt code-generation steps. Empty for a module that does not use Qt.
    ///
    /// Returns rather than mutating ModuleRules.CodeGenSteps: the graph is built once per
    /// configuration against the same ModuleRules instances, so appending would accumulate
    /// duplicate steps across configs. Returning keeps this idempotent by construction.
    /// </summary>
    /// <param name="module">The module to scan.</param>
    /// <param name="context">
    /// Passed explicitly because ModuleRules.Context is protected.
    /// </param>
    /// <param name="toolchain">The located moc/uic/rcc executables.</param>
    /// <param name="env">
    /// The compile environment for the module. moc preprocesses, so it needs exactly the
    /// includes and defines the compiler receives - which is why one shared resolver exists.
    /// uic and rcc do not preprocess and are given neither.
    /// </param>
    public IReadOnlyList<CodeGenStep> CreateSteps(
        ModuleRules module, BuildContext context, QtToolchain toolchain, CompileEnvironment env)
    {
        if (module.QtVersion == QtVersion.None)
        {
            return [];
        }

        var steps = new List<CodeGenStep>();

        // Every generated output lands in one flat intermediate directory, so the three
        // generators share a claim table: two files producing the same output name would leave
        // one silently overwritten.
        var outputOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // EnableMoc/EnableUic/EnableRcc are honoured here as well as in the .vcxproj, where
        // they became QtMocEnabled/QtUicEnabled/QtRccEnabled. Reading them in only one of the
        // two places is how the build and the project file drift apart.
        if (module.EnableMoc)
        {
            AddMocSteps(steps, outputOwners, module, context, toolchain, env);
        }

        if (module.EnableUic)
        {
            AddUicSteps(steps, outputOwners, module, context, toolchain);
        }

        if (module.EnableRcc)
        {
            AddRccSteps(steps, outputOwners, module, context, toolchain);
        }

        return steps;
    }

    private static void AddMocSteps(
        List<CodeGenStep> steps,
        Dictionary<string, string> outputOwners,
        ModuleRules module,
        BuildContext context,
        QtToolchain toolchain,
        CompileEnvironment env)
    {
        var headers = Scan(module, context, HeaderExtensions).Where(ContainsQObject).ToList();
        var arguments = BuildMocArguments(env);
        var requested = RequestedOutputPaths(module, context, "moc_", ".cpp");
        var names = OutputNames(headers, "moc_", ".cpp", context, module);

        foreach (var header in headers)
        {
            var stem = Path.GetFileNameWithoutExtension(header);
            var included = requested.TryGetValue($"moc_{stem}.cpp", out var requestedPath);
            var output = included ? requestedPath! : names[header];
            Claim(outputOwners, module, output, header);

            steps.Add(new CodeGenStep
            {
                Description = $"moc {Path.GetFileName(header)}",
                Command = $"\"{toolchain.MocPath}\"{arguments} \"{header}\" -o \"$(IntermediateDir)/{output}\"",
                Inputs = { header },
                Outputs = { output },
                // A moc file the source #includes is part of that translation unit. Compiling it
                // as one of its own too is every symbol in it defined twice.
                CompileGeneratedSources = !included
            });
        }
    }

    private static void AddUicSteps(
        List<CodeGenStep> steps,
        Dictionary<string, string> outputOwners,
        ModuleRules module,
        BuildContext context,
        QtToolchain toolchain)
    {
        var forms = Scan(module, context, [".ui"]).ToList();
        var requested = RequestedOutputPaths(module, context, "ui_", ".h");
        var names = OutputNames(forms, "ui_", ".h", context, module);

        foreach (var form in forms)
        {
            // A header, not a translation unit: nothing compiles it, hand-written sources
            // include it, and the intermediate directory is already on the module's include
            // paths whenever any step is declared.
            var stem = Path.GetFileNameWithoutExtension(form);
            var output = requested.TryGetValue($"ui_{stem}.h", out var requestedPath)
                ? requestedPath
                : names[form];
            Claim(outputOwners, module, output, form);

            steps.Add(new CodeGenStep
            {
                Description = $"uic {Path.GetFileName(form)}",
                Command = $"\"{toolchain.UicPath}\" \"{form}\" -o \"$(IntermediateDir)/{output}\"",
                Inputs = { form },
                Outputs = { output }
            });
        }
    }

    private static void AddRccSteps(
        List<CodeGenStep> steps,
        Dictionary<string, string> outputOwners,
        ModuleRules module,
        BuildContext context,
        QtToolchain toolchain)
    {
        var resources = Scan(module, context, [".qrc"]).ToList();
        var names = OutputNames(resources, "qrc_", ".cpp", context, module);

        foreach (var resource in resources)
        {
            var output = names[resource];
            Claim(outputOwners, module, output, resource);

            // rcc derives the qInitResources_<name> symbol from -name, defaulting to the .qrc's
            // basename, which Q_INIT_RESOURCE(<basename>) callers rely on; two same-named .qrc
            // files in one module (AzQtComponents has Components/resources.qrc and Images/
            // resources.qrc) would define the same symbol twice, so a contested name carries the
            // directory qualifier its output file already does.
            var symbol = Path.GetFileNameWithoutExtension(output)["qrc_".Length..];
            var step = new CodeGenStep
            {
                Description = $"rcc {Path.GetFileName(resource)}",
                Command = $"\"{toolchain.RccPath}\" -name {symbol} \"{resource}\" -o \"$(IntermediateDir)/{output}\"",
                Inputs = { resource },
                Outputs = { output }
            };

            // The .qrc names the files it embeds, so they are real inputs: editing an embedded
            // resource has to rerun rcc, and tracking only the .qrc would never notice.
            foreach (var embedded in ReadEmbeddedFiles(resource, module))
            {
                step.Inputs.Add(embedded);
            }

            steps.Add(step);
        }
    }

    /// <summary>
    /// Where each generated file has to land, read from the #include directives that ask for it.
    ///
    /// This is not a preference: moc and uic output is included by path, and the path is part of
    /// the source. AzQtComponents writes `#include "Components/moc_TagSelector.cpp"` and
    /// AzToolsFramework writes `#include &lt;AzToolsFramework/AssetBrowser/Search/ui_FilterByWidget.h&gt;`,
    /// so one flat output directory satisfies neither. CMake's AUTOMOC does exactly this - the
    /// build tree has moc_TagSelector.cpp under an autogen `include_debug/Components/` and
    /// ui_FilterByWidget.h under `include/AzToolsFramework/AssetBrowser/Search/`.
    ///
    /// Keyed by file name, because that is what ties an include back to the .h or .ui that
    /// generates it. A name asked for from two different paths would be ambiguous; the last one
    /// wins and the Claim table catches the collision downstream.
    /// </summary>
    private static Dictionary<string, string> RequestedOutputPaths(
        ModuleRules module, BuildContext context, string prefix, string extension)
    {
        var requested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pattern = new System.Text.RegularExpressions.Regex(
            @"#\s*include\s*[<""]([^>""]*" +
            System.Text.RegularExpressions.Regex.Escape(prefix) +
            @"[^>""/]*" + System.Text.RegularExpressions.Regex.Escape(extension) + @")[>""]");

        // Headers as well as translation units: KeyQueryDPE.h includes its own ui_KeyQueryDPE.h,
        // so a scan of .cpp alone misses the path and uic writes the flat name nothing asks for.
        foreach (var source in Scan(module, context, [".cpp", ".cxx", ".mm", ".h", ".hpp", ".hxx"]))
        {
            string content;
            try
            {
                content = File.ReadAllText(source);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(content))
            {
                var path = match.Groups[1].Value.Replace('\\', '/');
                requested[Path.GetFileName(path)] = path;
            }
        }

        return requested;
    }

    /// <summary>
    /// Generated output name per input file, disambiguated only where it has to be.
    ///
    /// Inputs are globbed recursively but outputs land in one flat intermediate directory, so a
    /// basename appearing in two subdirectories would collide. That is an ordinary Qt layout -
    /// AzQtComponents ships Components/resources.qrc and Images/resources.qrc - so colliding
    /// names are qualified with their directory rather than rejected.
    ///
    /// Files whose basename is unique in the module keep the plain, conventional name; only a
    /// contested basename is qualified, and then for every file sharing it, so the result does
    /// not depend on which one was seen first.
    /// </summary>
    private static Dictionary<string, string> OutputNames(
        IReadOnlyList<string> files, string prefix, string extension, BuildContext context, ModuleRules module)
    {
        var moduleDirectory = Path.Combine(
            context.ProjectRoot, module.SourceDirectory ?? $"Source/{module.Name}");

        var contested = files
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var stem = Path.GetFileNameWithoutExtension(file);

            names[file] = contested.Contains(stem)
                ? $"{prefix}{Qualifier(file, moduleDirectory)}{stem}{extension}"
                : $"{prefix}{stem}{extension}";
        }

        return names;
    }

    /// <summary>
    /// A file's directory relative to its module, flattened into a name fragment
    /// ("Components/" becomes "Components_"). Empty when the file sits at the module root.
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
    /// The module's files with the given extensions, ordinal-sorted.
    ///
    /// The sort is a correctness requirement, not tidiness: unsorted scan output makes action
    /// digests unstable, which makes no-op rebuilds rerun forever.
    /// </summary>
    private static IEnumerable<string> Scan(ModuleRules module, BuildContext context, string[] extensions) =>
        module.ResolveSourceFiles(context.ProjectRoot, extensions)
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// Record which source file owns a generated output name, rejecting a second claimant.
    ///
    /// Files are globbed recursively but outputs land in one flat directory, so two same-named
    /// inputs in different subdirectories would collide and one would silently overwrite the
    /// other - surfacing much later, and far from the cause, as an unresolved symbol.
    /// </summary>
    private static void Claim(
        Dictionary<string, string> outputOwners, ModuleRules module, string output, string source)
    {
        if (outputOwners.TryGetValue(output, out var owner))
        {
            throw new InvalidOperationException(
                $"Module '{module.Name}' has two files that would both generate '{output}':" +
                $"{Environment.NewLine}  {owner}{Environment.NewLine}  {source}{Environment.NewLine}" +
                "Rename one, or split them into separate modules.");
        }

        outputOwners[output] = source;
    }

    /// <summary>
    /// The files a .qrc embeds, as absolute paths. Paths inside a .qrc are relative to the .qrc
    /// itself, not to the module root.
    /// </summary>
    private static IEnumerable<string> ReadEmbeddedFiles(string qrcPath, ModuleRules module)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(qrcPath);
        }
        catch (XmlException exception)
        {
            // A .qrc that cannot be parsed yields a step tracking only the .qrc, which would
            // then never rerun when an embedded file changed. Better to say so now.
            throw new InvalidOperationException(
                $"Module '{module.Name}': could not parse '{qrcPath}' to find the files it " +
                $"embeds - {exception.Message}", exception);
        }

        var directory = Path.GetDirectoryName(qrcPath)!;

        return document.Descendants("file")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Select(value => Path.GetFullPath(Path.Combine(directory, value)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildMocArguments(CompileEnvironment env)
    {
        var arguments = env.IncludePaths.Select(p => $" \"-I{p}\"")
            .Concat(env.Definitions.Select(d => $" \"-D{d}\""));

        return string.Concat(arguments);
    }

    /// <summary>
    /// Whether a header needs moc. Q_GADGET would also qualify, but the NightFox tree contains
    /// none, so only Q_OBJECT is matched; adding Q_GADGET later is additive.
    /// </summary>
    private static bool ContainsQObject(string headerPath)
    {
        try
        {
            return File.ReadAllText(headerPath).Contains("Q_OBJECT", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // A header that cannot be read cannot be scanned; leaving it out is the same
            // outcome as it containing nothing, and the compile will report it properly.
            return false;
        }
    }
}
