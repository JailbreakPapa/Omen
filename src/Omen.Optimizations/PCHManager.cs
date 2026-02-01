// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Interfaces;
using Omen.Core.Rules;

namespace Omen.Optimizations;

/// <summary>
/// Manages precompiled header generation and usage.
/// </summary>
public sealed class PCHManager
{
    private readonly IDigestCalculator _digestCalculator;
    private readonly PCHManagerOptions _options;
    
    public PCHManager(
        IDigestCalculator digestCalculator,
        PCHManagerOptions? options = null)
    {
        _digestCalculator = digestCalculator;
        _options = options ?? new PCHManagerOptions();
    }
    
    /// <summary>
    /// Determines the PCH configuration for a module.
    /// </summary>
    public PCHConfiguration GetPCHConfiguration(
        ModuleRules module,
        BuildContext context,
        string intermediateDirectory)
    {
        if (module.PCHUsage == PCHUsage.None)
        {
            return PCHConfiguration.None;
        }
        
        var pchHeaderFile = ResolvePCHHeader(module, context);
        if (string.IsNullOrEmpty(pchHeaderFile))
        {
            return PCHConfiguration.None;
        }
        
        var pchSourceFile = ResolvePCHSource(module, pchHeaderFile);
        var pchOutputPath = GetPCHOutputPath(module, context, intermediateDirectory);
        
        return new PCHConfiguration
        {
            Usage = module.PCHUsage,
            HeaderFile = pchHeaderFile,
            SourceFile = pchSourceFile,
            OutputPath = pchOutputPath,
            ForceIncludes = module.ForcedIncludes.ToList(),
            IncludePaths = module.IncludePaths.ToList()
        };
    }
    
    /// <summary>
    /// Checks if the PCH needs to be rebuilt.
    /// </summary>
    public bool NeedsRebuild(PCHConfiguration config, string intermediateDirectory)
    {
        if (config.Usage == PCHUsage.None)
            return false;
        
        var pchFile = config.OutputPath;
        var manifestFile = Path.Combine(intermediateDirectory, "pch.manifest");
        
        // Check if PCH exists
        if (!File.Exists(pchFile))
            return true;
        
        // Check if manifest exists
        if (!File.Exists(manifestFile))
            return true;
        
        // Load manifest and compare
        var manifest = LoadManifest(manifestFile);
        
        // Check header file
        if (!File.Exists(config.HeaderFile))
            return true;
        
        var currentDigest = ComputeHeaderDigest(config);
        
        return manifest.HeaderDigest != currentDigest.ToString();
    }
    
    /// <summary>
    /// Generates compiler arguments for PCH creation.
    /// </summary>
    public PCHCompilerArgs GetCreationArgs(PCHConfiguration config, TargetPlatform platform)
    {
        var args = new List<string>();
        
        if (platform == TargetPlatform.Windows)
        {
            // MSVC
            args.Add("/Yc" + Path.GetFileName(config.HeaderFile));
            args.Add("/Fp" + config.OutputPath);
        }
        else
        {
            // Clang/GCC
            args.Add("-x");
            args.Add("c++-header");
            args.Add("-o");
            args.Add(config.OutputPath);
        }
        
        return new PCHCompilerArgs
        {
            CreateArgs = args,
            UseArgs = [],
            SourceFile = config.SourceFile ?? config.HeaderFile
        };
    }
    
    /// <summary>
    /// Generates compiler arguments for PCH usage.
    /// </summary>
    public PCHCompilerArgs GetUsageArgs(PCHConfiguration config, TargetPlatform platform)
    {
        var useArgs = new List<string>();
        
        if (platform == TargetPlatform.Windows)
        {
            // MSVC
            useArgs.Add("/Yu" + Path.GetFileName(config.HeaderFile));
            useArgs.Add("/Fp" + config.OutputPath);
            
            // Force include the PCH header
            useArgs.Add("/FI" + Path.GetFileName(config.HeaderFile));
        }
        else
        {
            // Clang/GCC
            useArgs.Add("-include-pch");
            useArgs.Add(config.OutputPath);
            
            // Also include the header normally for source compatibility
            useArgs.Add("-include");
            useArgs.Add(config.HeaderFile);
        }
        
        return new PCHCompilerArgs
        {
            CreateArgs = [],
            UseArgs = useArgs,
            SourceFile = null
        };
    }
    
