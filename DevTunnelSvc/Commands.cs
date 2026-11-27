using System.CommandLine;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Tunnels;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc;

internal readonly record struct LoginOptions(bool OpenBrowser, bool CopyToClipboard);
internal readonly record struct UpdateOptions(bool CheckOnly, bool Force, string? WorkerTag = null, int? AutoServicePid = null);

internal sealed record LocalActions(
    Func<TextWriter, LoginOptions, CancellationToken, Task<GitHubCredential>> Login,
    Func<UpdateOptions, CancellationToken, Task<string>> Update,
    Func<CancellationToken, Task<string>> Start,
    Func<CancellationToken, Task<string>> Install,
    Func<TextWriter, CancellationToken, Task> Run)
{
    public static LocalActions Default { get; } = new(Program.LoginAsync, Program.UpdateAsync,
        ServiceLifetime.StartAsync, ServiceLifetime.InstallAsync, ServiceLifetime.RunAsync);
}

internal sealed class Commands(TextReader input, TextWriter output, TextWriter error, Func<Request, CancellationToken, Task<string>> send, LocalActions localActions)
{
    public async Task<int> InvokeAsync(string[] args)
    {
        var root = new RootCommand("Control the per-user Dev Tunnels service.");
        foreach (var name in new[] { "list", "regions", "status", "logout" })
            root.Subcommands.Add(Action(name, (_, ct) => send(new(name), ct)));
        root.Subcommands.Add(Create());
        root.Subcommands.Add(Login());
        root.Subcommands.Add(WithName("unhost", "mapping"));
        root.Subcommands.Add(WithName("disconnect", "mapping"));
        root.Subcommands.Add(WithName("delete", "tunnel"));
        root.Subcommands.Add(Forward(true));
        root.Subcommands.Add(Forward(false));
        AddLifetimeCommands(root);
        root.Subcommands.Add(Update());
        root.Subcommands.Add(AutoUpdate());
        root.Subcommands.Add(AutoUpdateWorker());
#if WINDOWS
        root.Subcommands.Add(TrayUpdateCommand());
#endif
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

#if WINDOWS
    private static Command TrayUpdateCommand()
    {
        var pid = new Argument<int>("service-pid");
        var selected = new Argument<string?>("selected-version") { Arity = ArgumentArity.ZeroOrOne };
        var command = new Command(TrayMenu.UpdateWorker) { Hidden = true };
        var tag = WorkerTagOption();
        command.Arguments.Add(pid);
        command.Arguments.Add(selected);
        command.Options.Add(tag);
        command.SetAction((r, ct) => TrayUpdateUI.FromTrayAsync(r.GetRequiredValue(pid), r.GetValue(selected), r.GetValue(tag), ct));
        return command;
    }
#endif

    private Command AutoUpdate()
    {
        var command = new Command("auto-update", "Manage automatic updates from stable releases.");
        command.Subcommands.Add(Action("status", (_, ct) => send(new(AutoUpdateConsent.Status), ct)));
        command.Subcommands.Add(Action("enable", (_, ct) => AutoUpdateConsent.ChangeAsync(true, send,
            (question, token) => AutoUpdateConsent.ConfirmAsync(input, output, question, token), ct)));
        command.Subcommands.Add(Action("disable", (_, ct) => send(new(AutoUpdateConsent.Disable), ct)));
        return command;
    }

    private Command AutoUpdateWorker()
    {
        var pid = new Argument<int>("service-pid");
        var tag = WorkerTagOption();
        var command = Action(AutoUpdates.Worker, (r, ct) => localActions.Update(new(false, true, r.GetValue(tag), r.GetRequiredValue(pid)), ct));
        command.Hidden = true;
        command.Arguments.Add(pid);
        command.Options.Add(tag);
        return command;
    }

    private Command Update()
    {
        var check = new Option<bool>("--check") { Description = "Check for a newer release without installing it or stopping the service." };
        var force = new Option<bool>("--force") { Description = "Without asking, stop a running service and its mappings, install a newer release, and start the service, even if it was stopped." };
        var tag = WorkerTagOption();
        var command = Action("update", (r, ct) => localActions.Update(new(r.GetValue(check), r.GetValue(force), r.GetValue(tag)), ct));
        command.Options.Add(check);
        command.Options.Add(force);
        command.Options.Add(tag);
        return command;
    }

    private static Option<string?> WorkerTagOption() => new(UpdateWorker.TagOption) { Hidden = true };

    private Command Create()
    {
        var region = new Option<string>("--region") { Description = "Create the tunnel in this region instead of the recommended one. Run 'regions' to compare them." };
        var command = Action("create", (r, ct) => send(r.GetValue(region) is { } id ? new("create-in-region", Region: id) : new("create"), ct));
        command.Options.Add(region);
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

    private void AddLifetimeCommands(RootCommand root)
    {
        root.Subcommands.Add(Action("install", (_, ct) => localActions.Install(ct), "Register the per-user service for automatic startup."));
        root.Subcommands.Add(Action("start", (_, ct) => localActions.Start(ct), "Start the per-user service in the background."));
        root.Subcommands.Add(Action("stop", (_, ct) => send(new("shutdown"), ct), "Stop the service without forgetting its mappings or sign-in."));
        var run = new Command("run", "Run the service in the foreground.");
        run.SetAction((_, ct) => localActions.Run(error, ct));
        root.Subcommands.Add(run);
    }

    private Command Action(string name, Func<ParseResult, CancellationToken, Task<string>> handler, string? description = null)
    {
        var command = new Command(name, description);
        command.SetAction(async (r, ct) => output.WriteLine(await handler(r, ct)));
        return command;
    }
}
