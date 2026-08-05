// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Core.Configuration;
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
            LoadProject(_settings.LastProjectPath);
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
    }

    public void CloseProject()
    {
        ProjectTreeRoots.Clear();
        ProjectPath = null;
        StatusText = "No project open";
    }
}
