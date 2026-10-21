// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Omen.Core.Rules;

namespace Omen.Executors.Orchestration;

public sealed record TestRunResult(string Name, string Status, double Seconds, string Output, string Error, string? Reason = null);

public sealed class TestRunRequest
{
    public required string ProjectRoot { get; init; }
    public required IReadOnlyList<TestDefinition> Tests { get; init; }
    public string? NamePattern { get; init; }
    public string? ExcludePattern { get; init; }
    public string? LabelPattern { get; init; }
    public string? ExcludeLabelPattern { get; init; }
    public int Jobs { get; init; } = 1;
    public double? TimeoutSeconds { get; init; }
    public bool ListOnly { get; init; }
    public bool AllowNoTests { get; init; }
    public string? JunitPath { get; init; }
}

public sealed record TestRunSummary(IReadOnlyList<TestRunResult> Results, bool Success);

/// <summary>Runs registered tests with filtering, prerequisite ordering, bounded parallelism and JUnit output.</summary>
public sealed class TestOrchestrator
{
    public async Task<TestRunSummary> RunAsync(TestRunRequest request, IProgress<OrchestratorEvent>? events = null,
        CancellationToken ct = default)
    {
        if (request.Jobs <= 0) throw new ArgumentOutOfRangeException(nameof(request.Jobs));
        var all = request.Tests.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var name = Pattern(request.NamePattern);
        var exclude = Pattern(request.ExcludePattern);
        var label = Pattern(request.LabelPattern);
        var excludeLabel = Pattern(request.ExcludeLabelPattern);
        foreach (var test in all.Values)
        {
            if (string.IsNullOrWhiteSpace(test.Name) || string.IsNullOrWhiteSpace(test.Command))
                throw new InvalidOperationException("Tests require nonempty names and commands.");
            double timeout = request.TimeoutSeconds ?? test.TimeoutSeconds;
            if (!double.IsFinite(timeout) || timeout <= 0 || timeout > 86400)
                throw new InvalidOperationException($"Invalid timeout for test '{test.Name}'.");
            _ = Pattern(test.PassRegularExpression);
            _ = Pattern(test.FailRegularExpression);
        }
        var selected = new HashSet<string>(all.Values.Where(t =>
                (name == null || name.IsMatch(t.Name)) && (exclude == null || !exclude.IsMatch(t.Name)) &&
                (label == null || t.Labels.Any(l => label.IsMatch(l))) &&
                (excludeLabel == null || !t.Labels.Any(l => excludeLabel.IsMatch(l))))
            .Select(t => t.Name), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string key)
        {
            if (!all.TryGetValue(key, out var test)) throw new InvalidOperationException($"Unknown test prerequisite '{key}'.");
            if (visited.Contains(key)) return;
            if (!visiting.Add(key)) throw new InvalidOperationException($"Test dependency cycle at '{key}'.");
            foreach (var dependency in test.DependsOn) { selected.Add(dependency); Visit(dependency); }
            visiting.Remove(key);
            visited.Add(key);
        }
        foreach (var key in selected.ToArray()) Visit(key);
        if (request.ListOnly)
        {
            var listed = selected.Order(StringComparer.Ordinal).Select(n => new TestRunResult(n, "Listed", 0, "", "")).ToList();
            return new(listed, listed.Count > 0 || request.AllowNoTests);
        }
        var completed = new Dictionary<string, TestRunResult>(StringComparer.Ordinal);
        using var slots = new SemaphoreSlim(request.Jobs);
        while (completed.Count < selected.Count)
        {
            ct.ThrowIfCancellationRequested();
            var ready = selected.Where(n => !completed.ContainsKey(n) && all[n].DependsOn.All(completed.ContainsKey))
                .Order(StringComparer.Ordinal).ToList();
            foreach (var serial in ready.Where(n => all[n].RunSerial)) completed[serial] = await Run(all[serial]);
            var results = await Task.WhenAll(ready.Where(n => !all[n].RunSerial).Select(async key =>
            {
                await slots.WaitAsync(ct);
                try { return await Run(all[key]); }
                finally { slots.Release(); }
            }));
            foreach (var result in results) completed[result.Name] = result;
        }
        var ordered = completed.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        if (request.JunitPath != null) WriteJunit(request.JunitPath, ordered);
        return new(ordered, (ordered.Count > 0 || request.AllowNoTests) && ordered.All(r => r.Status != "Failed"));

        async Task<TestRunResult> Run(TestDefinition test)
        {
            string? skip = test.Disabled ? "Disabled" : test.DependsOn.FirstOrDefault(d => completed[d].Status != "Passed") is { } dependency
                ? $"Prerequisite '{dependency}' did not pass" : null;
            if (skip != null) return new(test.Name, "Skipped", 0, "", "", skip);
            events?.Report(new($"Running {test.Name}", OrchestratorEventLevel.Info));
            try
            {
                string directory = Path.GetFullPath(test.WorkingDirectory ?? request.ProjectRoot, request.ProjectRoot);
                string command = test.Command.Contains(Path.DirectorySeparatorChar) || test.Command.Contains(Path.AltDirectorySeparatorChar)
                    ? Path.GetFullPath(test.Command, directory) : test.Command;
                var result = await WorkflowProcess.RunAsync(command, test.Arguments, directory, test.Environment,
                    request.TimeoutSeconds ?? test.TimeoutSeconds, ct);
                string text = result.Output + "\n" + result.Error;
                bool skipped = !result.TimedOut && test.SkipExitCode == result.ExitCode;
                bool passed = !result.TimedOut && result.ExitCode == test.ExpectedExitCode &&
                    (test.PassRegularExpression == null || Pattern(test.PassRegularExpression)!.IsMatch(text)) &&
                    (test.FailRegularExpression == null || !Pattern(test.FailRegularExpression)!.IsMatch(text));
                string status = skipped ? "Skipped" : passed ? "Passed" : "Failed";
                string? reason = skipped ? $"Skip exit code {result.ExitCode}" : passed ? null : result.TimedOut
                    ? "Timed out" : $"Exit code {result.ExitCode}; expected {test.ExpectedExitCode}, or output did not match expectations";
                events?.Report(new($"{status}: {test.Name}", passed || skipped ? OrchestratorEventLevel.Info : OrchestratorEventLevel.Error));
                return new(test.Name, status, result.Duration.TotalSeconds, result.Output, result.Error, reason);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new(test.Name, "Failed", 0, "", "", ex.Message); }
        }
    }

