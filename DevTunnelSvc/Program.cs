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
          install                         Register the per-user service for automatic startup
          start                           Start the service in the background
          run                             Run the service in the foreground
          stop                            Stop the service and keep saved mappings and sign-in
          login [--no-browser] [--no-clipboard]  Sign in with GitHub
          logout                          Forget all mappings and remove the saved GitHub sign-in
          create [--region <region>]      Create a private tunnel in the recommended or chosen region
          delete <tunnel>                 Delete an owned tunnel
          list                            Show your tunnels' host and client counts, expiration, and ports
          regions                         Compare regions by latency from this machine and recommendation
          host <tunnel> --tunnel-port N --target-port N
          connect <tunnel> --tunnel-port N --listen-port N
          status                          Show active and restoring mappings
          unhost <mapping>                Stop hosting and forget a host mapping
          disconnect <mapping>            Close and forget a client connection
          update [--check] [--force]      Check or install a newer release; --force also stops and starts the service without asking
          auto-update status|enable|disable  Manage unattended stable updates for this user
          --version                       Print the embedded release version
        """;

    internal static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private static readonly HttpClient Http = GitHubOAuth.CreateClient();

    // Only the entry point wires the real service pipe and local actions. Shared code has no fallback to them,
    // so neither a test nor a mutant of shared code can reach a developer's running service or credentials.
    private static Task<int> Main(string[] args) => RunAsync(args, Console.In, Console.Out, Console.Error,
        (request, ct) => Control.SendAsync(request, Control.PipeName, ct), LocalActions.Default);

    internal static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error,
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

        if (args is ["service", ..])
        {
            error.WriteLine("Invalid arguments. The 'service' prefix was removed. Use 'devtunnelsvc install', 'start', 'run', or 'stop' directly.");
            return 2;
        }

        try { return await new Commands(input, output, error, send, actions).InvokeAsync(args); }
        catch (OperationCanceledException) { error.WriteLine("Operation cancelled or timed out."); return 1; }
        catch (Exception ex) { error.WriteLine(AppLog.Detail(ex)); return 1; }
    }

    internal static Task<GitHubCredential> LoginAsync(TextWriter output, LoginOptions options, CancellationToken ct) =>
        new DeviceLogin(Http, Task.Delay, options.OpenBrowser ? LoginBrowser.OpenAsync : null,
            options.CopyToClipboard ? LoginClipboard.TryCopyAsync : null).SignInAsync(output, ct);

    internal static Task<string> UpdateAsync(UpdateOptions options, CancellationToken ct) =>
        new CliUpdate(Http, Version, GetUpdateInstallation).RunAsync(options, ct);

    internal static UpdateInstallation GetUpdateInstallation()
    {
        RequireSingleFileUpdate();
        return new(ServiceLifetime.ApplicationExecutable(), InstanceLease.DefaultDirectory, Control.PipeName, ServiceLifetime.ExitCodeAsync, UpdateWorker.ParentClosed);
    }

    internal static AutoUpdateActions BackgroundUpdates { get; } = new(RequireSingleFileUpdate,
        ct => new ReleaseUpdate(Http).FindNewerAsync(Version, ct),
        (pid, ct) => AutoUpdateLauncher.LaunchAsync(ServiceLifetime.ApplicationExecutable(), pid, ct), Task.Delay);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty Location identifies the supported single-file installation.")]
    internal static void RequireSingleFileUpdate()
    {
        if (typeof(Program).Assembly.Location.Length != 0)
            throw new InvalidOperationException("Only a published single-file executable can self-update.");
    }
}
