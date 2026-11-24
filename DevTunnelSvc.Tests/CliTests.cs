using System.Reflection;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class CliTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("-h")]
    [DataRow("--help")]
    public async Task HelpPrintsUsage(string? option)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(option is null ? [] : [option], output, error);

        Assert.AreEqual(0, exitCode);
        Assert.Contains("Usage: devtunnelsvc", output.ToString());
        Assert.Contains("--version", output.ToString());
        Assert.IsEmpty(error.ToString());
    }

    [TestMethod]
    public async Task VersionPrintsTheEmbeddedReleaseVersion()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(["--version"], output, error);

        var expected = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(expected + Environment.NewLine, output.ToString());
        Assert.IsEmpty(error.ToString());
    }

    [TestMethod]
    public async Task UnknownArgumentFails()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(["--unknown"], output, error);

        Assert.AreNotEqual(0, exitCode);
        Assert.IsEmpty(output.ToString());
        Assert.Contains("Invalid arguments", error.ToString());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task LoginTransfersCredentialToServiceButNeverPrintsIt(bool noBrowser, bool noClipboard)
    {
        List<Request> requests = [];
        using var output = new StringWriter();
        using var error = new StringWriter();
        var saved = new DevTunnelSvc.Auth.GitHubCredential("secret", DateTimeOffset.UtcNow.AddHours(8), "private-refresh");
        var actions = LocalActions.Default with
        {
            Login = (writer, options, _) =>
            {
                Assert.AreEqual(!noBrowser, options.OpenBrowser);
                Assert.AreEqual(!noClipboard, options.CopyToClipboard);
                Assert.HasCount(1, requests);
                writer.WriteLine("visit GitHub");
                return Task.FromResult(saved);
            }
        };
        List<string> args = ["login"];
        if (noBrowser) args.Add("--no-browser");
        if (noClipboard) args.Add("--no-clipboard");
        var code = await Program.RunAsync([.. args], output, error, (r, _) =>
        {
            requests.Add(r);
            return Task.FromResult(r.Command == "login" ? "signed in" : "running");
        }, actions);
        Assert.AreEqual(0, code);
        Assert.HasCount(2, requests);
        Assert.AreEqual(new Request("status"), requests[0]);
        Assert.AreEqual(new Request("login", SignIn: saved), requests[1]);
        Assert.Contains("visit GitHub", output.ToString());
        Assert.DoesNotContain("secret", output.ToString());
        Assert.DoesNotContain("private-refresh", output.ToString());
        Assert.IsEmpty(error.ToString());
    }

    [TestMethod]
    public async Task PlatformCommandsAndCheckOnlyUpdateDispatchWithoutRemoteAccess()
    {
        List<string> called = [];
        Task<string> Record(string op) { called.Add(op); return Task.FromResult(op); }
        var actions = LocalActions.Default with
        {
            Start = _ => Record("start"),
            Install = _ => Record("install"),
            Run = (_, _) => Record("run"),
            Update = (check, _) => Record(check ? "check" : "apply")
        };
        string[][] commands = [["service", "install"], ["service", "start"], ["service", "run"], ["service", "stop"], ["update", "--check"], ["update"]];
        foreach (var command in commands)
            Assert.AreEqual(0, await Program.RunAsync(command, TextWriter.Null, TextWriter.Null, (r, _) => Record(r.Command), actions));
        Assert.AreEqual("install,start,run,shutdown,check,apply", string.Join(',', called));
        using var error = new StringWriter();
        Assert.AreNotEqual(0, await Program.RunAsync(["status"], TextWriter.Null, error, (_, _) => throw new OperationCanceledException()));
        Assert.Contains("cancelled", error.ToString());
    }

    [TestMethod]
    [DataRow("host", "--tunnel-port", "8080", "--target-port")]
    [DataRow("connect", "--listen-port", "5000", "--tunnel-port")]
    public async Task MissingPortOptionsAreRejectedBeforeContactingService(string command, string supplied, string value, string missing)
    {
        using var errors = new StringWriter();
        var result = await Program.RunAsync([command, "blue-tunnel.use2", supplied, value], TextWriter.Null, errors,
            (_, _) => throw new AssertFailedException());
        Assert.AreEqual(2, result);
        Assert.Contains(missing, errors.ToString());
    }

    [TestMethod]
    public async Task DeletePassesOnlyTheNamedTunnelToTheRunningService()
    {
        Request? received = null;
        using var output = new StringWriter();
        Assert.AreEqual(0, await Program.RunAsync(["delete", "blue-tunnel.use2"], output, TextWriter.Null, (r, _) =>
        { received = r; return Task.FromResult("deleted"); }));
        Assert.AreEqual(new Request("delete", "blue-tunnel.use2"), received);
        Assert.Contains("deleted", output.ToString());
    }
}
