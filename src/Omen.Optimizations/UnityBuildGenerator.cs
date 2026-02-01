// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.Optimizations;

/// <summary>
/// Generates unity build source files that #include multiple source files.
/// This reduces compile times by decreasing the number of translation units.
/// </summary>
public sealed class UnityBuildGenerator
{
    private readonly UnityBuildOptions _options;
    
    public UnityBuildGenerator(UnityBuildOptions? options = null)
    {
        _options = options ?? new UnityBuildOptions();
    }
    
    /// <summary>
    /// Generates unity files for the given source files.
    /// </summary>
    public List<UnityFileInfo> GenerateUnityFiles(
        ModuleRules module,
        IReadOnlyList<string> sourceFiles,
        string intermediateDirectory)
    {
        var result = new List<UnityFileInfo>();
        
        // Separate files by type
        var cppFiles = sourceFiles
            .Where(f => IsCppFile(f) && !IsExcluded(f, module))
            .ToList();
        
        var cFiles = sourceFiles
            .Where(f => IsCFile(f) && !IsExcluded(f, module))
            .ToList();
        
        // Generate unity files for C++ sources
        result.AddRange(GenerateUnityFilesForGroup(
            cppFiles, intermediateDirectory, module.Name, ".cpp"));
        
        // Generate unity files for C sources
        result.AddRange(GenerateUnityFilesForGroup(
            cFiles, intermediateDirectory, module.Name, ".c"));
        
        return result;
    }
    
    private List<UnityFileInfo> GenerateUnityFilesForGroup(
        List<string> sourceFiles,
        string intermediateDirectory,
        string moduleName,
        string extension)
    {
        var result = new List<UnityFileInfo>();
        
        if (sourceFiles.Count == 0)
            return result;
        
        // Group files by directory for better locality
        var fileGroups = GroupFilesByDirectory(sourceFiles);
        
        int unityIndex = 0;
        var currentBatch = new List<string>();
        var currentEstimatedSize = 0L;
        
        foreach (var files in fileGroups)
        {
            foreach (var file in files)
            {
                var fileSize = GetFileSize(file);
                
                // Check if adding this file would exceed limits
                if (currentBatch.Count >= _options.MaxFilesPerUnity ||
                    (currentEstimatedSize + fileSize) > _options.MaxBytesPerUnity)
                {
                    if (currentBatch.Count > 0)
                    {
                        result.Add(CreateUnityFile(
                            currentBatch,
                            intermediateDirectory,
                            moduleName,
                            extension,
                            unityIndex++));
                        
                        currentBatch = [];
                        currentEstimatedSize = 0;
                    }
                }
                
                currentBatch.Add(file);
                currentEstimatedSize += fileSize;
            }
        }
        
        // Create final unity file
        if (currentBatch.Count > 0)
        {
            result.Add(CreateUnityFile(
                currentBatch,
                intermediateDirectory,
                moduleName,
                extension,
                unityIndex));
        }
        
        return result;
    }
    
    private UnityFileInfo CreateUnityFile(
        List<string> sourceFiles,
        string intermediateDirectory,
        string moduleName,
        string extension,
        int index)
    {
        var unityFileName = $"{moduleName}.Unity{index}{extension}";
        var unityFilePath = Path.Combine(intermediateDirectory, "Unity", unityFileName);
        
        var content = GenerateUnityFileContent(sourceFiles, moduleName, index);
        
        return new UnityFileInfo
        {
            UnityFilePath = unityFilePath,
            IncludedFiles = sourceFiles.ToList(),
            Content = content
        };
    }
    
    private string GenerateUnityFileContent(
        List<string> sourceFiles,
        string moduleName,
        int index)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("// Auto-generated Unity Build File");
        sb.AppendLine($"// Module: {moduleName}");
        sb.AppendLine($"// Unity Index: {index}");
        sb.AppendLine($"// Files: {sourceFiles.Count}");
        sb.AppendLine($"// Generated: {DateTime.UtcNow:O}");
        sb.AppendLine();
        
