// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.GUI.Services;
using Omen.Platforms;

namespace Omen.GUI.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _selectedCategory = "General";

    [ObservableProperty]
    private bool _checkForUpdatesOnStartup = true;

    [ObservableProperty]
    private bool _confirmBeforeClean = true;

    [ObservableProperty]
    private string? _defaultProjectFolder;

    [ObservableProperty]
    private string _defaultConfiguration = "Development";

    [ObservableProperty]
    private int _parallelJobs = Environment.ProcessorCount;

    [ObservableProperty]
    private bool _useUnityBuilds = true;

    [ObservableProperty]
    private bool _usePrecompiledHeaders = true;

    [ObservableProperty]
    private bool _enableLto;

    [ObservableProperty]
    private bool _enableDistributedBuilds;

    [ObservableProperty]
    private string _coordinatorAddress = "";

    [ObservableProperty]
    private int _localAgentJobs = Environment.ProcessorCount;

    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private string _uiDensity = "Standard";

    [ObservableProperty]
    private int _consoleFontSize = 12;

    /// <summary>
    /// Live toolchain detection. PlatformFactory.GetAvailablePlatforms() yields one tuple per
    /// known platform with a null Info when that platform's SDK is not installed, so this
    /// needs no detection logic of its own.
    /// </summary>
    public IReadOnlyList<ToolchainStatus> Toolchains =>
        PlatformFactory.GetAvailablePlatforms()
            .Select(p => new ToolchainStatus(
                Name: $"{p.Platform} ({p.Sdk.Name})",
                Status: p.Info is not null ? $"Detected ({p.Info.Version})" : "Not found",
                State: p.Info is not null ? ToolchainState.Detected : ToolchainState.NotFound))
            .ToList();

    public void Load()
    {
        var settings = GuiSettings.Load();
        CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        ConfirmBeforeClean = settings.ConfirmBeforeClean;
        DefaultProjectFolder = settings.DefaultProjectFolder;
        DefaultConfiguration = settings.DefaultConfiguration;
        ParallelJobs = settings.ParallelJobs;
        UseUnityBuilds = settings.UseUnityBuilds;
        UsePrecompiledHeaders = settings.UsePrecompiledHeaders;
        EnableLto = settings.EnableLto;
        EnableDistributedBuilds = settings.EnableDistributedBuilds;
        CoordinatorAddress = settings.CoordinatorAddress;
        LocalAgentJobs = settings.LocalAgentJobs;
        Theme = settings.Theme;
        UiDensity = settings.UiDensity;
        ConsoleFontSize = settings.ConsoleFontSize;
    }

    public void Save()
    {
        var settings = new GuiSettings
        {
            LastProjectPath = GuiSettings.Load().LastProjectPath,
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
            ConfirmBeforeClean = ConfirmBeforeClean,
            DefaultProjectFolder = DefaultProjectFolder,
            DefaultConfiguration = DefaultConfiguration,
            ParallelJobs = ParallelJobs,
            UseUnityBuilds = UseUnityBuilds,
            UsePrecompiledHeaders = UsePrecompiledHeaders,
            EnableLto = EnableLto,
            EnableDistributedBuilds = EnableDistributedBuilds,
            CoordinatorAddress = CoordinatorAddress,
            LocalAgentJobs = LocalAgentJobs,
            Theme = Theme,
            UiDensity = UiDensity,
            ConsoleFontSize = ConsoleFontSize
        };
        settings.Save();
    }
}

public sealed record ToolchainStatus(string Name, string Status, ToolchainState State);

public enum ToolchainState
{
    Detected,
    NotFound,
    Unavailable
}
