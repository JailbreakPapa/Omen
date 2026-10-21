// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text.Json;

namespace Omen.GUI.Services;

public sealed class GuiSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Omen", "gui-settings.json");

    public string? LastProjectPath { get; set; }

    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public bool ConfirmBeforeClean { get; set; } = true;
    public string? DefaultProjectFolder { get; set; }
    public string DefaultConfiguration { get; set; } = "Development";
    public int ParallelJobs { get; set; } = Environment.ProcessorCount;
    public bool UseUnityBuilds { get; set; } = true;
    public bool UsePrecompiledHeaders { get; set; } = true;
    public bool EnableLto { get; set; }
    public bool EnableDistributedBuilds { get; set; }
    public string CoordinatorAddress { get; set; } = "";
    public int LocalAgentJobs { get; set; } = Environment.ProcessorCount;
    public string Theme { get; set; } = "Dark";
    public string UiDensity { get; set; } = "Standard";
    public int ConsoleFontSize { get; set; } = 12;

    public static GuiSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new GuiSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<GuiSettings>(json) ?? new GuiSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new GuiSettings();
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A settings-save failure shouldn't crash a build that otherwise succeeded;
            // silently not persisting is an acceptable degradation here.
        }
    }
}
