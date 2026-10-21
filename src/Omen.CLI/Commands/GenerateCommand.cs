using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using Omen.Core.Diagnostics;
using Omen.Executors.Orchestration;
using Spectre.Console;

namespace Omen.CLI.Commands;

public static class GenerateCommand
{
	public static Command Create()
	{
		Command command = new Command("generate", "Generate project files or build scripts");
		command.AddCommand(CreateProjectFilesCommand());
		command.AddCommand(CreateModuleCommand());
		command.AddCommand(CreateTargetCommand());
		return command;
	}

	private static Command CreateProjectFilesCommand()
	{
		Command command = new Command("project", "Generate IDE project files");
		Option<string> ideOption = new Option<string>(new string[2] { "--ide", "-i" }, () => "vs2022", "IDE to generate for (vs2019, vs2022, vs2026, vscode, rider, cmake)");
		command.AddOption(ideOption);
		command.SetHandler(async (InvocationContext context) =>
		{
			context.ExitCode = await GenerateProjectFilesAsync(context.ParseResult.GetValueForOption(ideOption)?.ToLowerInvariant() ?? "vs2022");
		});
		return command;
	}

	private static async Task<int> GenerateProjectFilesAsync(string ide)
	{
		IdeKind? ideKind = ide switch
		{
			"vs2019" => (IdeKind?)IdeKind.VS2019, 
			"vs2022" => IdeKind.VS2022, 
			"vs2026" => IdeKind.VS2026, 
			"vscode" => IdeKind.VSCode, 
			"cmake" => IdeKind.CMake, 
			"rider" => IdeKind.Rider, 
			_ => null, 
		};
		if (!ideKind.HasValue)
		{
			AnsiConsole.MarkupLine("[red]Error:[/] Unknown IDE '" + ide.EscapeMarkup() + "'");
			AnsiConsole.MarkupLine("[dim]Supported: vs2019, vs2022, vs2026, vscode, cmake, rider[/]");
			return 1;
		}
		return (!(await new ProjectGenerationOrchestrator().GenerateAsync(new ProjectGenerationOrchestratorRequest
		{
			ProjectRoot = Environment.CurrentDirectory,
			Ide = ideKind.Value
		}, new SynchronousProgress<OrchestratorEvent>(RenderEvent)))) ? 1 : 0;
	}

	private static void RenderEvent(OrchestratorEvent evt)
	{
		string text = evt.Message.EscapeMarkup();
		switch (evt.Level)
		{
		case OrchestratorEventLevel.Error:
			AnsiConsole.MarkupLine("[red]Error:[/] " + text);
			break;
		case OrchestratorEventLevel.Warning:
			AnsiConsole.MarkupLine("[yellow]" + text + "[/]");
			break;
		case OrchestratorEventLevel.Success:
			AnsiConsole.MarkupLine("[green]✓[/] " + text);
			break;
		default:
			AnsiConsole.MarkupLine("[dim]" + text + "[/]");
			break;
		}
	}

	private static Command CreateModuleCommand()
	{
		Command command = new Command("module", "Generate a new module");
		Argument<string> nameArgument = new Argument<string>("name", "The name of the module");
		Option<string> typeOption = new Option<string>(new string[2] { "--type", "-t" }, () => "Runtime", "Module type (Runtime, Developer, Editor, ThirdParty)");
		Option<string?> pathOption = new Option<string>(new string[2] { "--path", "-p" }, "Directory to create the module in");
		command.AddArgument(nameArgument);
		command.AddOption(typeOption);
		command.AddOption(pathOption);
		command.SetHandler(async (InvocationContext context) =>
		{
			string name = context.ParseResult.GetValueForArgument(nameArgument);
			string type = context.ParseResult.GetValueForOption(typeOption) ?? "Runtime";
			string path = context.ParseResult.GetValueForOption(pathOption) ?? Path.Combine(Environment.CurrentDirectory, "Source", name);
			context.ExitCode = await GenerateModuleAsync(name, type, path);
		});
		return command;
	}