        // Disable warnings that commonly occur in unity builds
        if (_options.DisableWarnings)
        {
            sb.AppendLine("#if defined(_MSC_VER)");
            sb.AppendLine("#pragma warning(push)");
            sb.AppendLine("#pragma warning(disable: 4005) // macro redefinition");
            sb.AppendLine("#pragma warning(disable: 4244) // conversion warning");
            sb.AppendLine("#endif");
            sb.AppendLine();
        }
        
        foreach (var file in sourceFiles)
        {
            // Use relative paths when possible for cleaner output
            var includePath = file.Replace('\\', '/');
            sb.AppendLine($"#include \"{includePath}\"");
        }
        
        if (_options.DisableWarnings)
        {
            sb.AppendLine();
            sb.AppendLine("#if defined(_MSC_VER)");
            sb.AppendLine("#pragma warning(pop)");
            sb.AppendLine("#endif");
        }
        
        return sb.ToString();
    }
    
    private IEnumerable<IEnumerable<string>> GroupFilesByDirectory(List<string> files)
    {
        if (!_options.GroupByDirectory)
        {
            return [files];
        }
        
        return files
            .GroupBy(f => Path.GetDirectoryName(f))
            .Select(g => g.AsEnumerable());
    }
    
    private bool IsExcluded(string file, ModuleRules module)
    {
        var fileName = Path.GetFileName(file);
        
        // Check explicit exclusions
        if (module.UnityBuildExclusions.Any(e => 
            fileName.Equals(e, StringComparison.OrdinalIgnoreCase) ||
            file.Contains(e, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        
        // Exclude PCH source files
        if (module.PCHUsage != PCHUsage.None && !string.IsNullOrEmpty(module.SharedPCHHeaderFile))
        {
            var pchSource = Path.ChangeExtension(module.SharedPCHHeaderFile, ".cpp");
            if (fileName.Equals(Path.GetFileName(pchSource), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        
        return false;
    }
    
    private static bool IsCppFile(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".cpp" or ".cc" or ".cxx";
    }
    
    private static bool IsCFile(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext == ".c";
    }
    
    private static long GetFileSize(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch
        {
            return 0;
        }
    }
    
    /// <summary>
    /// Writes unity files to disk.
    /// </summary>
    public void WriteUnityFiles(IEnumerable<UnityFileInfo> unityFiles)
    {
        foreach (var unity in unityFiles)
        {
            var dir = Path.GetDirectoryName(unity.UnityFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            // Check if content changed before writing
            if (File.Exists(unity.UnityFilePath))
            {
                var existing = File.ReadAllText(unity.UnityFilePath);
                if (existing == unity.Content)
                    continue;
            }
            
            File.WriteAllText(unity.UnityFilePath, unity.Content);
        }
    }
}

/// <summary>
/// Information about a generated unity file.
/// </summary>
public sealed class UnityFileInfo
{
    public required string UnityFilePath { get; init; }
    public required List<string> IncludedFiles { get; init; }
    public required string Content { get; init; }
}

/// <summary>
/// Options for unity build generation.
/// </summary>
public sealed class UnityBuildOptions
{
    /// <summary>
    /// Maximum number of source files per unity file.
    /// </summary>
    public int MaxFilesPerUnity { get; init; } = 16;
    
    /// <summary>
    /// Maximum total bytes of source files per unity file.
    /// </summary>
    public long MaxBytesPerUnity { get; init; } = 256 * 1024; // 256 KB
    
    /// <summary>
    /// Whether to group files by directory for better locality.
    /// </summary>
    public bool GroupByDirectory { get; init; } = true;
    
    /// <summary>
    /// Whether to disable common warnings in unity builds.
    /// </summary>
    public bool DisableWarnings { get; init; } = true;
}
