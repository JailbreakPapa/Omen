// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Text;
using System.Text.RegularExpressions;
using Omen.Core.Rules;

namespace Omen.Core.Generators;

/// <summary>Configuration-header substitution with write-if-changed semantics and CMake template compatibility.</summary>
public static class ConfigurationFileGenerator
{
    public static string Render(string template, IReadOnlyDictionary<string, string> variables, bool atOnly = false, bool strict = false,
        ConfiguredNewlineStyle newlineStyle = ConfiguredNewlineStyle.Preserve, bool escapeQuotes = false)
    {
        string Value(string key) => variables.TryGetValue(key, out var value) ? value : strict
            ? throw new InvalidOperationException($"Configuration template variable '{key}' is not defined.") : "";
        bool Enabled(string key)
        {
            string value = variables.TryGetValue(key, out var found) ? found : "";
            return value.Length > 0 && !new[] { "0", "OFF", "NO", "FALSE", "N", "IGNORE", "NOTFOUND" }
                .Contains(value, StringComparer.OrdinalIgnoreCase) && !value.EndsWith("-NOTFOUND", StringComparison.OrdinalIgnoreCase);
        }
        template = Regex.Replace(template, @"(?m)^([ \t]*)#(?:cmake|omen)define(01)?[ \t]+([A-Za-z_][A-Za-z0-9_]*)([^\r\n]*)", match =>
        {
            string key = match.Groups[3].Value;
            if (match.Groups[2].Success) return match.Groups[1].Value + "#define " + key + (Enabled(key) ? " 1" : " 0");
            return match.Groups[1].Value + (Enabled(key) ? "#define " + key + match.Groups[4].Value : "/* #undef " + key + " */");
        });
        // One replacement pass: substituted values are literals, not another template language.
        string pattern = atOnly ? @"@([A-Za-z_][A-Za-z0-9_]*)@" : @"@([A-Za-z_][A-Za-z0-9_]*)@|\$\{([A-Za-z_][A-Za-z0-9_]*)\}";
        string rendered = Regex.Replace(template, pattern, m =>
        {
            string value = Value(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            return escapeQuotes ? value.Replace("\"", "\\\"") : value;
        });
        return newlineStyle switch
        {
            ConfiguredNewlineStyle.LF => rendered.ReplaceLineEndings("\n"),
            ConfiguredNewlineStyle.CRLF => rendered.ReplaceLineEndings("\r\n"),
            _ => rendered
        };
    }

    public static bool Generate(string input, string output, IReadOnlyDictionary<string, string> variables, bool atOnly = false, bool strict = false,
        ConfiguredNewlineStyle newlineStyle = ConfiguredNewlineStyle.Preserve, bool escapeQuotes = false, bool copyOnly = false)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Configuration template input and output must be different files.");
        if (copyOnly)
        {
            if (newlineStyle != ConfiguredNewlineStyle.Preserve) throw new InvalidOperationException("CopyOnly cannot normalize newlines.");
            byte[] bytes = File.ReadAllBytes(input);
            if (File.Exists(output) && File.ReadAllBytes(output).AsSpan().SequenceEqual(bytes)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            if (File.Exists(output)) new FileInfo(output).IsReadOnly = false;
            File.WriteAllBytes(output, bytes);
            return true;
        }
        string contents = Render(File.ReadAllText(input), variables, atOnly, strict, newlineStyle, escapeQuotes);
        if (File.Exists(output) && File.ReadAllText(output) == contents) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        GeneratedFile.WriteAllText(output, contents);
        return true;
    }
}
