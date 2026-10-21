// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Every build server launches the tray when it starts; only the first launch stays.
        using var single = new Mutex(initiallyOwned: true, @"Local\OmenTray", out var first);
        if (!first)
            return;

        ApplicationConfiguration.Initialize();
        using var tray = new TrayApp();
        Application.Run();
    }
}
