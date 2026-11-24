using System.Reflection;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class CliTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("-h")]
    [DataRow("--help")]
    public void HelpPrintsUsage(string? option)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Run(option is null ? [] : [option], output, error);

        Assert.AreEqual(0, exitCode);
        Assert.Contains("Usage: DevTunnelSvc", output.ToString());
        Assert.Contains("--version", output.ToString());
        Assert.IsEmpty(error.ToString());
    }

    [TestMethod]
    public void VersionPrintsTheEmbeddedReleaseVersion()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Run(["--version"], output, error);

        var expected = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(expected + Environment.NewLine, output.ToString());
        Assert.IsEmpty(error.ToString());
    }

    [TestMethod]
    public void UnknownArgumentFails()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Run(["--unknown"], output, error);

        Assert.AreNotEqual(0, exitCode);
        Assert.IsEmpty(output.ToString());
        Assert.Contains("Invalid arguments", error.ToString());
    }
}
