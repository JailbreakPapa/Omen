// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Core.Configuration;
using Omen.Executors.Orchestration;
using Omen.GUI.Models;
using Omen.GUI.Services;
using Omen.Platforms;

namespace Omen.GUI.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? _projectPath;

    [ObservableProperty]
    private string _statusText = "No project open";

    [ObservableProperty]
    private bool _isBuilding;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private TargetPlatform? _selectedPlatform;

    [ObservableProperty]
    private BuildConfiguration _selectedConfiguration = BuildConfiguration.Development;

    public ObservableCollection<TargetPlatform> AvailablePlatforms { get; } = [];

    public ObservableCollection<BuildConfiguration> Configurations { get; } =
    [
        BuildConfiguration.Debug,
        BuildConfiguration.Development,
        BuildConfiguration.Release,
        BuildConfiguration.Shipping
    ];

    public ObservableCollection<ProjectTreeNode> ProjectTreeRoots { get; } = [];

    public OptionsPanelViewModel OptionsPanel { get; } = new();

    private readonly GuiSettings _settings = GuiSettings.Load();

    public MainWindowViewModel()
    {
        foreach (var (platform, _, _) in PlatformFactory.GetAvailablePlatforms())
        {
            AvailablePlatforms.Add(platform);
        }

        SelectedPlatform = AvailablePlatforms.FirstOrDefault();

        if (!string.IsNullOrEmpty(_settings.LastProjectPath) && Directory.Exists(_settings.LastProjectPath))
        {
            try
            {
                LoadProject(_settings.LastProjectPath);
            }
            catch (Exception)
            {
                // Last-resort guard: a bad/inaccessible last-project path must not crash
                // startup before the window appears. Leave the project unopened.
                CloseProject();
            }
        }
    }

    public void LoadProject(string path)
    {
        ProjectTreeRoots.Clear();
        ProjectTreeRoots.Add(ProjectTreeNode.BuildTree(path));

        ProjectPath = path;
        StatusText = $"Project: {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}";

        _settings.LastProjectPath = path;
        _settings.Save();

        _ = ConfigureOptionsAsync();
    }

    public void CloseProject()
    {
        ProjectTreeRoots.Clear();
        ProjectPath = null;
        StatusText = "No project open";
    }

    public async Task ConfigureOptionsAsync()
    {
        if (ProjectPath == null) return;

        var targetFile = Directory.GetFiles(ProjectPath, "*.target.cs", SearchOption.AllDirectories).FirstOrDefault();
        if (targetFile == null)
        {
            OptionsPanel.StatusText = "No .target.cs file found in this project.";
            return;
        }

        var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));

        try
        {
            await OptionsPanel.ConfigureAsync(targetFile, eventsProgress);
        }
        catch (Exception ex)
        {
            OptionsPanel.StatusText = "Configure failed";
            AppendLine($"Unexpected error configuring options: {ex.Message}", OrchestratorEventLevel.Error);
        }
    }

    public ObservableCollection<OutputLine> OutputLines { get; } = [];

    private CancellationTokenSource? _buildCts;

    // manageBusyState is false only when called from RebuildAsync, which owns IsBuilding for
    // the whole Clean+Build sequence itself - otherwise IsBuilding would briefly flip back to
    // false between the two phases (each phase's own finally resetting it), re-enabling
    // Build/Rebuild/Clean for the split second before the next phase starts and flips it back.
    public async Task BuildAsync(bool manageBusyState = true)
    {
        if (ProjectPath == null || SelectedPlatform == null) return;

        OutputLines.Clear();
        if (manageBusyState) IsBuilding = true;
        ProgressValue = 0;
        StatusText = "Building...";
        _buildCts = new CancellationTokenSource();

        try
        {
            var targetFile = Directory.GetFiles(ProjectPath, "*.target.cs", SearchOption.AllDirectories).FirstOrDefault();
            if (targetFile == null)
            {
                AppendLine("No .target.cs file found in this project.", OrchestratorEventLevel.Error);
                return;
            }

            var orchestrator = new BuildOrchestrator();
            var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));
            var buildProgress = new Progress<Omen.Core.Interfaces.BuildProgress>(p => ProgressValue = p.PercentComplete);

            var request = new BuildOrchestratorRequest
            {
                TargetFile = targetFile,
                Platform = SelectedPlatform.Value,
                Architecture = TargetArchitecture.X64,
                Configuration = SelectedConfiguration
            };

            var result = await orchestrator.BuildAsync(request, eventsProgress, buildProgress, _buildCts.Token);
            StatusText = result?.Success == true ? "Build succeeded" : "Build failed";
        }
        catch (OperationCanceledException)
        {
            AppendLine("Build cancelled.", OrchestratorEventLevel.Warning);
            StatusText = "Build cancelled";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLine($"Unexpected error: {ex.Message}", OrchestratorEventLevel.Error);
            StatusText = "Build failed";
        }
        finally
        {
            if (manageBusyState) IsBuilding = false;
            _buildCts = null;
        }
    }

    public async Task RebuildAsync()
    {
        IsBuilding = true;
        try
        {
            await CleanAsync(manageBusyState: false);
            await BuildAsync(manageBusyState: false);
        }
        finally
        {
            IsBuilding = false;
        }
    }

    public async Task CleanAsync(bool manageBusyState = true)
    {
        if (ProjectPath == null) return;

        OutputLines.Clear();
        if (manageBusyState) IsBuilding = true;
        StatusText = "Cleaning...";

        var orchestrator = new CleanOrchestrator();
        var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));

        try
        {
            await orchestrator.CleanAsync(
                new CleanOrchestratorRequest { ProjectRoot = ProjectPath, All = true },
                eventsProgress);

            StatusText = $"Project: {Path.GetFileName(ProjectPath.TrimEnd(Path.DirectorySeparatorChar))}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLine($"Unexpected error: {ex.Message}", OrchestratorEventLevel.Error);
            StatusText = "Clean failed";
        }
        finally
        {
            if (manageBusyState) IsBuilding = false;
        }
    }

    public void CancelBuild() => _buildCts?.Cancel();

    public async Task GenerateProjectFilesAsync(IdeKind ide)
    {
        if (ProjectPath == null) return;

        OutputLines.Clear();
        IsBuilding = true;
        StatusText = $"Generating {ide} project files...";

        var orchestrator = new ProjectGenerationOrchestrator();
        var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));

        try
        {
            var success = await orchestrator.GenerateAsync(
                new ProjectGenerationOrchestratorRequest { ProjectRoot = ProjectPath, Ide = ide },
                eventsProgress);

            StatusText = success
                ? $"Project: {Path.GetFileName(ProjectPath.TrimEnd(Path.DirectorySeparatorChar))}"
                : "Project file generation failed";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLine($"Unexpected error: {ex.Message}", OrchestratorEventLevel.Error);
            StatusText = "Project file generation failed";
        }
        finally
        {
            IsBuilding = false;
        }
    }

    private void AppendLine(string text, OrchestratorEventLevel level) =>
        OutputLines.Add(new OutputLine { Text = text, Level = level });
}
