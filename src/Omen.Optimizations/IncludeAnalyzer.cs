// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Concurrent;
using System.Text;
using Omen.Core.Interfaces;

namespace Omen.Optimizations;

/// <summary>
/// Analyzes include dependencies for build optimization.
/// </summary>
public sealed class IncludeAnalyzer
{
    private readonly IDigestCalculator _digestCalculator;
    private readonly ConcurrentDictionary<string, IncludeInfo> _includeCache = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _includeGraph = new();
    
    public IncludeAnalyzer(IDigestCalculator digestCalculator)
    {
        _digestCalculator = digestCalculator;
    }
    
    /// <summary>
    /// Analyzes includes for a source file.
    /// </summary>
    public IncludeInfo AnalyzeFile(string filePath, IReadOnlyList<string> includePaths)
    {
        if (_includeCache.TryGetValue(filePath, out var cached))
        {
            // Verify cache is still valid
            if (File.Exists(filePath) && 
                new FileInfo(filePath).LastWriteTimeUtc <= cached.AnalyzedAt)
            {
                return cached;
            }
        }
        
        var includes = new HashSet<string>();
        var systemIncludes = new HashSet<string>();
        var visited = new HashSet<string>();
        
        AnalyzeFileRecursive(filePath, includePaths, includes, systemIncludes, visited);
        
        var info = new IncludeInfo
        {
            FilePath = filePath,
            DirectIncludes = includes.ToList(),
            SystemIncludes = systemIncludes.ToList(),
            AllIncludes = visited.ToList(),
            AnalyzedAt = DateTime.UtcNow,
            ContentDigest = ComputeTransitiveDigest(filePath, visited)
        };
        
        _includeCache[filePath] = info;
        _includeGraph[filePath] = includes;
        
        return info;
    }
    
    /// <summary>
    /// Gets files that include the specified header.
    /// </summary>
    public IEnumerable<string> GetDependentFiles(string headerPath)
    {
        var normalizedPath = NormalizePath(headerPath);
        
        return _includeGraph
            .Where(kvp => kvp.Value.Contains(normalizedPath))
            .Select(kvp => kvp.Key);
    }
    
    /// <summary>
    /// Computes which files need recompilation when a header changes.
    /// </summary>
    public IEnumerable<string> GetAffectedFiles(string changedHeader)
    {
        var affected = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(NormalizePath(changedHeader));
        
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            
            foreach (var (file, includes) in _includeGraph)
            {
                if (includes.Contains(current) && affected.Add(file))
                {
                    // If this is also a header, propagate
                    if (IsHeaderFile(file))
                    {
                        queue.Enqueue(file);
                    }
                }
            }
        }
        
