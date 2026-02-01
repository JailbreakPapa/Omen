// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Omen.Core.Configuration;

namespace Omen.Core.Rules;

/// <summary>
/// Compiles .module.cs and .target.cs rule files using Roslyn.
/// </summary>
public sealed class RuleCompiler
{
    private readonly string _cacheDirectory;
    private readonly List<MetadataReference> _references;
    
    public RuleCompiler(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "Omen", "RuleCache");
        Directory.CreateDirectory(_cacheDirectory);
        
        // Build references from current assemblies
        _references = BuildMetadataReferences();
    }
    
    /// <summary>
    /// Compiles and loads rule files from a project directory.
    /// </summary>
    public async Task<CompiledRules> CompileRulesAsync(string projectRoot, CancellationToken ct = default)
    {
        var moduleFiles = Directory.GetFiles(projectRoot, "*.module.cs", SearchOption.AllDirectories);
        var targetFiles = Directory.GetFiles(projectRoot, "*.target.cs", SearchOption.AllDirectories);
        
        if (moduleFiles.Length == 0 && targetFiles.Length == 0)
        {
            throw new InvalidOperationException($"No rule files found in '{projectRoot}'.");
        }
        
        var allFiles = moduleFiles.Concat(targetFiles).ToList();
        
        // Check if we have a valid cached assembly
        var cacheKey = ComputeCacheKey(allFiles);
        var cachedAssemblyPath = Path.Combine(_cacheDirectory, $"{cacheKey}.dll");
        
        Assembly assembly;
        if (File.Exists(cachedAssemblyPath) && IsCacheValid(cachedAssemblyPath, allFiles))
        {
            assembly = LoadAssembly(cachedAssemblyPath);
        }
        else
        {
            assembly = await CompileFilesAsync(allFiles, cachedAssemblyPath, ct);
        }
        
        return new CompiledRules(assembly, moduleFiles, targetFiles);
    }
    
    private async Task<Assembly> CompileFilesAsync(List<string> files, string outputPath, CancellationToken ct)
    {
        var syntaxTrees = new List<SyntaxTree>();
        
        foreach (var file in files)
        {
            var code = await File.ReadAllTextAsync(file, ct);
            var syntaxTree = CSharpSyntaxTree.ParseText(
                code,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12),
                path: file);
            syntaxTrees.Add(syntaxTree);
        }
        
        var assemblyName = $"OmenRules_{Guid.NewGuid():N}";
        
        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            _references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release,
                allowUnsafe: false));
        
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms, cancellationToken: ct);
        
        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Location}: {d.GetMessage()}")
                .ToList();
            
            throw new RuleCompilationException(
                $"Failed to compile rule files:\n{string.Join("\n", errors)}",
                result.Diagnostics);
        }
        
        // Save to cache
        ms.Seek(0, SeekOrigin.Begin);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using (var fs = File.Create(outputPath))
        {
            await ms.CopyToAsync(fs, ct);
        }
        
        // Load from memory
        ms.Seek(0, SeekOrigin.Begin);
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }
    
    private Assembly LoadAssembly(string path)
    {
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    
    private List<MetadataReference> BuildMetadataReferences()
    {
        var references = new List<MetadataReference>();
        
        // Add core assemblies
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString();
        if (trustedAssemblies != null)
        {
            foreach (var assembly in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(assembly));
                    }
                    catch
                    {
                        // Skip problematic assemblies
                    }
                }
            }
        }
        
        // Add Omen.Core assembly
        var coreAssembly = typeof(ModuleRules).Assembly;
        references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
        
        return references;
    }
    
    private string ComputeCacheKey(List<string> files)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var content = string.Join("|", files.OrderBy(f => f).Select(f => $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}"));
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash)[..16];
    }
    
    private bool IsCacheValid(string cachedPath, List<string> sourceFiles)
    {
        var cacheTime = File.GetLastWriteTimeUtc(cachedPath);
        return sourceFiles.All(f => File.GetLastWriteTimeUtc(f) < cacheTime);
    }
}

/// <summary>
/// Represents compiled rule files.
/// </summary>
public sealed class CompiledRules
{
    private readonly Assembly _assembly;
    
    public IReadOnlyList<string> ModuleFiles { get; }
    public IReadOnlyList<string> TargetFiles { get; }
    
    internal CompiledRules(Assembly assembly, IReadOnlyList<string> moduleFiles, IReadOnlyList<string> targetFiles)
    {
        _assembly = assembly;
        ModuleFiles = moduleFiles;
        TargetFiles = targetFiles;
    }
    
    /// <summary>
    /// Creates instances of all ModuleRules in the compiled assembly.
    /// </summary>
    public IReadOnlyList<ModuleRules> CreateModuleRules(BuildContext context)
    {
        var moduleType = typeof(ModuleRules);
        var rules = new List<ModuleRules>();
        
        foreach (var type in _assembly.GetTypes())
        {
            if (type.IsAbstract || !moduleType.IsAssignableFrom(type))
                continue;
            
            var constructor = type.GetConstructor([typeof(BuildContext)]);
            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Module rule type '{type.Name}' must have a constructor that takes BuildContext.");
            }
            
            var instance = (ModuleRules)constructor.Invoke([context]);
            rules.Add(instance);
        }
        
        return rules;
    }
    
    /// <summary>
    /// Creates instances of all TargetRules in the compiled assembly.
    /// </summary>
    public IReadOnlyList<TargetRules> CreateTargetRules(BuildContext context)
    {
        var targetType = typeof(TargetRules);
        var rules = new List<TargetRules>();
        
        foreach (var type in _assembly.GetTypes())
        {
            if (type.IsAbstract || !targetType.IsAssignableFrom(type))
                continue;
            
            var constructor = type.GetConstructor([typeof(BuildContext)]);
            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"Target rule type '{type.Name}' must have a constructor that takes BuildContext.");
            }
            
            var instance = (TargetRules)constructor.Invoke([context]);
            rules.Add(instance);
        }
        
        return rules;
    }
    
    /// <summary>
    /// Gets a specific target by name.
    /// </summary>
    public TargetRules? GetTarget(string name, BuildContext context)
    {
        return CreateTargetRules(context).FirstOrDefault(t => 
            t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    
    /// <summary>
    /// Gets a specific module by name.
    /// </summary>
    public ModuleRules? GetModule(string name, BuildContext context)
    {
        return CreateModuleRules(context).FirstOrDefault(m => 
            m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Exception thrown when rule compilation fails.
/// </summary>
public sealed class RuleCompilationException : Exception
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    
    public RuleCompilationException(string message, IEnumerable<Diagnostic> diagnostics) 
        : base(message)
    {
        Diagnostics = diagnostics.ToList();
    }
}
