using System.CommandLine;
using System.Runtime.Versioning;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc;

internal readonly record struct LoginOptions(bool OpenBrowser, bool CopyToClipboard);

internal sealed record LocalActions(
    Func<TextWriter, LoginOptions, CancellationToken, Task<GitHubCredential>> Login,
    Func<bool, CancellationToken, Task<string>> Update,
    Func<CancellationToken, Task<string>> Start,
    Func<CancellationToken, Task<string>> Install,
    Func<TextWriter, CancellationToken, Task> Run)
{
    public static LocalActions Default { get; } = new(Program.LoginAsync, Program.UpdateAsync,
        ServiceLifetime.StartAsync, ServiceLifetime.InstallAsync, ServiceLifetime.RunAsync);
}

internal sealed class Commands(TextWriter output, TextWriter error, Func<Request, CancellationToken, Task<string>> send, LocalActions localActions)
{
    public async Task<int> InvokeAsync(string[] args)
    {
        var root = new RootCommand("Control the per-user Dev Tunnels service.");
        foreach (var name in new[] { "create", "status", "logout" })
            root.Subcommands.Add(Action(name, (_, ct) => send(new(name), ct)));
        root.Subcommands.Add(Login());
        root.Subcommands.Add(WithName("stop", "mapping"));
        root.Subcommands.Add(WithName("delete", "tunnel"));
        root.Subcommands.Add(Forward(true));
        root.Subcommands.Add(Forward(false));
        root.Subcommands.Add(Service());
        var check = new Option<bool>("--check");
        var update = Action("update", (r, ct) => localActions.Update(r.GetValue(check), ct));
        update.Options.Add(check);
        root.Subcommands.Add(update);
        if (OperatingSystem.IsWindows()) root.Subcommands.Add(TrayUpdateCommand());
        var parsed = root.Parse(args);
        if (parsed.Errors.Count != 0)
        {
            error.WriteLine("Invalid arguments. " + string.Join(" ", parsed.Errors.Select(e => e.Message)));
            return 2;
        }
        return await parsed.InvokeAsync(new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = TimeSpan.FromSeconds(30)
        });
    }

    [SupportedOSPlatform("windows")]
    private static Command TrayUpdateCommand()
    {
        var pid = new Argument<int>("service-pid");
        var command = new Command(TrayMenu.UpdateWorker) { Hidden = true };
        command.Arguments.Add(pid);
        command.SetAction((r, ct) => TrayUpdate.FromMenuAsync(r.GetRequiredValue(pid), ct));
        return command;
    }

    private Command Login()
    {
        var noBrowser = new Option<bool>("--no-browser") { Description = "Print the GitHub URL and one-time code without opening a browser." };
        var noClipboard = new Option<bool>("--no-clipboard") { Description = "Leave the clipboard unchanged during sign-in." };
        var command = Action("login", async (r, ct) =>
        {
            await send(new("status"), ct);
            var options = new LoginOptions(OpenBrowser: !r.GetValue(noBrowser), CopyToClipboard: !r.GetValue(noClipboard));
            var signIn = await localActions.Login(output, options, ct);
            return await send(new("login", SignIn: signIn), ct);
        });
        command.Options.Add(noBrowser);
        command.Options.Add(noClipboard);
        return command;
    }

    private Command WithName(string command, string argument)
    {
        var name = new Argument<string>(argument);
        var result = Action(command, (r, ct) => send(new(command, r.GetRequiredValue(name)), ct));
        result.Arguments.Add(name);
        return result;
    }

    private Command Forward(bool host)
    {
        var name = host ? "host" : "connect";
        var tunnel = new Argument<string>("tunnel");
        var remote = new Option<int>("--tunnel-port") { Required = true };
        var local = new Option<int>(host ? "--target-port" : "--listen-port") { Required = true };
        var command = Action(name, (r, ct) =>
        {
            var map = Mapping.Create(host, r.GetRequiredValue(tunnel), r.GetValue(remote), r.GetValue(local));
            return send(new(name, map.Tunnel.ToString(), map.TunnelPort, map.LocalPort), ct);
        });
        command.Arguments.Add(tunnel);
        command.Options.Add(remote);
        command.Options.Add(local);
        return command;
    }

    private Command Service()
    {
        var service = new Command("service");
        service.Subcommands.Add(Action("start", (_, ct) => localActions.Start(ct)));
        service.Subcommands.Add(Action("install", (_, ct) => localActions.Install(ct)));
        service.Subcommands.Add(Action("stop", (_, ct) => send(new("shutdown"), ct)));
        var run = new Command("run");
        run.SetAction((_, ct) => localActions.Run(error, ct));
        service.Subcommands.Add(run);
        return service;
    }

    private Command Action(string name, Func<ParseResult, CancellationToken, Task<string>> handler)
    {
        var command = new Command(name);
        command.SetAction(async (r, ct) => output.WriteLine(await handler(r, ct)));
        return command;
    }
}