        return affected;
    }
    
    /// <summary>
    /// Finds candidates for precompiled headers based on include frequency.
    /// </summary>
    public IReadOnlyList<string> FindPCHCandidates(
        IEnumerable<string> sourceFiles,
        IReadOnlyList<string> includePaths,
        int topN = 10)
    {
        var includeFrequency = new Dictionary<string, int>();
        
        foreach (var source in sourceFiles)
        {
            var info = AnalyzeFile(source, includePaths);
            
            foreach (var include in info.DirectIncludes)
            {
                if (!includeFrequency.ContainsKey(include))
                    includeFrequency[include] = 0;
                
                includeFrequency[include]++;
            }
        }
        
        // Return headers used by most files
        return includeFrequency
            .OrderByDescending(kvp => kvp.Value)
            .Take(topN)
            .Select(kvp => kvp.Key)
            .ToList();
    }
    
    /// <summary>
    /// Generates an optimal PCH header based on analysis.
    /// </summary>
    public string GeneratePCHContent(
        IEnumerable<string> sourceFiles,
        IReadOnlyList<string> includePaths,
        int minUsageCount = 3)
    {
        var candidates = new Dictionary<string, int>();
        
        foreach (var source in sourceFiles)
        {
            var info = AnalyzeFile(source, includePaths);
            
            foreach (var include in info.DirectIncludes)
            {
                if (!candidates.ContainsKey(include))
                    candidates[include] = 0;
                
                candidates[include]++;
            }
        }
        
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Precompiled Header");
        sb.AppendLine($"// Generated: {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("#pragma once");
        sb.AppendLine();
        
        // System includes first
        var systemHeaders = candidates
            .Where(kvp => kvp.Value >= minUsageCount)
            .Where(kvp => IsSystemHeader(kvp.Key))
            .OrderByDescending(kvp => kvp.Value);
        
        foreach (var (header, _) in systemHeaders)
        {
            sb.AppendLine($"#include <{Path.GetFileName(header)}>");
        }
        
        sb.AppendLine();
        
        // Project includes
        var projectHeaders = candidates
            .Where(kvp => kvp.Value >= minUsageCount)
            .Where(kvp => !IsSystemHeader(kvp.Key))
            .OrderByDescending(kvp => kvp.Value);
        
        foreach (var (header, _) in projectHeaders)
        {
            sb.AppendLine($"#include \"{header}\"");
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Clears the include cache.
    /// </summary>
    public void ClearCache()
    {
        _includeCache.Clear();
        _includeGraph.Clear();
    }
    
    private void AnalyzeFileRecursive(
        string filePath,
        IReadOnlyList<string> includePaths,
        HashSet<string> includes,
        HashSet<string> systemIncludes,
        HashSet<string> visited)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!visited.Add(normalizedPath))
            return;
        
        if (!File.Exists(filePath))
            return;
        
        try
        {
            var lines = File.ReadAllLines(filePath);
            
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                
                if (!trimmed.StartsWith("#include"))
                    continue;
                
                var (includePath, isSystem) = ParseIncludeDirective(trimmed);
                
                if (string.IsNullOrEmpty(includePath))
                    continue;
                
                if (isSystem)
                {
                    systemIncludes.Add(includePath);
                    continue;
                }
                
                // Resolve the include path
                var resolved = ResolveInclude(includePath, filePath, includePaths);
                
                if (!string.IsNullOrEmpty(resolved))
                {
                    includes.Add(resolved);
                    
                    // Recursively analyze
                    AnalyzeFileRecursive(resolved, includePaths, includes, systemIncludes, visited);
                }
            }
        }
        catch
        {
            // Ignore file read errors
        }
    }
    
    private static (string path, bool isSystem) ParseIncludeDirective(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line, @"#include\s*([<""])(.+?)[>""]");
        
        if (!match.Success)
            return ("", false);
        
        var isSystem = match.Groups[1].Value == "<";
        var path = match.Groups[2].Value;
        
        return (path, isSystem);
    }
    
    private static string? ResolveInclude(
        string includePath,
        string sourceFile,
        IReadOnlyList<string> includePaths)
    {
        // Try relative to source file first
        var sourceDir = Path.GetDirectoryName(sourceFile);
        if (!string.IsNullOrEmpty(sourceDir))
        {
            var relative = Path.Combine(sourceDir, includePath);
            if (File.Exists(relative))
                return NormalizePath(Path.GetFullPath(relative));
        }
        
        // Try include paths
        foreach (var incPath in includePaths)
        {
            var full = Path.Combine(incPath, includePath);
            if (File.Exists(full))
                return NormalizePath(Path.GetFullPath(full));
        }
        
        return null;
    }
    
    private ContentDigest ComputeTransitiveDigest(string filePath, HashSet<string> allIncludes)
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream);
        
        // Include the source file
        if (File.Exists(filePath))
        {
            writer.Write(File.ReadAllText(filePath));
        }
        
        // Include all headers in sorted order for determinism
        foreach (var include in allIncludes.OrderBy(i => i))
        {
            if (File.Exists(include))
            {
                writer.Write(File.ReadAllText(include));
            }
        }
        
        writer.Flush();
        stream.Position = 0;
        
        return _digestCalculator.ComputeDigest(stream);
    }
    
    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
    }
    
    private static bool IsHeaderFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".h" or ".hpp" or ".hxx" or ".hh";
    }
    
    private static bool IsSystemHeader(string path)
    {
        // Check if it's in common system paths
        var lower = path.ToLowerInvariant();
        return lower.Contains("/usr/include") ||
               lower.Contains("\\program files") ||
               lower.Contains("\\windows kits") ||
               lower.Contains("\\vc\\include");
    }
}

/// <summary>
/// Information about includes for a source file.
/// </summary>
public sealed class IncludeInfo
{
    public required string FilePath { get; init; }
    public required List<string> DirectIncludes { get; init; }
    public required List<string> SystemIncludes { get; init; }
    public required List<string> AllIncludes { get; init; }
    public required DateTime AnalyzedAt { get; init; }
    public required ContentDigest ContentDigest { get; init; }
}
