// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using System.CommandLine.Invocation;
using Omen.Core.Configuration;
using Omen.Core.Diagnostics;
using Omen.Executors.Orchestration;
using Spectre.Console;

namespace Omen.CLI.Commands;

/// <summary>Native test, install, package and target inspection commands.</summary>
public static class WorkflowCommands
{
    public static Command Check()
    {
        var command = new Command("check", "Probe the selected C++ toolchain by compiling, linking or running source");
        var common = new CommonOptions(command);
        var source = new Option<string?>("--source", "C++ source file to compile");
        var header = new Option<string?>("--header", "Header to check, e.g. vector");
        var include = new Option<string[]>("--include", () => [], "Include directory (repeatable)") { AllowMultipleArgumentsPerToken = false };
        var define = new Option<string[]>("--define", () => [], "Preprocessor definition (repeatable)") { AllowMultipleArgumentsPerToken = false };
        var flag = new Option<string[]>("--flag", () => [], "Compiler flag (repeatable)") { AllowMultipleArgumentsPerToken = false };
        var linkerFlag = new Option<string[]>("--linker-flag", () => [], "Linker flag (repeatable)") { AllowMultipleArgumentsPerToken = false };
        var library = new Option<string[]>("--library", () => [], "Library path (repeatable)") { AllowMultipleArgumentsPerToken = false };
        var standard = new Option<CppStandard>("--standard", () => CppStandard.Cpp20, "C++ language standard");
        var link = new Option<bool>("--link", "Also link an executable; source must define main");
        var run = new Option<bool>("--run", "Also run the linked program on this host");
        var timeout = new Option<double>("--timeout", () => 60, "Timeout for the entire check in seconds");
        var json = new Option<string?>("--json", "Write a JSON capability report");
        foreach (var option in new Option[] { source, header, include, define, flag, linkerFlag, library, standard, link, run, timeout, json }) command.AddOption(option);
        command.SetHandler(async (InvocationContext invocation) =>
        {
            var request = common.Read(invocation);
            var sourcePath = invocation.ParseResult.GetValueForOption(source);
            var headerName = invocation.ParseResult.GetValueForOption(header);
            if ((sourcePath == null) == (headerName == null)) throw new InvalidOperationException("Select exactly one of --source or --header.");
            if (headerName?.IndexOfAny(['\r', '\n', '<', '>']) >= 0) throw new InvalidOperationException("Invalid header name.");
            var platform = request.Platform ?? Omen.Platforms.PlatformFactory.GetHostPlatform();
            var architecture = request.Architecture ?? Omen.Platforms.PlatformFactory.GetDefaultArchitecture(platform);
            var context = request.Target == null ? new BuildContext
            {
                ProjectRoot = Path.GetFullPath(request.ProjectRoot), Platform = platform, Architecture = architecture, Configuration = request.Configuration,
                OutputDirectory = Path.Combine(Path.GetFullPath(request.ProjectRoot), "Binaries", $"{platform}_{request.Configuration}"),
                IntermediateDirectory = Path.Combine(Path.GetFullPath(request.ProjectRoot), "Intermediate", $"{platform}_{request.Configuration}")
            } : (await ProjectWorkflow.LoadAsync(request, invocation.GetCancellationToken())).Targets.Single().Context;
            var toolchain = Omen.Platforms.PlatformFactory.CreateToolchain(platform, architecture)
                ?? throw new InvalidOperationException($"No toolchain for {platform}/{architecture}.");
            var result = await new ToolchainProbe().RunAsync(new()
            {
                Context = context, Source = sourcePath != null ? await File.ReadAllTextAsync(Path.GetFullPath(sourcePath, context.ProjectRoot), invocation.GetCancellationToken())
                    : $"#include <{headerName}>\nint main() {{ return 0; }}\n",
                Link = invocation.ParseResult.GetValueForOption(link), Run = invocation.ParseResult.GetValueForOption(run),
                IncludePaths = (invocation.ParseResult.GetValueForOption(include) ?? []).ToList(),
                Definitions = (invocation.ParseResult.GetValueForOption(define) ?? []).ToList(),
                CompilerFlags = (invocation.ParseResult.GetValueForOption(flag) ?? []).ToList(),
                LinkerFlags = (invocation.ParseResult.GetValueForOption(linkerFlag) ?? []).ToList(),
                Libraries = (invocation.ParseResult.GetValueForOption(library) ?? []).ToList(),
                Standard = invocation.ParseResult.GetValueForOption(standard), TimeoutSeconds = invocation.ParseResult.GetValueForOption(timeout)
            }, toolchain, invocation.GetCancellationToken());
            AnsiConsole.WriteLine($"Check {(result.Success ? "passed" : "failed")}: {result.Compiler}");
            AnsiConsole.Write(result.Output);
            if (invocation.ParseResult.GetValueForOption(json) is { } report)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            invocation.ExitCode = result.Success ? 0 : 1;
        });
        return command;
    }

