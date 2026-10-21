// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using Omen.Core.Rules;

namespace Omen.Executors.Orchestration;

public sealed record InstallPlanEntry(string Source, string Destination, string Component);
public sealed record InstalledFile(string Path, string Component, long Size, string Sha256);
public sealed record InstallManifest(string Project, string Version, IReadOnlyList<InstalledFile> Files);

/// <summary>Plans component installations, validates all inputs first, and emits relocatable ZIP packages.</summary>
public sealed class InstallOrchestrator
{
    public const string ManifestName = "omen-install-manifest.json";
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyList<InstallPlanEntry> Plan(string projectRoot, IEnumerable<InstallRule> rules, string? component = null)
    {
        var planned = new Dictionary<string, InstallPlanEntry>(Paths);
        foreach (var rule in rules.Where(r => component == null || r.Component.Equals(component, StringComparison.OrdinalIgnoreCase)))
        {
            string source = Path.GetFullPath(rule.Source, projectRoot);
            string destination = SafeRelative(rule.Destination);
            if (rule.Rename != null && (rule.Directory || string.IsNullOrWhiteSpace(rule.Rename) ||
                    Path.GetFileName(rule.Rename) != rule.Rename || rule.Rename is "." or ".." || rule.Rename.Contains('\\') || rule.Rename.Contains('/')))
                throw new InvalidOperationException("Install Rename must be a single file name and cannot be used with directories.");
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                if (rule.Optional) continue;
                throw new FileNotFoundException($"Install input is missing: '{source}'. Build the target first.", source);
            }
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Install source links are not supported: '{source}'.");
            if (!rule.Directory)
            {
                if (!File.Exists(source)) throw new InvalidOperationException($"Install input is not a file: '{source}'.");
                Add(source, Path.Combine(destination, rule.Rename ?? Path.GetFileName(source)), rule.Component);
                continue;
            }
            if (!Directory.Exists(source)) throw new InvalidOperationException($"Install input is not a directory: '{source}'.");
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(source, "*", options))
            {
                string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                bool Match(string pattern) => FileSystemName.MatchesSimpleExpression(pattern, relative, OperatingSystem.IsWindows());
                if (rule.Patterns.Count > 0 && !rule.Patterns.Any(Match)) continue;
                if (rule.ExcludePatterns.Any(Match)) continue;
                Add(file, Path.Combine(destination, relative), rule.Component);
            }
        }
        if (component != null && planned.Count == 0)
            throw new InvalidOperationException($"Component '{component}' has no installable files.");
        return planned.Values.OrderBy(p => p.Destination, StringComparer.Ordinal).ToList();

        void Add(string source, string destination, string componentName)
        {
            destination = SafeRelative(destination).Replace('\\', '/');
            if (Paths.Equals(destination, ManifestName)) throw new InvalidOperationException($"'{ManifestName}' is reserved for the install manifest.");
            var entry = new InstallPlanEntry(source, destination, componentName);
            if (planned.TryGetValue(destination, out var existing) && !Paths.Equals(existing.Source, source))
                throw new InvalidOperationException($"Conflicting install inputs for '{destination}'.");
            planned[destination] = entry;
        }
    }

    public async Task<InstallManifest> InstallAsync(IReadOnlyList<InstallPlanEntry> plan, string prefix, string project,
        string version, CancellationToken ct = default)
    {
        prefix = Path.GetFullPath(prefix);
        // Resolve every destination before creating directories or copying anything.
        var destinations = plan.Select(p => Destination(prefix, p.Destination)).ToList();
        string manifestPath = Destination(prefix, ManifestName);
        var files = new List<InstalledFile>();
        for (int i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string path = destinations[i];
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".omen-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(plan[i].Source, temporary);
                File.Move(temporary, path, overwrite: true);
                await using var input = File.OpenRead(path);
                files.Add(new(plan[i].Destination, plan[i].Component, input.Length,
                    Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant()));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var manifest = new InstallManifest(project, version, files);
        Directory.CreateDirectory(prefix);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), ct);
        return manifest;
    }

    public async Task<InstallManifest> PackageAsync(IReadOnlyList<InstallPlanEntry> plan, string output, string project,
        string version, CancellationToken ct = default)
    {
        output = Path.GetFullPath(output);
        if (plan.Any(p => Paths.Equals(Path.GetFullPath(p.Source), output)))
            throw new InvalidOperationException("Package output cannot be one of its own inputs.");
        foreach (var entry in plan) _ = SafeRelative(entry.Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = output + ".omen-" + Guid.NewGuid().ToString("N");
        var files = new List<InstalledFile>();
        var timestamp = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            await using (var outputStream = File.Create(temporary))
            using (var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in plan.OrderBy(p => p.Destination, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(file.Destination.Replace('\\', '/'), CompressionLevel.Optimal);
                    entry.LastWriteTime = timestamp;
                    if (!OperatingSystem.IsWindows()) entry.ExternalAttributes = (int)File.GetUnixFileMode(file.Source) << 16;
                    await using var input = File.OpenRead(file.Source);
                    await using var target = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long length = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        length += read;
                    }
                    files.Add(new(file.Destination, file.Component, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                }
                var manifestEntry = archive.CreateEntry(ManifestName);
                manifestEntry.LastWriteTime = timestamp;
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, new InstallManifest(project, version, files), JsonOptions, ct);
            }
            File.Move(temporary, output, overwrite: true);
            return new(project, version, files);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SafeRelative(string path)
    {
        // Portable destination names: a Windows-style rooted path is invalid even on Unix.
        if (Path.IsPathRooted(path) || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\'))
            throw new InvalidOperationException($"Install destination must be relative: '{path}'.");
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == "..")) throw new InvalidOperationException($"Install destination escapes its prefix: '{path}'.");
        return string.Join(Path.DirectorySeparatorChar, segments.Where(s => s != "."));
    }

    private static string Destination(string prefix, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(prefix, SafeRelative(relative)));
        // Existing links anywhere along the destination could redirect writes outside the prefix.
        for (string? parent = path; parent != null; parent = Path.GetDirectoryName(parent))
        {
            if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Install destination traverses a link: '{parent}'.");
        }
        return path;
    }
}
