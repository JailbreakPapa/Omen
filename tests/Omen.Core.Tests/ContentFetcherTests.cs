// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Diagnostics;
using Omen.Core.Rules;

namespace Omen.Core.Tests;

public class ContentFetcherTests : IDisposable
{
    private readonly string _root;

    public ContentFetcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ContentFetcherTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => ContentFetcher.DeleteDirectory(_root); // git clones hold read-only files

    /// <summary>A real local git repository - git clone accepts local paths, so the fetch
    /// path under test is the production one, no network involved.</summary>
    private string CreateGitRepo(string fileName, string content)
    {
        var repoDir = Path.Combine(_root, "upstream");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, fileName), content);
        Run(repoDir, "git", "init -q -b main");
        Run(repoDir, "git", "add .");
        Run(repoDir, "git", "-c user.email=t@t -c user.name=t commit -q -m init");
        return repoDir;
    }

    private static void Run(string workingDir, string tool, string args)
    {
        var process = Process.Start(new ProcessStartInfo(tool, args)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"{tool} {args} should succeed: {process.StandardError.ReadToEnd()}");
    }

    [Fact]
    public void GitFetch_ClonesIntoExternal_AndSkipsWhenStampMatches()
    {
        var repo = CreateGitRepo("Fetched.module.cs", "// fetched rule file\n");
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var fetch = new ContentFetch { Name = "Dep", GitRepository = repo };

        var dirs = ContentFetcher.EnsureFetched([fetch], projectRoot);

        var fetchedFile = Path.Combine(projectRoot, "External", "Dep", "Fetched.module.cs");
        dirs.Should().ContainSingle().Which.Should().Be(Path.Combine(projectRoot, "External", "Dep"));
        File.Exists(fetchedFile).Should().BeTrue();

        // Second call with the same spec must not refetch - prove it by mutating the fetched
        // copy and seeing the mutation survive.
        File.WriteAllText(fetchedFile, "// locally modified\n");
        ContentFetcher.EnsureFetched([fetch], projectRoot);
        File.ReadAllText(fetchedFile).Should().Contain("locally modified");
    }

    [Fact]
    public void GitFetch_ChangedSpec_Refetches()
    {
        var repo = CreateGitRepo("Fetched.module.cs", "// v1\n");
        Run(Path.Combine(_root, "upstream"), "git", "tag v1");
        File.WriteAllText(Path.Combine(_root, "upstream", "Fetched.module.cs"), "// v2\n");
        Run(Path.Combine(_root, "upstream"), "git", "add .");
        Run(Path.Combine(_root, "upstream"), "git", "-c user.email=t@t -c user.name=t commit -q -m v2");
        Run(Path.Combine(_root, "upstream"), "git", "tag v2");

        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var fetchedFile = Path.Combine(projectRoot, "External", "Dep", "Fetched.module.cs");

        ContentFetcher.EnsureFetched([new ContentFetch { Name = "Dep", GitRepository = repo, GitTag = "v1" }], projectRoot);
        File.ReadAllText(fetchedFile).Should().Contain("v1");

        ContentFetcher.EnsureFetched([new ContentFetch { Name = "Dep", GitRepository = repo, GitTag = "v2" }], projectRoot);
        File.ReadAllText(fetchedFile).Should().Contain("v2");
    }

    [Fact]
    public void ArchiveFetch_LocalFile_ExtractsWithRootStripped_AndVerifiesSha256()
    {
        // Build a release-tarball-shaped archive: everything under one wrapping dir.
        var stage = Path.Combine(_root, "stage", "mylib-1.0");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "mylib.h"), "// header\n");
        var archive = Path.Combine(_root, "mylib-1.0.tar.gz");
        Run(Path.Combine(_root, "stage"), "tar", $"-czf \"{archive}\" mylib-1.0");

        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive)));
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);

        ContentFetcher.EnsureFetched(
            [new ContentFetch { Name = "MyLib", ArchiveUrl = archive, ArchiveSha256 = sha }], projectRoot);

        File.Exists(Path.Combine(projectRoot, "External", "MyLib", "mylib.h")).Should().BeTrue();

        // A wrong hash must refuse the archive.
        var bad = () => ContentFetcher.EnsureFetched(
            [new ContentFetch { Name = "Other", ArchiveUrl = archive, ArchiveSha256 = new string('0', 64) }], projectRoot);
        bad.Should().Throw<InvalidOperationException>().WithMessage("*SHA256 mismatch*");
    }

    [Fact]
    public async Task Discovery_IncludesRuleFilesFromFetchedContent()
    {
        // The whole point of fetching at discovery time: a fetched tree's *.module.cs files
        // are part of the compiled rule set, with no second configure pass.
        var repo = CreateGitRepo("Fetched.module.cs", """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class FetchedModule : ModuleRules
            {
                public FetchedModule(BuildContext context) : base(context) { }
            }
            """);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Source"));
        File.WriteAllText(Path.Combine(projectRoot, "Source", "Local.module.cs"), """
            using Omen.Core.Configuration;
            using Omen.Core.Rules;

            public class LocalModule : ModuleRules
            {
                public LocalModule(BuildContext context) : base(context) { }
            }
            """);
        File.WriteAllText(Path.Combine(projectRoot, "Test.omenproject.cs"), $$"""
            using Omen.Core.Rules;

            public class TestProject : OmenProjectRules
            {
                public TestProject()
                {
                    SourceRoots.Add("Source");
                    FetchContent.Add(new ContentFetch { Name = "Dep", GitRepository = @"{{repo}}" });
                }
            }
            """);

        var compiler = new RuleCompiler(Path.Combine(projectRoot, "RuleCache"));
        var discovery = await compiler.DiscoverRuleFilesAsync(projectRoot);

        discovery.ModuleFiles.Should().Contain(f => f.EndsWith("Local.module.cs"));
        discovery.ModuleFiles.Should().Contain(f => f.Contains("External") && f.EndsWith("Fetched.module.cs"));
    }
}