	private static Command CreateTargetCommand()
	{
		Command command = new Command("target", "Generate a new target");
		Argument<string> nameArgument = new Argument<string>("name", "The name of the target");
		Option<string> typeOption = new Option<string>(new string[2] { "--type", "-t" }, () => "Executable", "Target type (Executable, SharedLibrary, StaticLibrary)");
		command.AddArgument(nameArgument);
		command.AddOption(typeOption);
		command.SetHandler(async (InvocationContext context) =>
		{
			string valueForArgument = context.ParseResult.GetValueForArgument(nameArgument);
			string type = context.ParseResult.GetValueForOption(typeOption) ?? "Executable";
			context.ExitCode = await GenerateTargetAsync(valueForArgument, type);
		});
		return command;
	}

	private static async Task<int> GenerateModuleAsync(string name, string type, string path)
	{
		AnsiConsole.MarkupLine("[blue]Generating module '" + name.EscapeMarkup() + "'...[/]");
		Directory.CreateDirectory(path);
		Directory.CreateDirectory(Path.Combine(path, "Public"));
		Directory.CreateDirectory(Path.Combine(path, "Private"));
		string moduleContent = $"// {name} Module Build Rules\r\n\r\nusing Omen.Core.Configuration;\r\nusing Omen.Core.Rules;\r\n\r\npublic class {name}Module : ModuleRules\r\n{{\r\n    public {name}Module(BuildContext context) : base(context)\r\n    {{\r\n        Type = ModuleType.{type};\r\n        SourceDirectory = \"Source/{name}\";\r\n\r\n        PublicIncludePaths.Add(\"Public\");\r\n        PrivateIncludePaths.Add(\"Private\");\r\n\r\n        // Add your dependencies here\r\n        // PublicDependencies.Add(\"Core\");\r\n\r\n        // Add your definitions here\r\n        // PublicDefinitions.Add(\"MY_DEFINE=1\");\r\n\r\n        // Compiler settings\r\n        CppStandard = CppStandard.Cpp20;\r\n        EnableExceptions = true;\r\n        EnableRTTI = false;\r\n    }}\r\n}}";
		await File.WriteAllTextAsync(Path.Combine(path, name + ".module.cs"), moduleContent);
		string headerContent = $"// {name} Module\r\n\r\n#pragma once\r\n\r\n// Public API header for {name} module";
		await File.WriteAllTextAsync(Path.Combine(path, "Public", name + ".h"), headerContent);
		string sourceContent = $"// {name} Module\r\n\r\n#include \"{name}.h\"\r\n\r\n// Implementation";
		await File.WriteAllTextAsync(Path.Combine(path, "Private", name + ".cpp"), sourceContent);
		AnsiConsole.MarkupLine("[green]✓[/] Created module at " + path.EscapeMarkup());
		AnsiConsole.MarkupLine("  - [dim]Public/[/] (public headers)");
		AnsiConsole.MarkupLine("  - [dim]Private/[/] (implementation)");
		AnsiConsole.MarkupLine("  - [dim]" + name.EscapeMarkup() + ".module.cs[/] (build rules)");
		return 0;
	}

	private static async Task<int> GenerateTargetAsync(string name, string type)
	{
		AnsiConsole.MarkupLine("[blue]Generating target '" + name.EscapeMarkup() + "'...[/]");
		string targetContent = $"// {name} Target Build Rules\r\n\r\nusing Omen.Core.Configuration;\r\nusing Omen.Core.Rules;\r\n\r\npublic class {name}Target : TargetRules\r\n{{\r\n    public {name}Target(BuildContext context) : base(context)\r\n    {{\r\n        Type = TargetType.{type};\r\n\r\n        // Supported platforms\r\n        SupportedPlatforms.Add(TargetPlatform.Windows);\r\n        SupportedPlatforms.Add(TargetPlatform.Linux);\r\n\r\n        // Build settings\r\n        UsePCHFiles = true;\r\n        UseUnityBuild = true;\r\n\r\n        // Enable LTO for shipping builds\r\n        ConfigureForConfiguration(BuildConfiguration.Shipping, () =>\r\n        {{\r\n            EnableLTO = true;\r\n        }});\r\n\r\n        // Add your modules here\r\n        // ExtraModules.Add(\"{name}\");\r\n    }}\r\n}}";
		string targetPath = Path.Combine(Environment.CurrentDirectory, name + ".target.cs");
		await File.WriteAllTextAsync(targetPath, targetContent);
		AnsiConsole.MarkupLine("[green]✓[/] Created target at " + targetPath.EscapeMarkup());
		return 0;
	}
}
