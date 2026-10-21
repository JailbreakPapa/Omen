// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using Omen.Core.Configuration;
using Omen.Core.Rules;
using Omen.Executors.Server;
using Spectre.Console;

namespace Omen.CLI.Commands;

/// <summary>The background build server <c>omen build</c> starts on demand; not meant to be run by hand.</summary>
public static class ServerCommand
{
    public static string Version =>
        typeof(ServerCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static Command Create()
    {
        var command = new Command("server", "Run the background build server (omen build starts it)") { IsHidden = true };
        var rootOption = new Option<string?>("--root", "Project root (default: the one containing the current directory)");
        var configOption = new Option<string>(["--configuration", "-c"], () => "Development", "Configuration this server builds");
        var jobsOption = new Option<int?>(["--jobs", "-j"], "Build slots shared by every client (default: cores - 2)");
        var idleOption = new Option<int>("--idle-minutes", () => 30, "Exit after this many minutes without a request");
        var stopOption = new Option<bool>("--stop", "Ask the server for this root and configuration to finish and exit");
        var statusOption = new Option<bool>("--status", "Print the server's pool usage");
        foreach (var option in new Option[] { rootOption, configOption, jobsOption, idleOption, stopOption, statusOption })
            command.AddOption(option);

        command.SetHandler(async context =>
        {
            // ResolveProjectRoot starts from a file's directory; name a file in the current one.
            var root = Path.GetFullPath(context.ParseResult.GetValueForOption(rootOption)
                ?? RuleCompiler.ResolveProjectRoot(Path.Combine(Directory.GetCurrentDirectory(), "omen")));
            var config = context.ParseResult.GetValueForOption(configOption)!;
            if (context.ParseResult.GetValueForOption(stopOption) || context.ParseResult.GetValueForOption(statusOption))
            {
                var reply = await SendControlAsync(root, config, context.ParseResult.GetValueForOption(stopOption) ? "stop" : "status");
                AnsiConsole.WriteLine(reply ?? "No Omen server running for this root and configuration.");
                return;
            }
            var slots = context.ParseResult.GetValueForOption(jobsOption) ?? Math.Max(1, Environment.ProcessorCount - 2);
            var idle = TimeSpan.FromMinutes(context.ParseResult.GetValueForOption(idleOption));
            StartTray();
            await new BuildServer(root, config, Version, slots, idle).RunAsync(default);
        });
        return command;
    }

    /// <summary>The server's reply text, or null when none is running.</summary>
    private static async Task<string?> SendControlAsync(string root, string config, string kind)
    {
        await using var pipe = new NamedPipeClientStream(".", ServerProtocol.PipeName(root, config), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(500);
        }
        catch (TimeoutException)
        {
            return null;
        }
        await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = "hello", Version = Version }, default);
        await ServerProtocol.WriteAsync(pipe, new ServerMessage { Kind = kind }, default);
        return (await ServerProtocol.ReadAsync(pipe, default))?.Text;
    }

    /// <summary>
    /// Stops every configuration's server for <paramref name="root"/> and waits for them to let go of
    /// Intermediate/ - <c>omen clean</c> cannot delete a directory a server holds its lock file in.
    /// </summary>
    public static async Task StopServersAsync(string root)
    {
        foreach (var configuration in Enum.GetNames<BuildConfiguration>())
        {
            if (await SendControlAsync(root, configuration, "stop") == null)
                continue;
            var lockFile = Path.Combine(root, "Intermediate", $".omen-server-{configuration}.lock");
            for (var i = 0; i < 300 && File.Exists(lockFile); i++)
                await Task.Delay(100);
        }
    }

    /// <summary>
    /// Starts the notification-area icon shipped next to omen.exe, if it is there; a second copy exits at once, so
    /// every server can ask.
    /// </summary>
    private static void StartTray()
    {
        var tray = Path.Combine(AppContext.BaseDirectory, "omen-tray.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(tray))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(tray) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No tray is not a reason to fail a build server.
        }
    }

    /// <summary>
    /// Starts <c>omen server</c> detached. Through ShellExecute, not CreateProcess: a child created with inherited
    /// handles keeps this client's redirected stdout/stderr open, and whoever captures them (Visual Studio's NMake
    /// project, a PowerShell redirect) waits until the server exits.
    /// </summary>
    public static Task StartDetachedAsync(string root, string config)
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root,
            Arguments = $"server --root \"{root.TrimEnd('\\', '/')}\" -c {config}",
        });
        return Task.CompletedTask;
    }
}
