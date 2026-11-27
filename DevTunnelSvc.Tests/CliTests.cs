using System.Reflection;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

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
        var (exitCode, output, error) = await Cli.CaptureAsync(option is null ? [] : [option]);

        Assert.AreEqual(0, exitCode);
        Assert.Contains("Usage: devtunnelsvc", output);
        Assert.Contains("--version", output);
        Assert.IsEmpty(error);
    }

    [TestMethod]
    public async Task VersionPrintsTheEmbeddedReleaseVersion()
    {
        var result = await Cli.CaptureAsync(["--version"]);

        var expected = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.AreEqual(0, result.Code);
        Assert.AreEqual(expected + Environment.NewLine, result.Output);
        Assert.IsEmpty(result.Error);
    }

    [TestMethod]
    public async Task UnknownArgumentFails()
    {
        var result = await Cli.CaptureAsync(["--unknown"]);
        Assert.AreNotEqual(0, result.Code);
        Assert.IsEmpty(result.Output);
        Assert.Contains("Invalid arguments", result.Error);
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
        var actions = Cli.NoLocalActions with
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
        var code = await Cli.RunAsync([.. args], output, error, (r, _) =>
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
        var actions = Cli.NoLocalActions with
        {
            Start = _ => Record("start"),
            Install = _ => Record("install"),
            Run = (_, _) => Record("run"),
            Update = (options, _) => Record($"{(options.CheckOnly ? "check" : "apply")}{(options.Force ? "-force" : "")}")
        };
        string[][] commands = [["service", "install"], ["service", "start"], ["service", "run"], ["service", "stop"],
            ["update", "--check"], ["update"], ["update", "--force"], ["update", "--force", "--check"]];
        foreach (var command in commands)
            Assert.AreEqual(0, await Cli.RunAsync(command, TextWriter.Null, TextWriter.Null, (r, _) => Record(r.Command), actions));
        Assert.AreEqual("install,start,run,shutdown,check,apply,apply-force,check-force", string.Join(',', called));
        using var error = new StringWriter();
        Assert.AreNotEqual(0, await Cli.RunAsync(["status"], TextWriter.Null, error, (_, _) => throw new OperationCanceledException()));
        Assert.Contains("cancelled", error.ToString());
    }

    [TestMethod]
    public async Task UpdateHelpDistinguishesReadOnlyChecksFromUnattendedRestarts()
    {
        var help = await Cli.CaptureAsync(["update", "--help"]);
        Assert.AreEqual(0, help.Code);
        Assert.Contains("without installing it or stopping the service", help.Output);
        Assert.Contains("Without asking, stop a running service and its mappings", help.Output);
        Assert.Contains("even if it was stopped", help.Output);
        Assert.IsEmpty(help.Error);
    }

    [TestMethod]
    [DataRow(" Yes ", true)]
    [DataRow("Y", true)]
    [DataRow("yes please", false)]
    [DataRow("", false)]
    public async Task AutoUpdateRequiresExplicitConsentBeforeEnabling(string answer, bool accepted)
    {
        List<string> requests = [];
        using var output = new StringWriter();
        using var input = new StringReader(answer);
        Assert.AreEqual(0, await Cli.RunAsync(["auto-update", "enable"], output, TextWriter.Null, (r, _) =>
        {
            requests.Add(r.Command);
            return Task.FromResult(r.Command == "auto-update-status" ? AutoUpdateConsent.Disabled : AutoUpdateConsent.Enabled);
        }, input: input));
        Assert.Contains("Active tunnel connections will be interrupted", output.ToString());
        Assert.Contains("Saved port mappings will be restored", output.ToString());
        Assert.Contains("[y/N]", output.ToString());
        Assert.Contains(accepted ? "enabled." : "not enabled.", output.ToString());
        Assert.AreEqual(accepted ? "auto-update-status,auto-update-enable" : "auto-update-status", string.Join(',', requests));
    }

    [TestMethod]
    [DataRow("enable", "auto-update-status")]
    [DataRow("disable", "auto-update-disable")]
    [DataRow("status", "auto-update-status")]
    public async Task ExistingOptInStatusAndOptOutDoNotPrompt(string verb, string expectedRequest)
    {
        using var output = new StringWriter();
        var result = verb == "disable" ? AutoUpdateConsent.Disabled : AutoUpdateConsent.Enabled;
        Assert.AreEqual(0, await Cli.RunAsync(["auto-update", verb], output, TextWriter.Null, (r, _) =>
        {
            Assert.AreEqual(new Request(expectedRequest), r);
            return Task.FromResult(result);
        }));
        Assert.AreEqual(result + Environment.NewLine, output.ToString());
    }

    [TestMethod]
    [DataRow("host", "--tunnel-port", "8080", "--target-port")]
    [DataRow("connect", "--listen-port", "5000", "--tunnel-port")]
    public async Task MissingPortOptionsAreRejectedBeforeContactingService(string command, string supplied, string value, string missing)
    {
        using var errors = new StringWriter();
        var result = await Cli.RunAsync([command, "blue-tunnel.use2", supplied, value], TextWriter.Null, errors,
            (_, _) => throw new AssertFailedException());
        Assert.AreEqual(2, result);
        Assert.Contains(missing, errors.ToString());
    }

    [TestMethod]
    public async Task DeletePassesOnlyTheNamedTunnelToTheRunningService()
    {
        Request? received = null;
        using var output = new StringWriter();
        Assert.AreEqual(0, await Cli.RunAsync(["delete", "blue-tunnel.use2"], output, TextWriter.Null, (r, _) =>
        { received = r; return Task.FromResult("deleted"); }));
        Assert.AreEqual(new Request("delete", "blue-tunnel.use2"), received);
        Assert.Contains("deleted", output.ToString());
    }

    [TestMethod]
    public async Task CreateKeepsTheOriginalCommandUnlessARegionIsRequested()
    {
        List<Request> requests = [];
        Task<string> Record(Request request, CancellationToken _) { requests.Add(request); return Task.FromResult("created"); }
        Assert.AreEqual(0, await Cli.RunAsync(["create"], TextWriter.Null, TextWriter.Null, Record));
        Assert.AreEqual(0, await Cli.RunAsync(["create", "--region", "euw"], TextWriter.Null, TextWriter.Null, Record));
        // An older service rejects the region-aware command instead of creating the tunnel in its default region.
        CollectionAssert.AreEqual(new Request[] { new("create"), new("create-in-region", Region: "euw") }, requests);
        using var help = new StringWriter();
        Assert.AreEqual(0, await Cli.RunAsync(["create", "--help"], help, TextWriter.Null, Record));
        Assert.Contains("instead of the recommended one", help.ToString());
        Assert.HasCount(2, requests);
    }
}