    /// <summary>
    /// Saves the PCH manifest after successful creation.
    /// </summary>
    public void SaveManifest(PCHConfiguration config, string intermediateDirectory)
    {
        var manifestFile = Path.Combine(intermediateDirectory, "pch.manifest");
        var digest = ComputeHeaderDigest(config);
        
        var manifest = new PCHManifest
        {
            HeaderFile = config.HeaderFile,
            OutputPath = config.OutputPath,
            HeaderDigest = digest.ToString(),
            CreatedAt = DateTime.UtcNow,
            IncludePaths = config.IncludePaths
        };
        
        var json = System.Text.Json.JsonSerializer.Serialize(manifest);
        
        var dir = Path.GetDirectoryName(manifestFile);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        
        File.WriteAllText(manifestFile, json);
    }
    
    private string? ResolvePCHHeader(ModuleRules module, BuildContext context)
    {
        if (!string.IsNullOrEmpty(module.SharedPCHHeaderFile))
        {
            return module.SharedPCHHeaderFile;
        }
        
        // Look for common PCH header names
        var commonNames = new[] { "pch.h", "stdafx.h", "precompiled.h", $"{module.Name}PCH.h" };
        
        foreach (var includePath in module.IncludePaths)
        {
            foreach (var name in commonNames)
            {
                var path = Path.Combine(includePath, name);
                if (File.Exists(path))
                    return path;
            }
        }
        
        return null;
    }
    
    private string? ResolvePCHSource(ModuleRules module, string headerFile)
    {
        // Look for corresponding source file
        var possibleSources = new[]
        {
            Path.ChangeExtension(headerFile, ".cpp"),
            Path.ChangeExtension(headerFile, ".cc"),
            Path.Combine(Path.GetDirectoryName(headerFile) ?? "", 
                Path.GetFileNameWithoutExtension(headerFile) + ".cpp")
        };
        
        return possibleSources.FirstOrDefault(File.Exists);
    }
    
    private string GetPCHOutputPath(ModuleRules module, BuildContext context, string intermediateDirectory)
    {
        var extension = context.Platform == TargetPlatform.Windows ? ".pch" : ".pch.gch";
        return Path.Combine(intermediateDirectory, "PCH", $"{module.Name}{extension}");
    }
    
    private ContentDigest ComputeHeaderDigest(PCHConfiguration config)
    {
        // Hash the header file content and all force-includes
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream);
        
        // Include header content
        if (File.Exists(config.HeaderFile))
        {
            writer.Write(File.ReadAllText(config.HeaderFile));
        }
        
        // Include paths affect PCH validity
        foreach (var path in config.IncludePaths.OrderBy(p => p))
        {
            writer.Write(path);
        }
        
        // Force includes
        foreach (var fi in config.ForceIncludes.OrderBy(f => f))
        {
            writer.Write(fi);
        }
        
        writer.Flush();
        stream.Position = 0;
        
        return _digestCalculator.ComputeDigest(stream);
    }
    
    private static PCHManifest LoadManifest(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<PCHManifest>(json) 
                   ?? new PCHManifest();
        }
        catch
        {
            return new PCHManifest();
        }
    }
}

/// <summary>
/// PCH configuration for a module.
/// </summary>
public sealed class PCHConfiguration
{
    public static readonly PCHConfiguration None = new()
    {
        Usage = PCHUsage.None,
        HeaderFile = "",
        OutputPath = "",
        ForceIncludes = [],
        IncludePaths = []
    };
    
    public required PCHUsage Usage { get; init; }
    public required string HeaderFile { get; init; }
    public string? SourceFile { get; init; }
    public required string OutputPath { get; init; }
    public required List<string> ForceIncludes { get; init; }
    public required List<string> IncludePaths { get; init; }
}

/// <summary>
/// Compiler arguments for PCH creation and usage.
/// </summary>
public sealed class PCHCompilerArgs
{
    public required List<string> CreateArgs { get; init; }
    public required List<string> UseArgs { get; init; }
    public string? SourceFile { get; init; }
}

/// <summary>
/// Manifest file for tracking PCH state.
/// </summary>
internal sealed class PCHManifest
{
    public string HeaderFile { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string HeaderDigest { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<string> IncludePaths { get; set; } = [];
}

/// <summary>
/// Options for PCH management.
/// </summary>
public sealed class PCHManagerOptions
{
    /// <summary>
    /// Minimum file count to enable PCH.
    /// </summary>
    public int MinFilesForPCH { get; init; } = 3;
    
    /// <summary>
    /// Whether to use shared PCH across modules.
    /// </summary>
    public bool UseSharedPCH { get; init; } = true;
}
