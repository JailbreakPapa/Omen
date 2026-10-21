// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Tests;

public sealed class ConfigurationFileGeneratorTests
{
    [Fact]
    public void RendersDefinesValuesAndAtOnlyWithoutRecursivelyExpandingValues()
    {
        var variables = new Dictionary<string,string> { ["ON"] = "true", ["OFF"] = "OFF", ["TEXT"] = "${literal}", ["VERSION"] = "2.5" };
        string input = "#cmakedefine ON @VERSION@\n#omendefine OFF\n#cmakedefine01 ON\n#omendefine01 OFF\n@TEXT@ ${VERSION}";
        ConfigurationFileGenerator.Render(input, variables).Should().Be("#define ON 2.5\n/* #undef OFF */\n#define ON 1\n#define OFF 0\n${literal} 2.5");
        ConfigurationFileGenerator.Render("@VERSION@ ${VERSION}", variables, atOnly: true).Should().Be("2.5 ${VERSION}");
    }

    [Fact]
    public void NewlineAndQuoteOptionsAreAppliedToValues()
    {
        ConfigurationFileGenerator.Render("@VALUE@\r\nnext\n", new Dictionary<string,string> { ["VALUE"] = "say \"hello\"" },
            newlineStyle: ConfiguredNewlineStyle.LF, escapeQuotes: true).Should().Be("say \\\"hello\\\"\nnext\n");
        ConfigurationFileGenerator.Render("a\nb\r\n", new Dictionary<string,string>(), newlineStyle: ConfiguredNewlineStyle.CRLF)
            .Should().Be("a\r\nb\r\n");
    }

    [Fact]
    public void CopyOnlyPreservesBinaryAndSamePathIsRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OmenConfigurationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input"), output = Path.Combine(directory, "output");
            byte[] bytes = [0, 255, 13, 10, 64]; File.WriteAllBytes(input, bytes);
            ConfigurationFileGenerator.Generate(input, output, new Dictionary<string,string>(), copyOnly: true).Should().BeTrue();
            File.ReadAllBytes(output).Should().Equal(bytes);
            ConfigurationFileGenerator.Generate(input, output, new Dictionary<string,string>(), copyOnly: true).Should().BeFalse();
            FluentActions.Invoking(() => ConfigurationFileGenerator.Generate(input, input, new Dictionary<string,string>()))
                .Should().Throw<InvalidOperationException>().WithMessage("*different*");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void StrictModeRejectsUnknownVariablesAndDefaultModeUsesEmptyValue()
    {
        ConfigurationFileGenerator.Render("@UNKNOWN@", new Dictionary<string,string>()).Should().BeEmpty();
        FluentActions.Invoking(() => ConfigurationFileGenerator.Render("@UNKNOWN@", new Dictionary<string,string>(), strict: true))
            .Should().Throw<InvalidOperationException>().WithMessage("*UNKNOWN*");
    }

    [Fact]
    public void UnchangedOutputKeepsTimestampAndChangedOutputCanReplaceReadOnlyFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OmenConfigurationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "config.h.in"), output = Path.Combine(directory, "config.h");
            File.WriteAllText(input, "#define VALUE @VALUE@\n");
            ConfigurationFileGenerator.Generate(input, output, new Dictionary<string,string> { ["VALUE"] = "1" }).Should().BeTrue();
            File.SetLastWriteTimeUtc(output, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            ConfigurationFileGenerator.Generate(input, output, new Dictionary<string,string> { ["VALUE"] = "1" }).Should().BeFalse();
            File.GetLastWriteTimeUtc(output).Year.Should().Be(2001);
            File.SetAttributes(output, FileAttributes.ReadOnly);
            ConfigurationFileGenerator.Generate(input, output, new Dictionary<string,string> { ["VALUE"] = "2" }).Should().BeTrue();
            File.ReadAllText(output).Should().Contain("VALUE 2");
        }
        finally { Directory.Delete(directory, true); }
    }
}
