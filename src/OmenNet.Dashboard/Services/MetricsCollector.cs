// OmenNet Dashboard
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Microsoft.Extensions.Options;

namespace OmenNet.Dashboard.Services;

/// <summary>
/// Background service that periodically collects metrics from the coordinator.
/// </summary>
public class MetricsCollector : BackgroundService
{
    private readonly DashboardService _dashboard;
    private readonly OmenNetOptions _options;
    private readonly ILogger<MetricsCollector> _logger;
    
    public MetricsCollector(
        IDashboardService dashboard, 
        IOptions<OmenNetOptions> options,
        ILogger<MetricsCollector> logger)
    {
        _dashboard = (DashboardService)dashboard;
        _options = options.Value;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metrics collector started, polling every {Interval}s",
            _options.MetricsPollingIntervalSeconds);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _dashboard.RefreshAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect metrics");
            }
            
            await Task.Delay(
                TimeSpan.FromSeconds(_options.MetricsPollingIntervalSeconds), 
                stoppingToken);
        }
    }
}
