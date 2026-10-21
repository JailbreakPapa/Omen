// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using System.Linq;
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
    [NotifyPropertyChangedFor(nameof(ProjectName))]
    private string? _projectPath;

    [ObservableProperty]
    private string _statusText = "No project open";

    /// <summary>Folder name of the open project, for the top bar's project chip.</summary>
    public string ProjectName =>
        string.IsNullOrEmpty(ProjectPath) ? "No project" : Path.GetFileName(ProjectPath.TrimEnd(Path.DirectorySeparatorChar));

    public enum BuildState { Idle, Building, Success, Failed }

    [ObservableProperty]
    private BuildState _currentBuildState = BuildState.Idle;

    [ObservableProperty]
    private bool _isBuilding;

    /// <summary>
    /// True only while an operation that can actually be cancelled is running. Clean and
    /// project generation set IsBuilding but are not cancellable, so Cancel is hidden for them
    /// rather than shown and inert.
    /// </summary>
    [ObservableProperty]
    private bool _isCancellable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    private double _progressValue;

    /// <summary>Whole-percent label shown at the right of the status bar, e.g. "64%".</summary>
    public string ProgressLabel => $"{(int)Math.Round(ProgressValue)}%";

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

    public MatrixPanelViewModel MatrixPanel { get; } = new();

    public RuleEditorPanelViewModel RuleEditor { get; } = new();

    public SettingsViewModel Settings { get; } = new();

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isAboutOpen;

    public void OpenAbout() => IsAboutOpen = true;
    public void CloseAbout() => IsAboutOpen = false;

    public void OpenDocumentation()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/JailbreakPapa/Omen#readme") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppendLine($"Could not open documentation: {ex.Message}", OrchestratorEventLevel.Error);
        }
    }

    public void OpenSettings()
    {
        Settings.Load();
        IsSettingsOpen = true;
    }

    /// <summary>
    /// Closes settings and puts the density back to what is actually persisted. Density applies
    /// live as you pick it, which is the point - but that preview must not survive Cancel. After
    /// OK this is a no-op, because Save has already made the persisted value the current one.
    /// </summary>
    public void CloseSettings()
    {
        IsSettingsOpen = false;

        ThemeService.ApplyDensity(GuiSettings.Load().UiDensity);
    }

    private readonly GuiSettings _settings = GuiSettings.Load();

    public MainWindowViewModel()
    {
        foreach (var (platform, _, _) in PlatformFactory.GetAvailablePlatforms())
        {
            AvailablePlatforms.Add(platform);
        }

        SelectedPlatform = AvailablePlatforms.FirstOrDefault();

        MatrixPanel.Report = AppendLine;
        OptionsPanel.Report = AppendLine;
        RuleEditor.Report = AppendLine;
        RuleEditor.DiagnosticsChanged = OnRuleDocumentDiagnosticsChanged;

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
        try
        {
            ProjectTreeRoots.Clear();
            ProjectTreeRoots.Add(ProjectTreeNode.BuildTree(path));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppendLine($"Could not read '{path}': {ex.Message}", OrchestratorEventLevel.Error);
            return;
        }

        ProjectPath = path;
        StatusText = $"Project: {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}";

        _settings.LastProjectPath = path;
        _settings.Save();

        _ = ConfigureOptionsAsync();
        _ = ConfigureMatrixAsync();
    }

    public void CloseProject()
    {
        ProjectTreeRoots.Clear();
        OutputLines.Clear();
        NotifyOutputLinesChanged();
        OptionsPanel.Reset();
        MatrixPanel.Reset();
        RuleEditor.Reset();
        ProjectPath = null;
        StatusText = "No project open";
        CurrentBuildState = BuildState.Idle;
        ProgressValue = 0;
    }

    // ---- Rule-file editor -------------------------------------------------------------------

    /// <summary>Opens a *.plugin.cs/*.module.cs/*.target.cs/*.omenproject.cs file in the rule
    /// editor. The project tree and the search overlay's file results are the two entry points;
    /// both just call this rather than each owning their own open logic.</summary>
    public void OpenRuleFile(string filePath) => RuleEditor.OpenFile(filePath, ProjectPath);

    /// <summary>Mirrors one document's diagnostics into OutputLines (and so the Problems tab),
    /// replacing that file's previous lines rather than appending duplicates on every
    /// revalidation.</summary>
    private void OnRuleDocumentDiagnosticsChanged(Models.RuleEditorDocument document)
    {
        for (var i = OutputLines.Count - 1; i >= 0; i--)
        {
            if (string.Equals(OutputLines[i].SourceFile, document.FilePath, StringComparison.OrdinalIgnoreCase))
                OutputLines.RemoveAt(i);
        }

        foreach (var diagnostic in document.Diagnostics)
        {
            var level = diagnostic.Severity switch
            {
                Models.RuleDiagnosticSeverity.Error => OrchestratorEventLevel.Error,
                Models.RuleDiagnosticSeverity.Warning => OrchestratorEventLevel.Warning,
                _ => OrchestratorEventLevel.Info
            };

            OutputLines.Add(new OutputLine
            {
                Text = $"{document.FileName}({diagnostic.Line},{diagnostic.Column}): {diagnostic.Message}",
                Level = level,
                SourceFile = document.FilePath,
                Line = diagnostic.Line,
                Column = diagnostic.Column
            });
        }

        NotifyOutputLinesChanged();
    }

    /// <summary>
    /// Finds the project's target file. "A project" means a directory containing at least one
    /// *.target.cs; this lookup was previously duplicated at three call sites.
    /// </summary>
    private string? FindTargetFile()
    {
        if (string.IsNullOrEmpty(ProjectPath))
            return null;

        try
        {
            return Directory
                .GetFiles(ProjectPath, "*.target.cs", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppendLine($"Could not scan '{ProjectPath}' for a .target.cs file: {ex.Message}",
                OrchestratorEventLevel.Error);
            return null;
        }
    }

    public async Task ConfigureOptionsAsync()
    {
        if (ProjectPath == null) return;

        try
        {
            var targetFile = FindTargetFile();
            if (targetFile == null)
            {
                OptionsPanel.StatusText = "No .target.cs file found in this project.";
                return;
            }

            var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));
            await OptionsPanel.ConfigureAsync(targetFile, eventsProgress);
        }
        catch (Exception ex)
        {
            OptionsPanel.StatusText = "Configure failed";
            AppendLine($"Unexpected error configuring options: {ex.Message}", OrchestratorEventLevel.Error);
        }
    }

    public async Task ConfigureMatrixAsync()
    {
        if (ProjectPath == null) return;

        try
        {
            var targetFile = FindTargetFile();
            if (targetFile == null) return;

            await MatrixPanel.ResolveAsync(targetFile);
        }
        catch (Exception ex)
        {
            AppendLine($"Unexpected error resolving build matrix: {ex.Message}", OrchestratorEventLevel.Error);
        }
    }

    // ---- Search overlay ("Search everywhere") ---------------------------------------------
    //
    // Covers the app's whole addressable surface: commands, project files, and build options.
    // Files are searched on disk rather than through the project tree, because the tree loads
    // lazily - walking it would only ever find folders the user had already expanded.

    private const int MaxFileResults = 50;

    private CancellationTokenSource? _searchCts;

    /// <summary>Bound to the overlay's search box.</summary>
    [ObservableProperty]
    private string _quickSearch = string.Empty;

    /// <summary>Whether the search overlay is showing.</summary>
    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private int _selectedSearchIndex = -1;

    public ObservableCollection<QuickSearchResult> QuickSearchResults { get; } = [];

    public void OpenSearch()
    {
        QuickSearch = string.Empty;
        IsSearchOpen = true;
    }

    public void CloseSearch()
    {
        _searchCts?.Cancel();
        IsSearchOpen = false;
        QuickSearch = string.Empty;
        QuickSearchResults.Clear();
        SelectedSearchIndex = -1;
    }

    /// <summary>Moves the highlight, wrapping at both ends so Up from the top lands on the last.</summary>
    public void MoveSearchSelection(int delta)
    {
        if (QuickSearchResults.Count == 0)
            return;

        var next = SelectedSearchIndex + delta;
        if (next < 0)
            next = QuickSearchResults.Count - 1;
        else if (next >= QuickSearchResults.Count)
            next = 0;

        SelectedSearchIndex = next;
    }

    public void ActivateSelectedSearchResult()
    {
        if (SelectedSearchIndex < 0 || SelectedSearchIndex >= QuickSearchResults.Count)
            return;

        var result = QuickSearchResults[SelectedSearchIndex];
        CloseSearch();
        result.Activate();
    }

    partial void OnQuickSearchChanged(string value) => _ = RunSearchAsync(value);

    private async Task RunSearchAsync(string query)
    {
        // Each keystroke supersedes the walk in flight; without this a slow walk over a large
        // tree keeps running and races its results in behind a newer, narrower query.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        query = query.Trim();
        if (query.Length < 1)
        {
            QuickSearchResults.Clear();
            SelectedSearchIndex = -1;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        // Commands and options are in-memory and instant; only the file walk needs a thread.
        var hits = new List<QuickSearchResult>();
        hits.AddRange(MatchCommands(query));
        hits.AddRange(MatchOptions(query));

        var root = ProjectTreeRoots.FirstOrDefault();
        if (root is not null)
        {
            var rootPath = root.FullPath;
            try
            {
                var files = await Task.Run(() => ProjectTreeNode
                    .EnumerateSearchableFiles(rootPath, token)
                    .Where(f => Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(MaxFileResults)
                    .Select(f => new QuickSearchResult(
                        "Project files",
                        Path.GetFileName(f),
                        Path.GetRelativePath(rootPath, f),
                        () => ActivateFileResult(f)))
                    .ToList(), token);

                hits.AddRange(files);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (token.IsCancellationRequested)
            return;

        QuickSearchResults.Clear();
        foreach (var hit in hits)
            QuickSearchResults.Add(hit);

        SelectedSearchIndex = QuickSearchResults.Count > 0 ? 0 : -1;
    }

    private IEnumerable<QuickSearchResult> MatchCommands(string query)
    {
        var commands = new (string Name, string Detail, Action Run)[]
        {
            ("Build", "Build the open project", () => _ = BuildAsync()),
            ("Rebuild", "Clean, then build", () => _ = RebuildAsync()),
            ("Clean", "Delete build output", () => _ = CleanAsync()),
            ("Cancel build", "Stop the running build", CancelBuild),
            ("Settings", "Open the settings dialog", OpenSettings),
            ("Close project", "Close the open project", CloseProject),
            ("Generate Visual Studio 2022", "Generate project files", () => _ = GenerateProjectFilesAsync(IdeKind.VS2022)),
            ("Generate Visual Studio 2019", "Generate project files", () => _ = GenerateProjectFilesAsync(IdeKind.VS2019)),
            ("Generate Visual Studio Code", "Generate project files", () => _ = GenerateProjectFilesAsync(IdeKind.VSCode)),
            ("Generate Visual Studio 2026", "Generate project files", () => _ = GenerateProjectFilesAsync(IdeKind.VS2026)),
            ("Generate Rider", "Generate project files", () => _ = GenerateProjectFilesAsync(IdeKind.Rider)),
            ("Generate CMake", "Generate project files", () => _ = GenerateProjectFilesAsync(IdeKind.CMake))
        };

        return commands
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(c => new QuickSearchResult("Commands", c.Name, c.Detail, c.Run));
    }

    private IEnumerable<QuickSearchResult> MatchOptions(string query) =>
        OptionsPanel.Options
            .Where(o => o.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .Select(o => new QuickSearchResult(
                "Build options",
                o.Name,
                o.Description,
                () => OptionsPanel.SearchText = o.Name));

    private void RevealInTree(string fullPath)
    {
        var root = ProjectTreeRoots.FirstOrDefault();
        if (root is not null && !root.Reveal(fullPath))
            AppendLine($"Could not reveal {fullPath} in the project tree.", OrchestratorEventLevel.Warning);
    }

    /// <summary>What activating a file in the search overlay does: reveal it in the project
    /// tree, and - for a rule file - also open it in the rule editor, the same entry point the
    /// tree's own double-click uses.</summary>
    private void ActivateFileResult(string fullPath)
    {
        RevealInTree(fullPath);
        if (Services.RuleFileKinds.IsRuleFile(fullPath))
            OpenRuleFile(fullPath);
    }

    public ObservableCollection<OutputLine> OutputLines { get; } = [];

    /// <summary>Which output tab is showing: "output" or "problems".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleOutputLines))]
    private string _activeOutputTab = "output";

    /// <summary>Warnings and errors only - the Problems tab's contents.</summary>
    public IEnumerable<OutputLine> ProblemLines =>
        OutputLines.Where(l => l.Level is OrchestratorEventLevel.Warning or OrchestratorEventLevel.Error);

    public int ProblemCount => ProblemLines.Count();

    public IEnumerable<OutputLine> VisibleOutputLines =>
        ActiveOutputTab == "problems" ? ProblemLines : OutputLines;

    /// <summary>
    /// OutputLines is an ObservableCollection so these computed views (which wrap it in a Where/
    /// ternary) do not raise their own change notification - call this after every Add or Clear.
    /// </summary>
    private void NotifyOutputLinesChanged()
    {
        OnPropertyChanged(nameof(VisibleOutputLines));
        OnPropertyChanged(nameof(ProblemLines));
        OnPropertyChanged(nameof(ProblemCount));
    }

    private CancellationTokenSource? _buildCts;

    // manageBusyState is false only when called from RebuildAsync, which owns IsBuilding for
    // the whole Clean+Build sequence itself - otherwise IsBuilding would briefly flip back to
    // false between the two phases (each phase's own finally resetting it), re-enabling
    // Build/Rebuild/Clean for the split second before the next phase starts and flips it back.
    public async Task BuildAsync(bool manageBusyState = true)
    {
        if (string.IsNullOrEmpty(ProjectPath))
        {
            AppendLine("No project is open. Use the project chip to open one.", OrchestratorEventLevel.Warning);
            return;
        }

        if (!MatrixPanel.IsActive && SelectedPlatform is null)
        {
            AppendLine("No platform is selected.", OrchestratorEventLevel.Warning);
            return;
        }

        var targetFile = FindTargetFile();
        if (targetFile is null)
        {
            AppendLine("No .target.cs file found in this project.", OrchestratorEventLevel.Error);
            return;
        }

        OutputLines.Clear();
        NotifyOutputLinesChanged();
        if (manageBusyState) IsBuilding = true;
        ProgressValue = 0;
        CurrentBuildState = BuildState.Building;
        StatusText = "Building...";
        _buildCts = new CancellationTokenSource();
        IsCancellable = true;

        try
        {
            var orchestrator = new BuildOrchestrator();
            var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));
            var buildProgress = new Progress<Omen.Core.Interfaces.BuildProgress>(p => ProgressValue = p.PercentComplete);

            var request = MatrixPanel.IsActive
                ? BuildRequestFromMatrix(targetFile)
                : new BuildOrchestratorRequest
                {
                    TargetFile = targetFile,
                    Platform = SelectedPlatform.Value,
                    Architecture = TargetArchitecture.X64,
                    Configuration = SelectedConfiguration,
                    Jobs = Settings.ParallelJobs,
                    UseUnityBuild = Settings.UseUnityBuilds,
                    UsePrecompiledHeaders = Settings.UsePrecompiledHeaders,
                    EnableLinkTimeOptimization = Settings.EnableLto,
                    CoordinatorAddress = Settings.EnableDistributedBuilds ? Settings.CoordinatorAddress : null
                };

            var result = await orchestrator.BuildAsync(request, eventsProgress, buildProgress, _buildCts.Token);
            CurrentBuildState = result?.Success == true ? BuildState.Success : BuildState.Failed;
            StatusText = result?.Success == true ? "Build succeeded" : "Build failed";
        }
        catch (OperationCanceledException)
        {
            AppendLine("Build cancelled.", OrchestratorEventLevel.Warning);
            CurrentBuildState = BuildState.Failed;
            StatusText = "Build cancelled";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLine($"Unexpected error: {ex.Message}", OrchestratorEventLevel.Error);
            CurrentBuildState = BuildState.Failed;
            StatusText = "Build failed";
        }
        finally
        {
            if (manageBusyState) IsBuilding = false;
            IsCancellable = false;
            _buildCts = null;
            ProgressValue = 0;
        }
    }

    private BuildOrchestratorRequest BuildRequestFromMatrix(string targetFile)
    {
        var (platform, architecture, configuration, dimensionFilter) = MatrixPanel.BuildFilter();
        return new BuildOrchestratorRequest
        {
            TargetFile = targetFile,
            Platform = platform,
            Architecture = architecture,
            Configuration = configuration,
            DimensionFilter = dimensionFilter,
            Jobs = Settings.ParallelJobs,
            UseUnityBuild = Settings.UseUnityBuilds,
            UsePrecompiledHeaders = Settings.UsePrecompiledHeaders,
            EnableLinkTimeOptimization = Settings.EnableLto,
            CoordinatorAddress = Settings.EnableDistributedBuilds ? Settings.CoordinatorAddress : null
        };
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
        if (string.IsNullOrEmpty(ProjectPath))
        {
            AppendLine("No project is open. Use the project chip to open one.", OrchestratorEventLevel.Warning);
            return;
        }

        OutputLines.Clear();
        NotifyOutputLinesChanged();
        if (manageBusyState) IsBuilding = true;
        CurrentBuildState = BuildState.Building;
        StatusText = "Cleaning...";

        var orchestrator = new CleanOrchestrator();
        var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));

        try
        {
            await orchestrator.CleanAsync(
                new CleanOrchestratorRequest { ProjectRoot = ProjectPath, All = true },
                eventsProgress);

            CurrentBuildState = BuildState.Success;
            StatusText = $"Project: {Path.GetFileName(ProjectPath.TrimEnd(Path.DirectorySeparatorChar))}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLine($"Unexpected error: {ex.Message}", OrchestratorEventLevel.Error);
            CurrentBuildState = BuildState.Failed;
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
        if (string.IsNullOrEmpty(ProjectPath))
        {
            AppendLine("No project is open. Use the project chip to open one.", OrchestratorEventLevel.Warning);
            return;
        }

        OutputLines.Clear();
        NotifyOutputLinesChanged();
        IsBuilding = true;
        CurrentBuildState = BuildState.Building;
        StatusText = $"Generating {ide} project files...";

        var orchestrator = new ProjectGenerationOrchestrator();
        var eventsProgress = new Progress<OrchestratorEvent>(e => AppendLine(e.Message, e.Level));

        try
        {
            var success = await orchestrator.GenerateAsync(
                new ProjectGenerationOrchestratorRequest { ProjectRoot = ProjectPath, Ide = ide },
                eventsProgress);

            CurrentBuildState = success ? BuildState.Success : BuildState.Failed;
            StatusText = success
                ? $"Project: {Path.GetFileName(ProjectPath.TrimEnd(Path.DirectorySeparatorChar))}"
                : "Project file generation failed";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLine($"Unexpected error: {ex.Message}", OrchestratorEventLevel.Error);
            CurrentBuildState = BuildState.Failed;
            StatusText = "Project file generation failed";
        }
        finally
        {
            IsBuilding = false;
        }
    }

    private void AppendLine(string text, OrchestratorEventLevel level)
    {
        OutputLines.Add(new OutputLine { Text = text, Level = level });
        NotifyOutputLinesChanged();
    }

    /// <summary>Writes an explicit "not implemented" line to the output pane for a menu item with
    /// no backing implementation yet, rather than silently doing nothing.</summary>
    public void ReportNotImplemented(string message) => AppendLine(message, OrchestratorEventLevel.Warning);
}