    public static Command Workflow()
    {
        var command = new Command("workflow", "Run an ordered preset workflow; stop at the first failed step");
        var name = new Argument<string?>("name", () => null, "Workflow name; omitted lists available workflows");
        command.AddArgument(name);
        command.SetHandler(async (InvocationContext context) =>
        {
            var presets = new ProjectPresets(Environment.CurrentDirectory);
            string? selected = context.ParseResult.GetValueForArgument(name);
            if (selected == null)
            {
                foreach (var workflow in presets.Workflows) AnsiConsole.WriteLine($"{workflow.Name}: {workflow.Description}");
                return;
            }
            var steps = presets.Workflow(selected).Steps;
            string[] allowed = ["configure", "build", "generate", "test", "install", "package", "targets", "check"];
            foreach (var step in steps)
                if (step.Count == 0 || !allowed.Contains(step[0], StringComparer.Ordinal))
                    throw new InvalidOperationException("Workflow steps must select configure, build, generate, test, install, package, targets or check.");
            foreach (var step in steps)
            {
                context.GetCancellationToken().ThrowIfCancellationRequested();
                AnsiConsole.WriteLine("Workflow: omen " + string.Join(" ", step));
                int code = await Program.Main(step.ToArray());
                if (code != 0) { context.ExitCode = code; return; }
            }
        });
        return command;
    }

    public static Command Test()
    {
        var command = new Command("test", "Run registered process tests and optionally write a JUnit report");
        var common = new CommonOptions(command);
        var filter = new Option<string?>(["--filter", "-R"], "Run names matching this regular expression");
        var exclude = new Option<string?>(["--exclude", "-E"], "Exclude names matching this regular expression");
        var label = new Option<string?>(["--label", "-L"], "Run tests with matching labels");
        var excludeLabel = new Option<string?>("--exclude-label", "Exclude tests with matching labels");
        var jobs = new Option<int>(["--jobs", "-j"], () => 1, "Maximum concurrent test processes");
        var timeout = new Option<double?>("--timeout", "Override each test's timeout in seconds");
        var list = new Option<bool>(["--list", "-N"], "List tests without running them");
        var noTests = new Option<bool>("--allow-no-tests", "Succeed when no tests match");
        var junit = new Option<string?>("--junit", "Write JUnit XML for CI");
        var build = new Option<bool>("--build", "Build the selected targets before running tests");
        foreach (var option in new Option[] { filter, exclude, label, excludeLabel, jobs, timeout, list, noTests, junit, build }) command.AddOption(option);
        command.SetHandler(async (InvocationContext context) =>
        {
            var request = common.Read(context);
            if (context.ParseResult.GetValueForOption(build) && !context.ParseResult.GetValueForOption(list) && request.OutputDirectory != null)
                throw new InvalidOperationException("--output-dir selects existing artifacts and cannot be combined with --build. Build first, then test that directory.");
            var workflow = await ProjectWorkflow.LoadAsync(request, context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(build) && !context.ParseResult.GetValueForOption(list))
            {
                foreach (var target in workflow.Targets)
                {
                    if (target.Rules.RuleFilePath == null) throw new InvalidOperationException("Target has no declaring rule file.");
                    var result = await new BuildOrchestrator().BuildAsync(new()
                    {
                        TargetFile = target.Rules.RuleFilePath, Platform = target.Context.Platform,
                        Architecture = target.Context.Architecture, Configuration = target.Context.Configuration,
                        Jobs = context.ParseResult.GetValueForOption(jobs),
                        DimensionFilter = target.Context.AdditionalDimensions.ToDictionary(d => d.Key, d => (IReadOnlyList<Enum>)new[] { d.Value })
                    }, Events, null, context.GetCancellationToken());
                    if (result?.Success != true) { context.ExitCode = 1; return; }
                }
            }
            var summary = await new TestOrchestrator().RunAsync(new()
            {
                ProjectRoot = workflow.Root, Tests = workflow.Tests(),
                NamePattern = context.ParseResult.GetValueForOption(filter), ExcludePattern = context.ParseResult.GetValueForOption(exclude),
                LabelPattern = context.ParseResult.GetValueForOption(label), ExcludeLabelPattern = context.ParseResult.GetValueForOption(excludeLabel),
                Jobs = context.ParseResult.GetValueForOption(jobs), TimeoutSeconds = context.ParseResult.GetValueForOption(timeout),
                ListOnly = context.ParseResult.GetValueForOption(list), AllowNoTests = context.ParseResult.GetValueForOption(noTests),
                JunitPath = context.ParseResult.GetValueForOption(junit)
            }, Events, context.GetCancellationToken());
            foreach (var result in summary.Results)
            {
                AnsiConsole.WriteLine($"{result.Status}: {result.Name} ({result.Seconds:F2}s){(result.Reason == null ? "" : " - " + result.Reason)}");
                if (result.Status == "Failed") { AnsiConsole.Write(result.Output); AnsiConsole.Write(result.Error); }
            }
            AnsiConsole.WriteLine($"{summary.Results.Count(r => r.Status == "Passed")} passed, {summary.Results.Count(r => r.Status == "Failed")} failed, {summary.Results.Count(r => r.Status == "Skipped")} skipped, {summary.Results.Count} selected");
            context.ExitCode = summary.Success ? 0 : 1;
        });
        return command;
    }

