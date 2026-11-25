using System.Reflection;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc;

internal static class Program
{
    private const string Usage = """
        Usage: devtunnelsvc <command> [options]
          service install|start|run|stop   Manage the per-user background service
          login [--no-browser] [--no-clipboard]  Sign in with GitHub
          logout                          Stop mappings and remove the saved GitHub sign-in
          create [--region <region>]      Create a private tunnel in the recommended or chosen region
          delete <tunnel>                 Delete an owned tunnel
          list                            Show your tunnels' host and client counts, expiration, and ports
          regions                         Compare regions by latency from this machine and recommendation
          host <tunnel> --tunnel-port N --target-port N
          connect <tunnel> --tunnel-port N --listen-port N
          status | stop <mapping>         Inspect or stop a forwarding session
          update [--check] [--force]      Check or install a newer release; --force also stops and starts the service without asking
          --version                       Print the embedded release version
        """;

    internal static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private static readonly HttpClient Http = GitHubOAuth.CreateClient();

    // Only the entry point wires the real service pipe and local actions. Shared code has no fallback to them,
    // so neither a test nor a mutant of shared code can reach a developer's running service or credentials.
    private static Task<int> Main(string[] args) => RunAsync(args, Console.Out, Console.Error,
        (request, ct) => Control.SendAsync(request, Control.PipeName, ct), LocalActions.Default);

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<Request, CancellationToken, Task<string>> send, LocalActions actions)
    {
        if (args is [] or ["-h"] or ["--help"])
        {
            output.WriteLine(Usage);
            return 0;
        }

        if (args is ["--version"])
        {
            output.WriteLine(Version);
            return 0;
        }

        try { return await new Commands(output, error, send, actions).InvokeAsync(args); }
        catch (OperationCanceledException) { error.WriteLine("Operation cancelled or timed out."); return 1; }
        catch (Exception ex) { error.WriteLine(ex.Message); return 1; }
    }

    internal static Task<GitHubCredential> LoginAsync(TextWriter output, LoginOptions options, CancellationToken ct) =>
        new DeviceLogin(Http, Task.Delay, options.OpenBrowser ? LoginBrowser.OpenAsync : null,
            options.CopyToClipboard ? LoginClipboard.TryCopyAsync : null).SignInAsync(output, ct);

    internal static Task<string> UpdateAsync(UpdateOptions options, CancellationToken ct)
    {
        var executable = options.CheckOnly ? string.Empty : ServiceLifetime.ApplicationExecutable();
        var handoff = options.Force ? new UpdateHandoff((request, t) =>
            { RequireSingleFileUpdate(); return Control.SendAsync(request, Control.PipeName, t); },
            () => UpdateHandoff.StartInstalledAsync(executable, ServiceLifetime.ExitCodeAsync)) : null;
        return new ReleaseUpdate(Http, handoff).RunAsync(Version, executable, options.CheckOnly, GetUpdateLease, ct);
    }

    private static IDisposable GetUpdateLease()
    {
        RequireSingleFileUpdate();
        return InstanceLease.Acquire(InstanceLease.DefaultDirectory);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty Location identifies the supported single-file installation.")]
    internal static void RequireSingleFileUpdate()
    {
        if (typeof(Program).Assembly.Location.Length != 0)
            throw new InvalidOperationException("Only a published single-file executable can self-update.");
    }
}
