// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Omen.GUI.Models;

/// <summary>
/// One node in the project tree. Children are populated on first expand rather than eagerly:
/// the previous implementation walked the entire directory tree synchronously inside
/// LoadProject, which hangs the window on a large repository.
/// </summary>
public sealed partial class ProjectTreeNode : ObservableObject
{
    private static readonly string[] SkipDirectoryNames =
        ["bin", "obj", ".git", ".vs", "node_modules", "Intermediate", "Binaries"];

    private static readonly string[] RelevantExtensions =
        [".cs", ".cpp", ".c", ".h", ".hpp", ".json", ".xml"];

    private bool _childrenLoaded;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }

    public ObservableCollection<ProjectTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public static IEnumerable<string> EnumerateSearchableFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRelevantFile(file))
                    yield return file;
            }
            foreach (var child in directories)
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !SkipDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(child);
            }
        }
    }

    public bool Reveal(string filePath)
    {
        var target = Path.GetFullPath(filePath);
        if (string.Equals(Path.GetFullPath(FullPath), target, StringComparison.OrdinalIgnoreCase))
        {
            IsSelected = true;
            return true;
        }
        if (!IsDirectory || !Path.GetRelativePath(FullPath, target).Split(Path.DirectorySeparatorChar).All(p => p != ".."))
            return false;
        IsExpanded = true;
        return Children.Any(child => child.Reveal(target));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            LoadChildren();
    }

    /// <summary>
    /// Builds a tree rooted at <paramref name="path"/>, skipping build-output and VCS
    /// directories and showing only build-rule and source files - matching the filter
    /// the abandoned Qt prototype's ProjectTree used, plus *.plugin.cs which postdates it.
    /// Only the root's immediate children are loaded - subdirectories load lazily on expand.
    /// </summary>
    public static ProjectTreeNode BuildTree(string path)
    {
        var root = new ProjectTreeNode
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            FullPath = path,
            IsDirectory = true
        };

        root.LoadChildren();
        return root;
    }

    /// <summary>
    /// Populates this node's immediate children, once. Each child directory is peeked one
    /// level ahead so its expander chevron only appears when it actually has contents.
    /// </summary>
    public void LoadChildren()
    {
        if (_childrenLoaded || !IsDirectory)
            return;

        _childrenLoaded = true;
        Children.Clear();

        foreach (var directory in EnumerateDirectories())
        {
            var child = new ProjectTreeNode
            {
                Name = Path.GetFileName(directory),
                FullPath = directory,
                IsDirectory = true
            };
            Children.Add(child);
            // Load one level ahead so the expander chevron only appears on non-empty folders.
            child.PeekForExpander();
        }

        foreach (var file in EnumerateFiles())
        {
            Children.Add(new ProjectTreeNode
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                IsDirectory = false
            });
        }
    }

    /// <summary>Marker child that makes the expander chevron appear before real children load.</summary>
    private static ProjectTreeNode CreatePlaceholder() => new()
    {
        Name = "Loading...",
        FullPath = string.Empty,
        IsDirectory = false
    };

    private void PeekForExpander()
    {
        if (!EnumerateDirectories().Any() && !EnumerateFiles().Any())
            return;

        Children.Add(CreatePlaceholder());
        _childrenLoaded = false;
    }

    private IEnumerable<string> EnumerateDirectories()
    {
        try
        {
            return Directory.EnumerateDirectories(FullPath)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return !name.StartsWith('.') && !SkipDirectoryNames.Contains(name);
                })
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private IEnumerable<string> EnumerateFiles()
    {
        try
        {
            return Directory.EnumerateFiles(FullPath)
                .Where(IsRelevantFile)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool IsRelevantFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.'))
            return false;

        if (name.EndsWith(".target.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".module.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".plugin.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        return RelevantExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
}
