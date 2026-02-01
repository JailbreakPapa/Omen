// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.CommandLine;
using System.Net;
using Spectre.Console;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omen.Distributed.Server;
using Omen.Distributed.Protos;
using Cmd = System.CommandLine.Command;

namespace Omen.CLI.Commands;

public static class CoordinatorCommand
{
    public static Cmd Create()
    {
        var command = new Cmd("coordinator", "Manage the build coordinator");

        command.AddCommand(CreateStartCommand());
        command.AddCommand(CreateStatusCommand());
        command.AddCommand(CreateStopCommand());

        return command;
    }

    private static Cmd CreateStartCommand()
    {
        var command = new Cmd("start", "Start the coordinator service");

        var portOption = new Option<int>(
            ["--port", "-p"],
            () => 5051,
            "gRPC port to listen on");

        var dashboardOption = new Option<bool>(
            ["--dashboard", "-d"],
            () => false,
            "Start the OmenNet web dashboard");

        var dashboardPortOption = new Option<int>(
            "--dashboard-port",
            () => 5172,
            "Port for the web dashboard");

        var redisOption = new Option<string?>(
            "--redis",
            "Redis connection string for distributed state");

        var casPathOption = new Option<string?>(
            "--cas-path",
            "Path for content-addressable storage");

        command.AddOption(portOption);
        command.AddOption(dashboardOption);
        command.AddOption(dashboardPortOption);
        command.AddOption(redisOption);
        command.AddOption(casPathOption);

        command.SetHandler(async (context) =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var withDashboard = context.ParseResult.GetValueForOption(dashboardOption);
            var dashboardPort = context.ParseResult.GetValueForOption(dashboardPortOption);
            var redis = context.ParseResult.GetValueForOption(redisOption);
            var casPath = context.ParseResult.GetValueForOption(casPathOption);

            var ct = context.GetCancellationToken();

            AnsiConsole.Write(new Rule("[orange1]Omen Build Coordinator[/]").RuleStyle("dim"));
            AnsiConsole.WriteLine();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Setting");
            table.AddColumn("Value");
            table.AddRow("gRPC Port", port.ToString());
            table.AddRow("Dashboard", withDashboard ? $"Enabled (port {dashboardPort})" : "Disabled");
            table.AddRow("Redis", redis ?? "(local mode)");
            table.AddRow("CAS Path", casPath ?? "(temp)");

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            try
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Initializing services...", async ctx =>
                    {
                        await Task.Delay(300, ct);
                        ctx.Status("Starting gRPC server...");
                        await Task.Delay(200, ct);
                    });

                // Start the coordinator host
                var host = CreateCoordinatorHost(port, withDashboard ? dashboardPort : null);
                await host.StartAsync(ct);

                AnsiConsole.MarkupLine($"[green] gRPC coordinator started on port {port}[/]");
                if (withDashboard)
                {
                    AnsiConsole.MarkupLine($"[green] Dashboard available at http://localhost:{dashboardPort}[/]");
                }
                AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop[/]");
                AnsiConsole.WriteLine();

                // Get the coordinator state for live updates
                var state = host.Services.GetRequiredService<CoordinatorState>();

                // Live status display
                var statusTable = new Table().Border(TableBorder.Rounded);
                statusTable.AddColumn("Metric");
                statusTable.AddColumn("Value");

                await AnsiConsole.Live(statusTable)
                    .AutoClear(false)
                    .StartAsync(async ctx =>
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            var stats = state.GetStats();

                            statusTable.Rows.Clear();
                            statusTable.AddRow("Connected Agents", stats.RegisteredAgents.ToString());
                            statusTable.AddRow("Active Agents", stats.ActiveAgents.ToString());
                            statusTable.AddRow("Queued Operations", stats.QueuedOperations.ToString());
                            statusTable.AddRow("Active Operations", stats.ExecutingOperations.ToString());
                            statusTable.AddRow("Completed", stats.CompletedOperations.ToString());
                            statusTable.AddRow("Failed", stats.FailedOperations.ToString());
                            statusTable.AddRow("Cache Hit Rate", $"{stats.CacheHitRate:P1}");

                            ctx.Refresh();

                            try
                            {
                                await Task.Delay(2000, ct);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                    });

                AnsiConsole.MarkupLine("\n[yellow]Shutting down coordinator...[/]");
                await host.StopAsync();
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
                context.ExitCode = 1;
            }
        });

        return command;
    }

    private static IHost CreateCoordinatorHost(int grpcPort, int? dashboardPort)
    {
        var builder = WebApplication.CreateBuilder();

        // Configure Kestrel for gRPC and optionally HTTP
        builder.WebHost.ConfigureKestrel(options =>
        {
            // gRPC endpoint (HTTP/2)
            options.Listen(IPAddress.Any, grpcPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });

            // Dashboard endpoint (HTTP/1.1 and HTTP/2)
            if (dashboardPort.HasValue)
            {
                options.Listen(IPAddress.Any, dashboardPort.Value, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                });
            }
        });

        // Suppress console logging
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("Grpc", LogLevel.Warning);

        // Register shared state
        builder.Services.AddSingleton<CoordinatorState>();

        // gRPC services
        builder.Services.AddGrpc();

        // Dashboard services (if enabled)
        if (dashboardPort.HasValue)
        {
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
        }

        var app = builder.Build();

        // Map gRPC services
        app.MapGrpcService<OmenCoordinatorService>();
        app.MapGrpcService<OmenAgentService>();

        // Map dashboard (if enabled)
        if (dashboardPort.HasValue)
        {
            app.UseStaticFiles();
            app.UseAntiforgery();
            app.MapRazorComponents<OmenNet.Dashboard.Components.App>()
                .AddInteractiveServerRenderMode();

            // Health and API endpoints
            app.MapGet("/api/health", () => Results.Ok(new { Status = "Healthy" }));
            app.MapGet("/api/stats", (CoordinatorState state) =>
            {
                var stats = state.GetStats();
                return Results.Ok(stats);
            });
            app.MapGet("/api/agents", (CoordinatorState state) =>
            {
                var agents = state.GetAgentInfoList();
                return Results.Ok(agents);
            });
        }

        return app;
    }

    private static Cmd CreateStatusCommand()
    {
        var command = new Cmd("status", "Show coordinator status");

        var addressOption = new Option<string>(
            ["--address", "-a"],
            () => "localhost:5051",
            "Coordinator address");

        command.AddOption(addressOption);

        command.SetHandler(async (context) =>
        {
            var address = context.ParseResult.GetValueForOption(addressOption);

            AnsiConsole.MarkupLine($"[blue]Checking coordinator at {address.EscapeMarkup()}...[/]");

            try
            {
                using var channel = Grpc.Net.Client.GrpcChannel.ForAddress($"http://{address}");
                var client = new OmenCoordinator.OmenCoordinatorClient(channel);

                var response = await client.GetStatusAsync(new GetStatusRequest());

                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("Metric");
                table.AddColumn("Value");
                table.AddRow("Registered Agents", response.RegisteredAgents.ToString());
                table.AddRow("Active Agents", response.ActiveAgents.ToString());
                table.AddRow("Queued Operations", response.QueuedOperations.ToString());
                table.AddRow("Executing Operations", response.ExecutingOperations.ToString());
                table.AddRow("Completed", response.CompletedOperations.ToString());
                table.AddRow("Failed", response.FailedOperations.ToString());

                AnsiConsole.Write(table);
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Could not connect to coordinator: {ex.Message.EscapeMarkup()}[/]");
                context.ExitCode = 1;
            }
        });

        return command;
    }

    private static Cmd CreateStopCommand()
    {
        var command = new Cmd("stop", "Stop the coordinator");

        command.SetHandler((context) =>
        {
            AnsiConsole.MarkupLine("[yellow]Stop must be done via Ctrl+C on the running coordinator[/]");
            context.ExitCode = 0;
        });

        return command;
    }
}
