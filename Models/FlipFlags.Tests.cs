using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Models;

public class FlipFlagsClientGenerationTests
{
    private const string PackageName = "Coflnet.Sky.FlipTracker.Client";
    private string workDir;

    [SetUp]
    public void CreateWorkDir()
    {
        workDir = Path.Combine(Path.GetTempPath(), "flipflags-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
    }

    [TearDown]
    public void RemoveWorkDir()
    {
        Directory.Delete(workDir, true);
    }

    /// <summary>
    /// Runs Client/generate.sh over the enum the generator would emit for the current <see cref="FlipFlags"/>
    /// </summary>
    [Test]
    public void GeneratedClientDeclaresEveryFlagWithItsServiceValue()
    {
        var generatedEnum = Path.Combine(workDir, "out", "src", PackageName, "Model", "FlipFlags.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(generatedEnum));
        File.WriteAllText(generatedEnum, GeneratorOutput());
        File.WriteAllText(Path.Combine(workDir, "out", "src", PackageName, PackageName + ".csproj"), "<Project />");

        var (exitCode, errors) = RunGenerateScript();

        exitCode.Should().Be(0, errors);
        var generated = File.ReadAllText(generatedEnum);
        var members = Regex.Matches(generated, @"^\s+(\w+) = (\d+),?\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
        members.Should().Equal(Enum.GetValues<FlipFlags>().ToDictionary(f => f.ToString(), f => (int)f));
        generated.Should().MatchRegex(@"\[Flags\]\s+public enum FlipFlags");
    }

    /// <summary>
    /// What openapi-generator's csharp generator emits for the string enum of the OpenAPI document:
    /// members in declaration order, numbered from 1
    /// </summary>
    private static string GeneratorOutput()
    {
        var members = Enum.GetNames<FlipFlags>().Select((name, index) => $"""
                    /// <summary>
                    /// Enum {name} for value: {name}
                    /// </summary>
                    [EnumMember(Value = "{name}")]
                    {name} = {index + 1}
            """);
        return $$"""
            using System;
            using System.Runtime.Serialization;
            using Newtonsoft.Json;
            using Newtonsoft.Json.Converters;

            namespace {{PackageName}}.Model
            {
                /// <summary>
                /// Defines FlipFlags
                /// </summary>
                [JsonConverter(typeof(StringEnumConverter))]
                public enum FlipFlags
                {
            {{string.Join(",\n\n", members)}}
                }

            }

            """;
    }

    /// <summary>
    /// docker and dotnet are replaced with no-op shell functions so only the script's own post-processing runs,
    /// nothing is generated, packed or pushed. Functions win over PATH and need no executable stub file.
    /// </summary>
    private (int exitCode, string errors) RunGenerateScript()
    {
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("docker() { :; }; dotnet() { :; }; . \"$0\"");
        start.ArgumentList.Add(GenerateScriptPath());
        start.Environment["NUGET_API_KEY"] = "";
        using var process = Process.Start(start);
        process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, errors);
    }

    private static string GenerateScriptPath()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Client", "generate.sh")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Client/generate.sh not found above the test directory");
        return Path.Combine(dir.FullName, "Client", "generate.sh");
    }
}
