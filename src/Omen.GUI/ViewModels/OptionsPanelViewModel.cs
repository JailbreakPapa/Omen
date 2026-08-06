// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Executors.Orchestration;

namespace Omen.GUI.ViewModels;

public sealed partial class OptionsPanelViewModel : ViewModelBase
{
    public ObservableCollection<BuildOptionViewModel> Options { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>
    /// Persists any current edits (if options were already discovered once) and re-runs
    /// discovery, mirroring cmake-gui's Configure button. The first-ever call for a project
    /// has nothing to save yet, so it only discovers.
    /// </summary>
    public async Task ConfigureAsync(string targetFile, IProgress<OrchestratorEvent>? events)
    {
        var orchestrator = new OptionsOrchestrator();

        if (Options.Count > 0)
        {
            var edited = Options.ToDictionary(o => o.Name, o => o.Value);
            orchestrator.SaveOptions(targetFile, edited);
        }

        var declarations = await orchestrator.DiscoverAsync(new OptionsOrchestratorRequest { TargetFile = targetFile }, events);
        if (declarations == null)
        {
            StatusText = "Configure failed";
            return;
        }

        Options.Clear();
        foreach (var declaration in declarations)
        {
            Options.Add(new BuildOptionViewModel
            {
                Name = declaration.Name,
                Description = declaration.Description,
                Type = declaration.Type,
                Value = declaration.EffectiveValue
            });
        }

        StatusText = Options.Count == 1 ? "1 option" : $"{Options.Count} options";
    }
}
