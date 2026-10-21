// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Diagnostics;
using System.Security.Cryptography;

namespace Omen.Core.Rules;

/// <summary>
/// One source dependency fetched into the project at rule-discovery time - Omen's
/// CMake-FetchContent equivalent, declared on <see cref="OmenProjectRules.FetchContent"/>.
/// Exactly one source: a git repository (+ optional tag/branch/commit) or an archive
/// (+ optional SHA256). Content lands in <c>External/&lt;Name&gt;</c> under the project root
/// (add it to .gitignore) and that directory joins the rule-file scan roots - so a fetched
/// tree that carries its own *.module.cs files participates directly, and a plain C++
/// library is consumed by writing a module whose SourceDirectory points into it.
/// </summary>
public sealed class ContentFetch
{
    public required string Name { get; init; }

    /// <summary>Git clone URL - any URL git itself accepts, including a local path.</summary>
    public string? GitRepository { get; init; }

    /// <summary>Tag, branch, or full 40-hex commit for <see cref="GitRepository"/>. Null clones
    /// the default branch.</summary>
    public string? GitTag { get; init; }

    /// <summary>Archive to download and extract (.tar.gz/.tar.xz/.zip - anything bsdtar reads).
    /// http(s) URLs download; anything else is treated as a local file path.</summary>
    public string? ArchiveUrl { get; init; }

    /// <summary>Optional SHA256 the downloaded archive must match.</summary>
    public string? ArchiveSha256 { get; init; }

    /// <summary>Strip the archive's single wrapping top-level directory (the near-universal
    /// release-tarball shape). Set false for archives whose entries start at the root.</summary>
    public bool StripArchiveRoot { get; init; } = true;

    /// <summary>The identity written to the stamp file - changing any part of it refetches.</summary>
    internal string Spec => $"git={GitRepository}@{GitTag}|archive={ArchiveUrl}#{ArchiveSha256}|strip={StripArchiveRoot}";
}

/// <summary>
/// Materializes <see cref="ContentFetch"/> declarations on disk. Runs synchronously inside
/// rule discovery (the fetched tree must exist before the rule-file glob), guarded by a stamp
/// file per fetch so an unchanged declaration costs one file read. A changed declaration
/// deletes and refetches the directory - the directory is Omen's, not a workspace.
/// </summary>
public static class ContentFetcher
{
    public const string ExternalDirectoryName = "External";
    private const string StampFileName = ".omen-fetch";

    /// <summary>Ensures every declared fetch is present; returns the fetched directories
    /// (existing or new), for appending to the rule-file scan roots.</summary>
    public static IReadOnlyList<string> EnsureFetched(IReadOnlyList<ContentFetch> fetches, string projectRoot)
    {
        var directories = new List<string>();
        foreach (var fetch in fetches)
        {
            var destination = Path.Combine(projectRoot, ExternalDirectoryName, fetch.Name);
            var stampPath = Path.Combine(destination, StampFileName);

            if (!File.Exists(stampPath) || File.ReadAllText(stampPath) != fetch.Spec)
            {
                DeleteDirectory(destination);
                Fetch(fetch, destination);
                File.WriteAllText(stampPath, fetch.Spec);
            }
            directories.Add(destination);
        }
        return directories;
    }

    /// <summary>Recursive delete that survives git clones on Windows, whose .git object files
    /// are read-only and make a plain Directory.Delete throw UnauthorizedAccessException.</summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static void Fetch(ContentFetch fetch, string destination)
    {
        if (fetch.GitRepository != null && fetch.ArchiveUrl != null)
            throw new InvalidOperationException($"FetchContent '{fetch.Name}': declare GitRepository or ArchiveUrl, not both.");

        if (fetch.GitRepository != null)
            FetchGit(fetch, destination);
        else if (fetch.ArchiveUrl != null)
            FetchArchive(fetch, destination);
        else
            throw new InvalidOperationException($"FetchContent '{fetch.Name}': declare GitRepository or ArchiveUrl.");
    }

    private static void FetchGit(ContentFetch fetch, string destination)
    {
        var isCommitSha = fetch.GitTag is { Length: 40 } sha && sha.All(Uri.IsHexDigit);
        if (isCommitSha)
        {
            // A commit can't be cloned by name - full clone, then detach onto it.
            RunTool("git", $"clone \"{fetch.GitRepository}\" \"{destination}\"", fetch.Name);
            RunTool("git", $"-C \"{destination}\" -c advice.detachedHead=false checkout {fetch.GitTag}", fetch.Name);
        }
        else
        {
            var branch = fetch.GitTag != null ? $"--branch \"{fetch.GitTag}\" " : "";
            RunTool("git", $"clone --depth 1 {branch}-c advice.detachedHead=false \"{fetch.GitRepository}\" \"{destination}\"", fetch.Name);
        }
    }

    private static void FetchArchive(ContentFetch fetch, string destination)
    {
        var archivePath = fetch.ArchiveUrl!;
        string? downloaded = null;
        try
        {
            if (archivePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                archivePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                downloaded = Path.Combine(Path.GetTempPath(), $"omen-fetch-{Guid.NewGuid():N}{Path.GetExtension(new Uri(archivePath).AbsolutePath)}");
                using var client = new HttpClient();
                using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, archivePath), HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using var file = File.Create(downloaded);
                response.Content.ReadAsStream().CopyTo(file);
                archivePath = downloaded;
            }

            if (fetch.ArchiveSha256 != null)
            {
                using var stream = File.OpenRead(archivePath);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!actual.Equals(fetch.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"FetchContent '{fetch.Name}': archive SHA256 mismatch - expected {fetch.ArchiveSha256}, got {actual}.");
                }
            }

            Directory.CreateDirectory(destination);
            var strip = fetch.StripArchiveRoot ? "--strip-components=1 " : "";
            // bsdtar (ships with Windows 10+, standard on Unix) reads tar.gz/tar.xz/zip alike.
            RunTool("tar", $"-xf \"{archivePath}\" -C \"{destination}\" {strip}", fetch.Name);
        }
        finally
        {
            if (downloaded != null && File.Exists(downloaded))
                File.Delete(downloaded);
        }
    }

    private static void RunTool(string tool, string arguments, string fetchName)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = tool,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"FetchContent '{fetchName}': could not start '{tool}'.");

        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FetchContent '{fetchName}': {tool} {arguments} exited with {process.ExitCode}: {stderr.Trim()}");
        }
    }
}
