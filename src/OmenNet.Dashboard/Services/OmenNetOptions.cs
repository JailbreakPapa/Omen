// OmenNet Dashboard
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace OmenNet.Dashboard.Services;

/// <summary>
/// Configuration options for OmenNet Dashboard.
/// </summary>
public class OmenNetOptions
{
    /// <summary>
    /// Host of the OmenNet coordinator.
    /// </summary>
    public string CoordinatorHost { get; set; } = "localhost";

    /// <summary>
    /// gRPC port of the OmenNet coordinator.
    /// </summary>
    public int CoordinatorPort { get; set; } = 5051;

    /// <summary>
    /// Full address of the OmenNet coordinator.
    /// </summary>
    public string CoordinatorAddress => $"http://{CoordinatorHost}:{CoordinatorPort}";

    /// <summary>
    /// How often to poll for metrics (in seconds).
    /// </summary>
    public int MetricsPollingIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum history entries to keep.
    /// </summary>
    public int MaxHistoryEntries { get; set; } = 1000;

    /// <summary>
    /// Enable authentication for the dashboard.
    /// </summary>
    public bool EnableAuthentication { get; set; } = false;
}
