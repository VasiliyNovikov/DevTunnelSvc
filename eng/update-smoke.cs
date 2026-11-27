#:project ..\DevTunnelSvc\DevTunnelSvc.csproj AdditionalProperties="GenerateRuntimeConfigurationFiles=false"
#:property AssemblyName=DevTunnelSvc.UpdateSmoke
#:property TargetFramework=net11.0-windows
#:property ApplicationIcon=..\DevTunnelSvc\Assets\devtunnelsvc.ico
#:property PublishAot=false
#:property PublishSingleFile=true
#:property PublishReadyToRun=true
#:property IncludeNativeLibrariesForSelfExtract=true

using System.Diagnostics;
using System.Text;
using DevTunnelSvc;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Updates;

// Only this fixture accepts a test state directory. Never use the production pipe, tray, or credentials.
var state = Environment.GetEnvironmentVariable("DEVTUNNELSVC_SMOKE_STATE") ?? throw new InvalidOperationException("Missing isolated state directory.");
var pipe = "DevTunnelSvc-smoke-" + Path.GetFileName(state);
var image = Environment.ProcessPath!;
using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
Task<string> Send(Request request, CancellationToken ct) => Control.SendAsync(request, pipe, ct);
if (args is ["smoke-pid"])
{
    try { Console.WriteLine(await Send(new("service-pid"), limit.Token)); return 0; }
    catch (IOException ex) when (ex.InnerException is TimeoutException) { return 1; }
}
if (args is ["service", "start"] && File.Exists(Path.Combine(state, "reject-start"))) return 37;

using var http = new HttpClient(new OfflineRelease(state));
var update = new CliUpdate(http, DevTunnelSvc.Program.Version, () =>
{
    DevTunnelSvc.Program.RequireSingleFileUpdate();
    return new(image, state, pipe, ServiceLifetime.ExitCodeAsync, UpdateWorker.ParentClosed);
});
var actions = new LocalActions((_, _, _) => throw new NotSupportedException(), update.RunAsync,
    ct => ServiceLifetime.StartAsync(true, t => Send(new("status"), t),
        t => ServiceLifetime.LaunchAsync(image, i => Process.Start(i)!.Dispose(), (_, _) => throw new NotSupportedException(), t), ct),
    _ => throw new NotSupportedException(), (log, ct) => RunService(log, ct));
return await DevTunnelSvc.Program.RunAsync(args, Console.In, Console.Out, Console.Error, Send, actions);

async Task RunService(TextWriter log, CancellationToken ct)
{
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, ct);
    // Exercise the packaged WinForms thread and embedded artwork, but don't add an icon to the user's taskbar.
    var background = new AutoUpdateActions(DevTunnelSvc.Program.RequireSingleFileUpdate,
        t => new ReleaseUpdate(http).FindNewerAsync(DevTunnelSvc.Program.Version, t), (pid, t) => AutoUpdateLauncher.LaunchAsync(image, pid, t), Task.Delay);
    await ServiceLifetime.RunAsync(log, pipe, state, background, lifetime.Token,
        static (name, writeEvent, enabled, stopped) => new TrayIcon(name, writeEvent, enabled, stopped, _ => { }));
}

internal sealed class OfflineRelease(string state) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpContent content = request.RequestUri!.Host switch
        {
            "api.github.com" => new StringContent(File.ReadAllText(Path.Combine(state, "release.json")), Encoding.UTF8, "application/json"),
            "github.com" => new StreamContent(File.OpenRead(Path.Combine(state, "asset.zip"))),
            _ => throw new InvalidOperationException("Unexpected release request."),
        };
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}