    private static Regex? Pattern(string? pattern) => pattern == null ? null :
        new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));

    public static void WriteJunit(string path, IReadOnlyList<TestRunResult> results)
    {
        var suite = new XElement("testsuite", new XAttribute("name", "Omen"), new XAttribute("tests", results.Count),
            new XAttribute("failures", results.Count(r => r.Status == "Failed")),
            new XAttribute("skipped", results.Count(r => r.Status == "Skipped")),
            new XAttribute("time", results.Sum(r => r.Seconds).ToString("R", CultureInfo.InvariantCulture)));
        foreach (var result in results)
        {
            var test = new XElement("testcase", new XAttribute("name", Clean(result.Name)), new XAttribute("classname", "Omen"),
                new XAttribute("time", result.Seconds.ToString("R", CultureInfo.InvariantCulture)));
            if (result.Status == "Failed") test.Add(new XElement("failure", new XAttribute("message", Clean(result.Reason ?? "Failed"))));
            if (result.Status == "Skipped") test.Add(new XElement("skipped", new XAttribute("message", Clean(result.Reason ?? "Skipped"))));
            test.Add(new XElement("system-out", Clean(result.Output)), new XElement("system-err", Clean(result.Error)));
            suite.Add(test);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        new XDocument(new XElement("testsuites", suite)).Save(path);
    }

    private static string Clean(string text) => string.Concat(text.EnumerateRunes()
        .Where(r => r.Value > 0xFFFF || System.Xml.XmlConvert.IsXmlChar((char)r.Value)).Select(r => r.ToString()));
}
