// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Graph;

namespace Omen.Executors.Tests;

public class RunProcessCancelTests
{
    [Fact(Timeout = 60_000)]
    public async Task Cancel_KillsTheCommandBeforeItCanWriteItsOutput()
    {
        // The command (a grandchild of cmd.exe) writes its output only after sleeping; a cancelled action whose
        // process keeps running would write it anyway - the orphan the build server must never leave behind.
        var output = Path.Combine(Path.GetTempPath(), $"omen-cancel-{Guid.NewGuid():N}.out");
        var action = new BuildAction
        {
            Id = "slow", Type = ActionType.Compile, Description = "slow", WorkingDirectory = Path.GetTempPath(),
            CommandLine = $"powershell -NoProfile -Command Start-Sleep 6; Set-Content '{output}' late",
            Outputs = [new FileItem { Path = output }],
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await FluentActions.Awaiting(() => ParallelExecutor.RunProcessAsync(action, null, cancel.Token)).Should().ThrowAsync<OperationCanceledException>();

        await Task.Delay(TimeSpan.FromSeconds(8));
        File.Exists(output).Should().BeFalse("the cancelled command was killed, so it never wrote its output");
        File.Delete(output);
    }
}