// Tests reach the CLI only through these defaults, which never touch the developer's real service or desktop.
internal static class Cli
{
    internal static readonly AutoUpdateActions NoBackgroundUpdates = new(() => throw new AssertFailedException("Unexpected auto-update opt-in."),
        _ => throw new AssertFailedException("Unexpected release poll."), (_, _) => throw new AssertFailedException("Unexpected updater."), Task.Delay);
    internal static readonly LocalActions NoLocalActions = new(
        (_, _, _) => throw new AssertFailedException("Unexpected login."),
        (_, _) => throw new AssertFailedException("Unexpected update."),
        _ => throw new AssertFailedException("Unexpected service start."),
        _ => throw new AssertFailedException("Unexpected service install."),
        (_, _) => throw new AssertFailedException("Unexpected service run."));

    internal static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<Request, CancellationToken, Task<string>>? send = null, LocalActions? actions = null, TextReader? input = null) =>
        Program.RunAsync(args, input ?? TextReader.Null, output, error, send ?? ((_, _) => throw new AssertFailedException("Unexpected service request.")),
            actions ?? NoLocalActions);

    internal static async Task<(int Code, string Output, string Error)> CaptureAsync(string[] args, Func<Request, CancellationToken, Task<string>>? send = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await RunAsync(args, output, error, send);
        return (code, output.ToString(), error.ToString());
    }
}
