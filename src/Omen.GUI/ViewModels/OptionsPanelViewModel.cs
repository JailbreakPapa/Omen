// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omen.Core.Options;
using Omen.Executors.Orchestration;

namespace Omen.GUI.ViewModels;

public sealed partial class OptionsPanelViewModel : ViewModelBase
{
    private string? _lastTargetFile;

    public Action<string, OrchestratorEventLevel>? Report { get; set; }

    public ObservableCollection<BuildOptionViewModel> Options { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredOptions))]
    [NotifyPropertyChangedFor(nameof(GroupedOptions))]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredOptions))]
    [NotifyPropertyChangedFor(nameof(GroupedOptions))]
    private bool _showAdvanced;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupedOptions))]
    private bool _groupByPrefix;

    /// <summary>Types offerable when adding an option by hand. The backend supports all five;
    /// the previous UI hardcoded String, making the other four unreachable.</summary>
    public IReadOnlyList<BuildOptionType> AvailableOptionTypes { get; } =
        [BuildOptionType.String, BuildOptionType.Bool, BuildOptionType.Int, BuildOptionType.Path];

    [ObservableProperty]
    private string _newEntryName = "";

    [ObservableProperty]
    private string _newEntryDescription = "";

    [ObservableProperty]
    private string _newEntryDefaultValue = "";

    [ObservableProperty]
    private BuildOptionType _newEntryType = BuildOptionType.String;

    public IEnumerable<BuildOptionViewModel> FilteredOptions =>
        Options
            .Where(o => ShowAdvanced || !o.IsAdvanced)
            .Where(o => string.IsNullOrWhiteSpace(SearchText) ||
                        o.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        o.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<IGrouping<string, BuildOptionViewModel>> GroupedOptions =>
        FilteredOptions.GroupBy(o => o.GroupName).OrderBy(g => g.Key);

    /// <summary>
    /// Persists any current edits (if options were already discovered once) and re-runs
    /// discovery, mirroring cmake-gui's Configure button. The first-ever call for a project
    /// has nothing to save yet, so it only discovers.
    /// </summary>
    public async Task ConfigureAsync(string targetFile, IProgress<OrchestratorEvent>? events)
    {
        _lastTargetFile = targetFile;

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
                Value = declaration.EffectiveValue,
                OriginalValue = declaration.EffectiveValue,
                IsAdvanced = declaration.IsAdvanced,
                IsManual = declaration.IsManual,
                AllowedValues = declaration.AllowedValues ?? []
            });
        }

        OnPropertyChanged(nameof(FilteredOptions));
        OnPropertyChanged(nameof(GroupedOptions));
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var shown = FilteredOptions.Count();
        var total = Options.Count;

        StatusText = shown == total
            ? total == 1 ? "1 option" : $"{total} options"
            : $"{shown} of {total} options";
    }

    partial void OnSearchTextChanged(string value) => UpdateStatusText();
    partial void OnShowAdvancedChanged(bool value) => UpdateStatusText();

    /// <summary>
    /// Adds the option described by the NewEntry* properties, then clears them. Enum is not
    /// offered because a manually added option has no way to declare its allowed values.
    /// </summary>
    public async Task AddCurrentEntryAsync()
    {
        if (string.IsNullOrWhiteSpace(NewEntryName))
            return;

        await AddEntryAsync(NewEntryName.Trim(), NewEntryDescription.Trim(), NewEntryType, NewEntryDefaultValue);

        NewEntryName = "";
        NewEntryDescription = "";
        NewEntryDefaultValue = "";
        NewEntryType = BuildOptionType.String;
    }

    public async Task AddEntryAsync(string name, string description, BuildOptionType type, string defaultValue)
    {
        if (_lastTargetFile == null) return;

        try
        {
            new OptionsOrchestrator().AddManualOption(_lastTargetFile, new BuildOptionDeclaration
            {
                Name = name,
                Description = description,
                Type = type,
                DefaultValue = defaultValue,
                EffectiveValue = defaultValue
            });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Report?.Invoke($"Could not add option '{name}': {ex.Message}", OrchestratorEventLevel.Error);
            return;
        }

        await ConfigureAsync(_lastTargetFile, null);
    }

    /// <summary>
    /// Drops all option state, including the remembered target file. Without this, Add/Remove
    /// Entry would keep writing to the project that was just closed.
    /// </summary>
    public void Reset()
    {
        Options.Clear();
        _lastTargetFile = null;
        SearchText = "";
        StatusText = "";
        OnPropertyChanged(nameof(FilteredOptions));
        OnPropertyChanged(nameof(GroupedOptions));
    }

    public async Task RemoveEntryAsync(BuildOptionViewModel option)
    {
        if (_lastTargetFile == null) return;

        try
        {
            new OptionsOrchestrator().RemoveManualOption(_lastTargetFile, option.Name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Report?.Invoke($"Could not remove option '{option.Name}': {ex.Message}", OrchestratorEventLevel.Error);
            return;
        }

        await ConfigureAsync(_lastTargetFile, null);
    }
}

