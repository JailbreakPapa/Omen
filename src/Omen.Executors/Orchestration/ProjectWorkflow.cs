// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Options;
using Omen.Core.Rules;
using Omen.Platforms;

namespace Omen.Executors.Orchestration;

public sealed class ProjectWorkflowRequest
{
    public required string ProjectRoot { get; init; }
    public string? Target { get; init; }
    public TargetPlatform? Platform { get; init; }
    public TargetArchitecture? Architecture { get; init; }
    public BuildConfiguration Configuration { get; init; } = BuildConfiguration.Development;
    public string? OutputDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Dimensions { get; init; } = new Dictionary<string, string>();
}

public sealed record WorkflowTarget(TargetRules Rules, BuildContext Context);

/// <summary>A selected configuration of compiled project rules, shared by tests, install, package and inspection.</summary>
public sealed class ProjectWorkflow
{
    public required string Root { get; init; }
    public required CompiledRules Compiled { get; init; }
    public required IReadOnlyList<WorkflowTarget> Targets { get; init; }
    public required BuildContext BaseContext { get; init; }
    public string Name => Compiled.Project?.Name ?? Path.GetFileName(Root);
    public string Version => Compiled.Project?.Version ?? "0.0.0";

    public static async Task<ProjectWorkflow> LoadAsync(ProjectWorkflowRequest request, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(request.ProjectRoot);
        var compiled = await new RuleCompiler(RuleCompiler.CacheDirectoryFor(root)).CompileRulesAsync(root, ct);
        var platform = request.Platform ?? PlatformFactory.GetHostPlatform();
        var architecture = request.Architecture ?? PlatformFactory.GetDefaultArchitecture(platform);
        var options = new OptionCacheStore(Path.Combine(root, "Intermediate", "omen-cache.json")).Load();
        var dimensions = new Dictionary<Type, Enum>();
        foreach (var (dimension, value) in request.Dimensions)
        {
            var dimensionType = DimensionLookup.FindDimensionType(compiled.Assembly, dimension)
                ?? throw new InvalidOperationException($"Unknown dimension '{dimension}'.");
            if (!DimensionLookup.TryParseValue(dimensionType, value, out var parsed))
                throw new InvalidOperationException($"Invalid value '{value}' for dimension '{dimension}'.");
            dimensions[dimensionType] = parsed!;
        }
        var targets = new List<WorkflowTarget>();
        var declaringTypes = request.Target?.EndsWith(".target.cs", StringComparison.OrdinalIgnoreCase) == true
            ? compiled.FindTargetTypes(Path.GetFullPath(request.Target, root)).ToHashSet() : null;
        bool matched = request.Target == null;
        foreach (var type in compiled.FindTargetTypes())
        {
            var matrix = compiled.GetSupportedTargets(type);
            var rows = matrix?.Resolve().Where(r => r.Get<TargetPlatform>() == platform &&
                r.Get<TargetArchitecture>() == architecture && r.Get<BuildConfiguration>() == request.Configuration).ToList();
            if (rows != null)
            {
                foreach (var (dimensionType, value) in dimensions)
                {
                    rows = rows.Where(r => r.Values.TryGetValue(dimensionType, out var actual) && actual.Equals(value)).ToList();
                }
            }
            // Name convention is the same one TargetRules itself uses.
            string name = type.Name.Replace("Target", "");
            bool explicitMatch = request.Target != null && (declaringTypes?.Contains(type) == true || name.Equals(request.Target, StringComparison.OrdinalIgnoreCase) ||
                type.Name.Equals(request.Target, StringComparison.OrdinalIgnoreCase));
            if (request.Target != null && !explicitMatch) continue;
            matched |= explicitMatch;
            if (rows?.Count == 0)
            {
                if (explicitMatch) throw new InvalidOperationException($"Target '{name}' does not support {platform}/{architecture}/{request.Configuration}.");
                continue;
            }
            if (rows?.Count > 1) throw new InvalidOperationException($"Target '{name}' has multiple matching custom-dimension rows; specify --dim Name=Value.");
            var context = new BuildContext
            {
                Platform = platform, Architecture = architecture, Configuration = request.Configuration,
                AdditionalDimensions = rows?.Single().Values.Where(v => v.Key != typeof(TargetPlatform) &&
                    v.Key != typeof(TargetArchitecture) && v.Key != typeof(BuildConfiguration)).ToDictionary(v => v.Key, v => v.Value)
                    ?? dimensions,
                ProjectRoot = root, CachedOptionValues = options,
                IntermediateDirectory = Path.Combine(root, "Intermediate", $"{platform}_{request.Configuration}"),
                OutputDirectory = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(root, "Binaries", $"{platform}_{request.Configuration}"), root)
            };
            // GetTarget uses the recovered constructor and project-options merge path.
            var target = compiled.GetTarget(name, context) ?? throw new InvalidOperationException($"Cannot instantiate target '{name}'.");
            targets.Add(new(target, context));
        }
        if (!matched) throw new InvalidOperationException($"Unknown target '{request.Target}'.");
        return new()
        {
            Root = root, Compiled = compiled, Targets = targets,
            BaseContext = new BuildContext
            {
                ProjectRoot = root, Platform = platform, Architecture = architecture, Configuration = request.Configuration,
                AdditionalDimensions = dimensions,
                CachedOptionValues = options,
                IntermediateDirectory = Path.Combine(root, "Intermediate", $"{platform}_{request.Configuration}"),
                OutputDirectory = Path.GetFullPath(request.OutputDirectory ?? Path.Combine(root, "Binaries", $"{platform}_{request.Configuration}"), root)
            }
        };
    }

    public IReadOnlyList<TestDefinition> Tests()
    {
        var tests = new List<TestDefinition>();
        if (Compiled.Project != null)
        {
            var context = DefaultContext();
            tests.AddRange(Compiled.Project.Tests.Select(t => Expand(t, context, null)));
        }
        foreach (var target in Targets) tests.AddRange(target.Rules.Tests.Select(t => Expand(t, target.Context, target.Rules)));
        return tests;
    }

    public IReadOnlyList<InstallRule> InstallRules()
    {
        var rules = new List<InstallRule>();
        if (Compiled.Project != null)
        {
            var context = DefaultContext();
            rules.AddRange(Compiled.Project.InstallRules.Select(r => Expand(r, context, null)));
        }
        foreach (var target in Targets) rules.AddRange(target.Rules.InstallRules.Select(r => Expand(r, target.Context, target.Rules)));
        return rules;
    }

    public int ConfigureFiles()
    {
        int changed = ConfigureFiles(BaseContext, Compiled.Project, null);
        foreach (var target in Targets) changed += ConfigureFiles(target.Context, Compiled.Project, target.Rules, includeProject: false);
        return changed;
    }

    public static int ConfigureFiles(BuildContext context, OmenProjectRules? project, TargetRules? target, bool includeProject = true)
    {
        int changed = 0;
        void Generate(IEnumerable<ConfiguredFile> files, TargetRules? owner)
        {
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in project?.Options ?? []) variables[item.Name] = item.EffectiveValue;
            foreach (var item in context.DeclaredOptions) variables[item.Name] = item.EffectiveValue;
            foreach (var item in context.CachedOptionValues) variables[item.Key] = item.Value;
            variables["ProjectName"] = project?.Name ?? Path.GetFileName(context.ProjectRoot);
            variables["ProjectVersion"] = project?.Version ?? "0.0.0";
            variables["Configuration"] = context.Configuration.ToString();
            variables["Platform"] = context.Platform.ToString();
            variables["Architecture"] = context.Architecture.ToString();
            if (owner != null) variables["TargetName"] = owner.Name;
            foreach (var file in files)
            {
                var values = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                foreach (var item in file.Variables) values[item.Key] = Expand(item.Value, context, owner);
                string input = Path.GetFullPath(Expand(file.Input, context, owner), context.ProjectRoot);
                string output = Path.GetFullPath(Expand(file.Output, context, owner), context.ProjectRoot);
                if (Omen.Core.Generators.ConfigurationFileGenerator.Generate(input, output, values, file.AtOnly, file.Strict,
                    file.NewlineStyle, file.EscapeQuotes, file.CopyOnly)) changed++;
            }
        }
        if (project != null && includeProject) Generate(project.ConfigureFiles, null);
        if (target != null) Generate(target.ConfigureFiles, target);
        return changed;
    }

    private BuildContext DefaultContext() => Targets.FirstOrDefault()?.Context ?? BaseContext;

    public static string Expand(string value, BuildContext context, TargetRules? target)
    {
        if (target == null && value.Contains("$(TargetFile)", StringComparison.Ordinal))
            throw new InvalidOperationException("$(TargetFile) belongs in a target rule, not a project-wide rule.");
        string extension = target?.Type switch
        {
            TargetType.SharedLibrary => context.Platform == TargetPlatform.Windows ? ".dll" : context.Platform is TargetPlatform.Mac or TargetPlatform.iOS ? ".dylib" : ".so",
            TargetType.StaticLibrary => context.Platform == TargetPlatform.Windows ? ".lib" : ".a",
            _ => context.Platform == TargetPlatform.Windows ? ".exe" : ""
        };
        string directory = Path.GetFullPath(target?.OutputDirectory ?? context.OutputDirectory, context.ProjectRoot);
        return value.Replace("$(ProjectDir)", context.ProjectRoot).Replace("$(OutputDir)", directory)
            .Replace("$(IntermediateDir)", context.IntermediateDirectory).Replace("$(Configuration)", context.Configuration.ToString())
            .Replace("$(Platform)", context.Platform.ToString()).Replace("$(Architecture)", context.Architecture.ToString())
            .Replace("$(TargetName)", target?.Name ?? "").Replace("$(TargetFile)", Path.Combine(directory, (target?.OutputName ?? target?.Name ?? "") + extension));
    }

    private static TestDefinition Expand(TestDefinition test, BuildContext context, TargetRules? target) => new()
    {
        Name = test.Name, Command = Expand(test.Command, context, target),
        Arguments = test.Arguments.Select(a => Expand(a, context, target)).ToList(),
        WorkingDirectory = Expand(test.WorkingDirectory ?? "$(ProjectDir)", context, target),
        Environment = test.Environment.ToDictionary(v => v.Key, v => v.Value == null ? null : Expand(v.Value, context, target)),
        TimeoutSeconds = test.TimeoutSeconds, ExpectedExitCode = test.ExpectedExitCode, SkipExitCode = test.SkipExitCode,
        Disabled = test.Disabled, RunSerial = test.RunSerial, Labels = test.Labels, DependsOn = test.DependsOn,
        PassRegularExpression = test.PassRegularExpression, FailRegularExpression = test.FailRegularExpression
    };

    private static InstallRule Expand(InstallRule rule, BuildContext context, TargetRules? target) => new()
    {
        Source = Expand(rule.Source, context, target), Destination = Expand(rule.Destination, context, target),
        Component = rule.Component, Directory = rule.Directory, Optional = rule.Optional, Rename = rule.Rename,
        Patterns = rule.Patterns, ExcludePatterns = rule.ExcludePatterns
    };
}
