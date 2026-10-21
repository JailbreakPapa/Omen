// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Diagnostics;

namespace Omen.GUI.Services;

/// <summary>
/// Opens a file or URL with whatever the OS has registered for it. Kept in one place so the
/// <c>UseShellExecute</c> flag and the failure handling are not repeated at each call site -
/// launching is allowed to fail (no handler registered, file moved) and must never take the app
/// down with it.
/// </summary>
public static class ExternalLauncher
{
    /// <summary>
    /// Attempts to open <paramref name="target"/>. Returns false rather than throwing when the
    /// shell has nothing to open it with, so callers can report it in the output pane.
    /// </summary>
    public static bool TryOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
