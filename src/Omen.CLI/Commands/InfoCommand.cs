// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using Omen.Platforms;
using Spectre.Console;

namespace Omen.CLI.Commands;

public static class InfoCommand
{
    public static Command Create()
    {
        var command = new Command("info", "Display system and environment information");
        
        command.SetHandler(async (context) =>
        {
            AnsiConsole.Write(new FigletText("OMEN").Color(Color.Orange1));
            
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            AnsiConsole.MarkupLine($"[bold]Omen Build System[/] v{version}\n");
            
            // System info
            var sysTable = new Table().Title("[bold]System Information[/]").Border(TableBorder.Rounded);
            sysTable.AddColumn("Property");
            sysTable.AddColumn("Value");
            
            sysTable.AddRow("OS", RuntimeInformation.OSDescription);
            sysTable.AddRow("Architecture", RuntimeInformation.OSArchitecture.ToString());
            sysTable.AddRow("Processor Count", Environment.ProcessorCount.ToString());
            sysTable.AddRow(".NET Version", RuntimeInformation.FrameworkDescription);
            sysTable.AddRow("Working Directory", Environment.CurrentDirectory);
            
            AnsiConsole.Write(sysTable);
            AnsiConsole.WriteLine();
            
            // Detect available toolchains
            var toolTable = new Table().Title("[bold]Available Toolchains[/]").Border(TableBorder.Rounded);
            toolTable.AddColumn("Platform");
            toolTable.AddColumn("Status");
            toolTable.AddColumn("Details");
            
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Detecting toolchains...", async ctx =>
                {
                    await Task.Yield();
                    
                    // Windows / MSVC
                    try
                    {
                        var windowsSdk = PlatformFactory.GetSDK(Core.Configuration.TargetPlatform.Windows);
                        var info = windowsSdk?.Detect();
                        var isValid = info != null && !string.IsNullOrEmpty(info.InstallPath);
                        toolTable.AddRow(
                            "Windows (MSVC)",
                            isValid ? "[green]✓ Available[/]" : "[red]✗ Not found[/]",
                            isValid ? $"VS {info!.Version} @ {TruncatePath(info.InstallPath)}" : "Install Visual Studio");
                    }
                    catch
                    {
                        toolTable.AddRow("Windows (MSVC)", "[dim]N/A[/]", "Windows only");
                    }
                    
                    // Linux / Clang
                    try
                    {
                        var linuxSdk = PlatformFactory.GetSDK(Core.Configuration.TargetPlatform.Linux);
                        var info = linuxSdk?.Detect();
                        var isValid = info != null && !string.IsNullOrEmpty(info.InstallPath);
                        toolTable.AddRow(
                            "Linux (Clang)",
                            isValid ? "[green]✓ Available[/]" : "[red]✗ Not found[/]",
                            isValid ? $"Clang @ {TruncatePath(info!.InstallPath)}" : "Install clang");
                    }
                    catch
                    {
                        toolTable.AddRow("Linux (Clang)", "[dim]N/A[/]", "Linux only");
                    }
                    
                    // Android NDK
                    try
                    {
                        var androidSdk = PlatformFactory.GetSDK(Core.Configuration.TargetPlatform.Android);
                        var info = androidSdk?.Detect();
                        var isValid = info != null && !string.IsNullOrEmpty(info.InstallPath);
                        toolTable.AddRow(
                            "Android (NDK)",
                            isValid ? "[green]✓ Available[/]" : "[yellow]○ Optional[/]",
                            isValid ? $"NDK @ {TruncatePath(info!.InstallPath)}" : "Set ANDROID_NDK_HOME");
                    }
                    catch
                    {
                        toolTable.AddRow("Android (NDK)", "[yellow]○ Not configured[/]", "Set ANDROID_NDK_HOME");
                    }
                    
                    // iOS / Xcode
                    try
                    {
                        var appleSdk = PlatformFactory.GetSDK(Core.Configuration.TargetPlatform.iOS);
                        var info = appleSdk?.Detect();
                        var isValid = info != null && !string.IsNullOrEmpty(info.InstallPath);
                        toolTable.AddRow(
                            "iOS (Xcode)",
                            isValid ? "[green]✓ Available[/]" : "[dim]N/A[/]",
                            isValid ? $"Xcode @ {TruncatePath(info!.InstallPath)}" : "macOS only");
                    }
                    catch
                    {
                        toolTable.AddRow("iOS (Xcode)", "[dim]N/A[/]", "macOS only");
                    }
                });
            
            AnsiConsole.Write(toolTable);
            AnsiConsole.WriteLine();
            
            // Project info if in a project
            var targetFiles = Directory.GetFiles(Environment.CurrentDirectory, "*.target.cs", SearchOption.TopDirectoryOnly);
            var moduleFiles = Directory.GetFiles(Environment.CurrentDirectory, "*.module.cs", SearchOption.AllDirectories);
            
            if (targetFiles.Length > 0 || moduleFiles.Length > 0)
            {
                var projTable = new Table().Title("[bold]Project Information[/]").Border(TableBorder.Rounded);
                projTable.AddColumn("Item");
                projTable.AddColumn("Count");
                
                projTable.AddRow("Target Files", targetFiles.Length.ToString());
                projTable.AddRow("Module Files", moduleFiles.Length.ToString());
                
                AnsiConsole.Write(projTable);
                AnsiConsole.WriteLine();
                
                if (targetFiles.Length > 0)
                {
                    AnsiConsole.MarkupLine("[bold]Targets:[/]");
                    foreach (var target in targetFiles)
                    {
                        AnsiConsole.MarkupLine($"  • {Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(target)).EscapeMarkup()}");
                    }
                }
            }
            
            context.ExitCode = 0;
        });
        
        return command;
    }
    
    private static string TruncatePath(string path, int maxLength = 50)
    {
        if (path.Length <= maxLength)
            return path;
        
        return "..." + path[^(maxLength - 3)..];
    }
}
