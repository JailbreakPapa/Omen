// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Executors.Orchestration;

namespace Omen.Executors.Tests;

/// <summary>
/// Regression coverage for the intermittent BuildOrchestratorTests failures (varying which
/// assertion on the accumulated `events` list fails, run to run). Root cause: every call site
/// reported OrchestratorEvent progress through a plain <c>System.Progress&lt;T&gt;</c>, which
/// always marshals its callback asynchronously (via a captured SynchronizationContext, or
/// ThreadPool.QueueUserWorkItem when there is none - the case here) instead of running it on
/// the reporting thread. Nothing in BuildOrchestrator awaits after its last Report() call, so
/// whether that queued callback has run by the time `await orchestrator.BuildAsync(...)`
/// resumes is a race - one that a real test process loses far more often once the ThreadPool is
/// under the kind of pressure many concurrently-compiling xUnit test classes create (exactly
/// what running the whole suite does), which is why the failure was invisible in isolation but
/// reproducible in the full run.
///
/// This test exercises many concurrent BuildOrchestrator.BuildAsync calls at once - deliberately
/// recreating that ThreadPool pressure via real concurrent Roslyn compilation rather than a
/// synthetic delay - and asserts, for every one of them, that its reported events are fully
/// present the instant its own `await` resumes, with no extra delay given for a lagging
/// callback to catch up. It only holds because BuildCommand/CleanCommand/GenerateCommand and
/// these test fixtures now use Omen.Core.Diagnostics.SynchronousProgress&lt;T&gt; (see
/// SynchronousProgress.cs), which invokes its handler synchronously and so never races the
/// awaited Task's completion. Swapping any one of these call sites back to
/// <c>new Progress&lt;OrchestratorEvent&gt;(...)</c> reproduces the original flake here directly
/// (verified while diagnosing this bug: reliably fails within a handful of runs under this
/// concurrency).
/// </summary>
public class ProgressReportingConcurrencyTests
{
    [Fact]
    public async Task BuildAsync_ManyConcurrentInvocations_EveryEventIsObservableImmediatelyAfterAwait()
    {
        const int concurrency = 40;

        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ProgressReportingConcurrencyTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(projectRoot);
            try
            {
                // Same shape as BuildAsync_NoModulesFound_ReturnsSuccessfulZeroActionResult: a
                // target with zero modules, so BuildAsync reports the "No modules to build."
                // warning right before returning, with no further await to let a lagging
                // async-marshaled callback catch up.
                var targetFile = Path.Combine(projectRoot, "Empty.target.cs");
                await File.WriteAllTextAsync(targetFile, """
                    using Omen.Core.Configuration;
                    using Omen.Core.Rules;

                    public class EmptyTarget : TargetRules
                    {
                        public EmptyTarget(BuildContext context) : base(context)
                        {
                            Type = TargetType.Executable;
                        }
                    }
                    """);

                var orchestrator = new BuildOrchestrator();
                var events = new List<OrchestratorEvent>();
                var request = new BuildOrchestratorRequest
                {
                    TargetFile = targetFile,
                    Platform = TargetPlatform.Windows,
                    Architecture = TargetArchitecture.X64,
                    Configuration = BuildConfiguration.Development
                };

                var result = await orchestrator.BuildAsync(
                    request,
                    new SynchronousProgress<OrchestratorEvent>(events.Add),
                    buildProgress: null);

                // Checked the instant this call's own await resumes - no Task.Delay, no
                // Thread.Sleep, nothing giving a queued callback extra time to run.
                result.Should().NotBeNull();
                result!.Success.Should().BeTrue();
                result.TotalActions.Should().Be(0);
                events.Should().Contain(e => e.Level == OrchestratorEventLevel.Warning && e.Message.Contains("No modules"));
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                    Directory.Delete(projectRoot, recursive: true);
            }
        });

        await Task.WhenAll(tasks);
    }
}
