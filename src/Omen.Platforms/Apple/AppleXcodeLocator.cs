// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Platforms.Apple;

/// <summary>
/// Shared Xcode discovery helpers used by both the iOS (<see cref="AppleSDK"/>) and macOS
/// (<see cref="MacSDK"/>) SDK detectors - both target the same Xcode install, just different
/// platform SDKs within it.
/// </summary>
internal static class AppleXcodeLocator
{
    public static string? GetXcodePath()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xcode-select",
                Arguments = "-p",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // xcode-select returns /Applications/Xcode.app/Contents/Developer
                    return output;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        // Fallback to common path
        var defaultPath = "/Applications/Xcode.app/Contents/Developer";
        if (Directory.Exists(defaultPath))
            return defaultPath;

        return null;
    }

    /// <summary>
    /// Finds the newest SDK directory under a given Xcode platform's SDKs folder, e.g.
    /// "Platforms/MacOSX.platform/Developer/SDKs" -> ".../MacOSX14.0.sdk".
    /// </summary>
    public static string? GetSdkPath(string xcodePath, string platformDirName)
    {
        var sdksPath = Path.Combine(xcodePath, "Platforms", platformDirName, "Developer", "SDKs");
        if (!Directory.Exists(sdksPath))
            return null;

        return Directory.GetDirectories(sdksPath)
            .Where(d => d.EndsWith(".sdk"))
            .OrderByDescending(d => d)
            .FirstOrDefault();
    }

    public static string? GetXcodeVersion()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xcodebuild",
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                var output = process.StandardOutput.ReadLine();
                process.WaitForExit();

                if (output != null)
                {
                    // Parse "Xcode 15.0" -> "15.0"
                    var match = System.Text.RegularExpressions.Regex.Match(output, @"Xcode\s+(\d+\.\d+)");
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }
}