    public static Command Install(bool package)
    {
        var command = new Command(package ? "package" : "install", package ? "Create a relocatable ZIP package from install rules" : "Install declared files under a prefix");
        var common = new CommonOptions(command);
        var component = new Option<string?>("--component", "Install/package only the named component");
        var dryRun = new Option<bool>("--dry-run", "Validate and list files without writing output");
        var destination = new Option<string>(package ? "--output" : "--prefix", package ? "ZIP archive path" : "Installation prefix") { IsRequired = true };
        command.AddOption(component); command.AddOption(dryRun); command.AddOption(destination);
        command.SetHandler(async (InvocationContext context) =>
        {
            var workflow = await ProjectWorkflow.LoadAsync(common.Read(context), context.GetCancellationToken());
            var installer = new InstallOrchestrator();
            var plan = installer.Plan(workflow.Root, workflow.InstallRules(), context.ParseResult.GetValueForOption(component));
            if (plan.Count == 0) throw new InvalidOperationException("No installation rules produced files. Declare InstallRules on the project or target.");
            foreach (var file in plan) AnsiConsole.WriteLine($"{file.Component}: {file.Source} -> {file.Destination}");
            if (!context.ParseResult.GetValueForOption(dryRun))
            {
                string path = context.ParseResult.GetValueForOption(destination)!;
                if (package) await installer.PackageAsync(plan, path, workflow.Name, workflow.Version, context.GetCancellationToken());
                else await installer.InstallAsync(plan, path, workflow.Name, workflow.Version, context.GetCancellationToken());
            }
            AnsiConsole.WriteLine($"{plan.Count} file(s) {(context.ParseResult.GetValueForOption(dryRun) ? "planned" : package ? "packaged" : "installed")}");
        });
        return command;
    }

    public static Command Targets()
    {
        var command = new Command("targets", "List discovered target names and their supported build matrices");
        var root = new Option<string>("--project", () => Environment.CurrentDirectory, "Project root");
        command.AddOption(root);
        command.SetHandler(async (InvocationContext context) =>
        {
            string path = Path.GetFullPath(context.ParseResult.GetValueForOption(root)!);
            var compiled = await new Omen.Core.Rules.RuleCompiler(Omen.Core.Rules.RuleCompiler.CacheDirectoryFor(path)).CompileRulesAsync(path, context.GetCancellationToken());
            foreach (var target in compiled.FindTargetTypes().OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var matrix = compiled.GetSupportedTargets(target);
                AnsiConsole.WriteLine(target.Name.Replace("Target", "") + (matrix == null ? " (default configuration)" : "\n  " + string.Join("\n  ", matrix.Resolve())));
            }
        });
        return command;
    }

    private static readonly IProgress<OrchestratorEvent> Events = new SynchronousProgress<OrchestratorEvent>(e =>
        AnsiConsole.WriteLine(e.Message));

    private sealed class CommonOptions
    {
        private readonly Argument<string?> target = new("target", () => null, "Optional target name; omitted selects all targets supporting the configuration");
        private readonly Option<string> root = new("--project", () => Environment.CurrentDirectory, "Project root");
        private readonly Option<TargetPlatform?> platform = new(["--platform", "-p"], "Target platform");
        private readonly Option<TargetArchitecture?> architecture = new(["--arch", "-a"], "Target architecture");
        private readonly Option<BuildConfiguration> configuration = new(["--configuration", "-c"], () => BuildConfiguration.Development, "Build configuration");
        private readonly Option<string?> output = new("--output-dir", "Override the built artifact directory");
        private readonly Option<string[]> dimensions = new("--dim", () => [], "Custom dimension Name=Value (repeatable)") { AllowMultipleArgumentsPerToken = false };

        public CommonOptions(Command command)
        {
            command.AddArgument(target);
            foreach (var option in new Option[] { root, platform, architecture, configuration, output, dimensions }) command.AddOption(option);
        }

        public ProjectWorkflowRequest Read(InvocationContext context)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in context.ParseResult.GetValueForOption(dimensions) ?? [])
            {
                var pair = entry.Split('=', 2);
                if (pair.Length != 2 || pair[0].Length == 0) throw new InvalidOperationException("--dim expects Name=Value.");
                values.Add(pair[0], pair[1]);
            }
            return new()
            {
                ProjectRoot = context.ParseResult.GetValueForOption(root)!, Target = context.ParseResult.GetValueForArgument(target),
                Platform = context.ParseResult.GetValueForOption(platform), Architecture = context.ParseResult.GetValueForOption(architecture),
                Configuration = context.ParseResult.GetValueForOption(configuration), OutputDirectory = context.ParseResult.GetValueForOption(output),
                Dimensions = values
            };
        }
    }
}
