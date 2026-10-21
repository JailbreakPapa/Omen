// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Omen.Executors;

/// <summary>
/// A Windows job object holding one action's process and everything it starts, so a cancel reaches descendants
/// created at any moment - <see cref="Process.Kill(bool)"/>'s tree snapshot misses cmd.exe starting the compiler while
/// the snapshot is taken. The job is not kill-on-close and <see cref="KillAll"/> spares mspdbsrv.exe: with /Zi /FS the
/// first cl.exe starts the PDB server every concurrent compile shares, and it may live in this job. Elsewhere this is
/// unavailable and callers fall back to the tree kill.
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    private readonly IntPtr _handle;

    private ProcessJob(IntPtr handle) => _handle = handle;

    /// <summary>A job containing <paramref name="process"/>, or null when job objects are unavailable.</summary>
    public static ProcessJob? For(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            return null;
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            CloseHandle(handle);
            return null;
        }
        return new ProcessJob(handle);
    }

    /// <summary>Kills every process in the job except the shared PDB server, until none is left.</summary>
    public void KillAll()
    {
        for (var round = 0; round < 20; round++)
        {
            var victims = ProcessIds()
                .Select(id => { try { return Process.GetProcessById(id); } catch (ArgumentException) { return null; } })
                .Where(p => p != null && !p.ProcessName.Equals("mspdbsrv", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (victims.Count == 0)
                return;
            foreach (var victim in victims)
            {
                try { victim!.Kill(); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                victim!.Dispose();
            }
            Thread.Sleep(50);
        }
    }

    private int[] ProcessIds()
    {
        const int capacity = 1024;
        var size = 8 + IntPtr.Size * capacity;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_handle, JobObjectBasicProcessIdList, buffer, (uint)size, IntPtr.Zero))
                return [];
            var count = Marshal.ReadInt32(buffer, 4);
            var ids = new int[count];
            for (var i = 0; i < count; i++)
                ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => CloseHandle(_handle);

    private const int JobObjectBasicProcessIdList = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
