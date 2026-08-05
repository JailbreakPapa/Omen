// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;

namespace Omen.GUI.Models;

public sealed class ProjectTreeNode
{
    private static readonly string[] SkipDirectoryNames =
        ["bin", "obj", ".git", ".vs", "node_modules", "Intermediate", "Binaries"];

    private static readonly string[] RelevantExtensions =
        [".cs", ".cpp", ".c", ".h", ".hpp", ".json", ".xml"];

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public ObservableCollection<ProjectTreeNode> Children { get; } = [];

    /// <summary>
    /// Builds a tree rooted at <paramref name="path"/>, skipping build-output and VCS
    /// directories and showing only build-rule and source files - matching the filter
    /// the abandoned Qt prototype's ProjectTree used, plus *.gem.cs which postdates it.
    /// </summary>
    public static ProjectTreeNode BuildTree(string path)
    {
        var root = new ProjectTreeNode { Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), FullPath = path, IsDirectory = true };
        Populate(root, path);
        return root;
    }

    private static void Populate(ProjectTreeNode parent, string path)
    {
        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(path)
                .Where(d => !SkipDirectoryNames.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(d => d)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A locked/permission-denied subdirectory shouldn't abort the whole tree - skip it.
            directories = [];
        }

        foreach (var dir in directories)
        {
            var node = new ProjectTreeNode { Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true };
            parent.Children.Add(node);
            Populate(node, dir);
        }

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(path)
                .Where(IsRelevantFile)
                .OrderBy(f => f)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            files = [];
        }

        foreach (var file in files)
        {
            parent.Children.Add(new ProjectTreeNode { Name = Path.GetFileName(file), FullPath = file, IsDirectory = false });
        }
    }

    private static bool IsRelevantFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.')) return false;
        if (name.EndsWith(".target.cs") || name.EndsWith(".module.cs") || name.EndsWith(".gem.cs")) return true;
        return RelevantExtensions.Contains(Path.GetExtension(path));
    }
}
