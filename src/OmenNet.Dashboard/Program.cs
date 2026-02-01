// OmenNet Dashboard
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using OmenNet.Dashboard.Components;
using OmenNet.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// OmenNet services
builder.Services.Configure<OmenNetOptions>(
    builder.Configuration.GetSection("OmenNet"));
builder.Services.AddSingleton<ICoordinatorClient, CoordinatorClient>();
builder.Services.AddSingleton<IDashboardService, DashboardService>();
builder.Services.AddHostedService<MetricsCollector>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// API endpoints for external tools
app.MapGet("/api/health", () => Results.Ok(new { Status = "Healthy" }));
app.MapGet("/api/stats", (IDashboardService svc) => Results.Ok(svc.GetCurrentStats()));
app.MapGet("/api/agents", (IDashboardService svc) => Results.Ok(svc.GetAgents()));
app.MapGet("/api/jobs", (IDashboardService svc) => Results.Ok(svc.GetActiveJobs()));

app.Run();
