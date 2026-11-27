using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Platform;
using DevTunnelSvc.Service;
using DevTunnelSvc.Tunnels;
using DevTunnelSvc.Updates;

namespace DevTunnelSvc.Tests;

internal sealed class HttpStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpClientHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

[TestClass]
public sealed class HttpTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LoginDisplaysOnlyTheUserCodeAndHonorsPollingBackoff(bool clipboardAvailable)
    {
        Queue<string> replies = new(["""{"device_code":"private-code","user_code":"ABCD-1234","verification_uri":"https://untrusted.invalid/","interval":2,"expires_in":900}""",
            """{"error":"authorization_pending"}""", """{"error":"slow_down"}""",
            """{"access_token":"private-token","expires_in":28800,"refresh_token":"private-refresh","refresh_token_expires_in":15897600}"""]);
        using var http = new HttpClient(new HttpStub(async (r, ct) =>
        {
            Assert.AreEqual(HttpMethod.Post, r.Method);
            Assert.AreEqual("github.com", r.RequestUri!.Host);
            Assert.AreEqual("application/json", r.Headers.Accept.Single().MediaType);
            var body = await r.Content!.ReadAsStringAsync(ct);
            var form = System.Web.HttpUtility.ParseQueryString(body);
            Assert.AreEqual(Microsoft.DevTunnels.Contracts.TunnelServiceProperties.Production.GitHubAppClientId, form["client_id"]);
            if (r.RequestUri.AbsolutePath.EndsWith("/access_token", StringComparison.Ordinal))
            {
                Assert.AreEqual("private-code", form["device_code"]);
                Assert.AreEqual("urn:ietf:params:oauth:grant-type:device_code", form["grant_type"]);
            }
            else
            {
                Assert.AreEqual("/login/device/code", r.RequestUri.AbsolutePath);
                Assert.AreEqual("offline_access", form["scope"]);
            }
            return HttpStub.Json(replies.Dequeue());
        }));
        List<double> delays = [];
        using var output = new StringWriter();
        CancellationToken browserWait = default;
        List<string> clipboardWrites = [];
        var oauth = new DeviceLogin(http, (d, _) => { delays.Add(d.TotalSeconds); return Task.CompletedTask; }, (page, ct) =>
        {
            Assert.AreEqual("ABCD-1234", clipboardWrites.Single(), "Only the user code should reach the clipboard, before browser launch.");
            Assert.Contains("ABCD-1234", output.ToString());
            Assert.Contains(clipboardAvailable ? "Code copied to the clipboard" : "Clipboard unavailable", output.ToString());
            Assert.AreEqual("https://github.com/login/device", page.AbsoluteUri);
            browserWait = ct;
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, (value, _) =>
        {
            clipboardWrites.Add(value);
            return Task.FromResult(clipboardAvailable);
        });
        var before = DateTimeOffset.UtcNow;
        var signIn = await oauth.SignInAsync(output, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("private-token", signIn.AccessToken);
        Assert.AreEqual("private-refresh", signIn.RefreshToken);
        Assert.IsTrue(signIn.ExpiresAt >= before.AddHours(8) && signIn.ExpiresAt <= DateTimeOffset.UtcNow.AddHours(8));
        Assert.IsTrue(signIn.RefreshExpiresAt >= before.AddSeconds(15897600) && signIn.RefreshExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(15897600));
        Assert.IsTrue(browserWait.IsCancellationRequested, "Login must finish without waiting for the user to close the browser.");
        Assert.Contains("https://github.com/login/device", output.ToString());
        Assert.Contains("ABCD-1234", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
        Assert.HasCount(1, clipboardWrites);
        CollectionAssert.AreEqual(new double[] { 2, 2, 7 }, delays);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnavailableOrFailingBrowserLeavesManualLoginUsable(bool missingLauncher)
    {
        using var http = new HttpClient(new HttpStub((r, _) => Task.FromResult(HttpStub.Json(
            r.RequestUri!.AbsolutePath.EndsWith("/code", StringComparison.Ordinal)
                ? """{"device_code":"private-code","user_code":"ABCD-1234","expires_in":900}"""
                : """{"access_token":"private-token"}"""))));
        using var output = new StringWriter();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oauth = new DeviceLogin(http, (_, ct) => attempted.Task.WaitAsync(ct), async (_, ct) =>
        {
            var launcher = missingLauncher ? new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
                : OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", ["/d", "/c", "exit", "/b", "7"]) : new ProcessStartInfo("/bin/false");
            try { await LoginBrowser.LaunchAsync(launcher, ct); }
            finally { attempted.SetResult(); }
        });
        Assert.AreEqual(new GitHubCredential("private-token"), await oauth.SignInAsync(output, limit.Token));
        Assert.Contains("https://github.com/login/device", output.ToString());
        Assert.Contains("ABCD-1234", output.ToString());
        Assert.Contains("Could not open the browser", output.ToString());
        Assert.DoesNotContain("private", output.ToString());
    }

    [TestMethod]
    [DataRow("""{"error":"access_denied"}""")]
    [DataRow("""{"error":"expired_token"}""")]
    public async Task LoginFailsOnDenialOrExpiry(string reply)
    {
        using var http = new HttpClient(new HttpStub((r, _) => Task.FromResult(HttpStub.Json(
            r.RequestUri!.AbsolutePath.EndsWith("/code", StringComparison.Ordinal)
                ? """{"device_code":"code","user_code":"ABCD","expires_in":900}""" : reply))));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new DeviceLogin(http, (_, _) => Task.CompletedTask).SignInAsync(TextWriter.Null, CancellationToken.None));
        Assert.Contains(JsonElement.Parse(reply).GetProperty("error").GetString()!, failure.Message);
    }

    [TestMethod]
    [DataRow(null, "use2", "/clusters/recommendations")]
    [DataRow("EUW", "euw", "/clusters")]
    public async Task ManagementSdkCreatesAnOwnerOnlyTunnelAndReturnsItsCluster(string? region, string cluster, string lookup)
    {
        List<string> lookups = [];
        string? createdOn = null;
        using var handler = new HttpStub(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var path = request.RequestUri!.AbsolutePath;
                lookups.Add(path);
                // The region list is anonymous; recommendations identify the account's service tier.
                Assert.AreEqual(path == "/clusters" ? null : "user-token", request.Headers.Authorization?.Parameter);
                return HttpStub.Json(path == "/clusters"
                    ? """[{"clusterId":"euw","uri":"https://euw.example.invalid","azureLocation":"WestEurope"}]"""
                    : """{"recommendedClusterId":"use2"}""");
            }
            Assert.AreEqual("github", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("user-token", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            Assert.AreEqual(HttpMethod.Put, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.IsFalse(body.RootElement.TryGetProperty("accessControl", out var acl) && acl.ValueKind != JsonValueKind.Null);
            Assert.Contains("*", request.Headers.GetValues("If-None-Match"));
            createdOn = request.RequestUri!.Host;
            return HttpStub.Json($$"""{"tunnelId":"blue-tunnel","clusterId":"{{cluster}}"}""");
        })
        { AllowAutoRedirect = false };
        await using var sdk = new TunnelBackend(() => Task.FromResult("user-token"), handler);
        Assert.AreEqual($"blue-tunnel.{cluster}", await sdk.CreateAsync(region, CancellationToken.None));
        Assert.AreEqual($"{cluster}.rel.tunnels.api.visualstudio.com", createdOn);
        CollectionAssert.AreEqual(new[] { lookup }, lookups);
        Assert.IsTrue(await sdk.DeleteAsync(TunnelKey.Parse($"blue-tunnel.{cluster}"), CancellationToken.None));
    }

    [TestMethod]
    public async Task UnknownRegionIsRejectedBeforeCreatingATunnel()
    {
        using var handler = new HttpStub((request, _) =>
        {
            Assert.AreEqual("/clusters", request.RequestUri!.AbsolutePath, "Only the region list may be requested.");
            return Task.FromResult(HttpStub.Json("""[{"clusterId":"euw","uri":"https://euw.example.invalid","azureLocation":"WestEurope"}]"""));
        })
        { AllowAutoRedirect = false };
        await using var sdk = new TunnelBackend(() => throw new AssertFailedException("Rejecting a region needs no token."), handler);
        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => sdk.CreateAsync("westeurope", CancellationToken.None));
        Assert.AreEqual("Unknown region 'westeurope'. Run 'regions' to see the available regions.", error.Message);
    }

    [TestMethod]
    public async Task RegionsCombineTheServiceRecommendationWithMeasuredLatency()
    {
        using var handler = new HttpStub((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            var recommendations = request.RequestUri!.AbsolutePath == "/clusters/recommendations";
            Assert.AreEqual(recommendations ? "user-token" : null, request.Headers.Authorization?.Parameter);
            return Task.FromResult(HttpStub.Json(recommendations ? """
                {"preferredClusterId":"usw2","recommendedClusterId":"usw2","isFallback":false,"recommendations":[
                  {"clusterId":"usw2","azureLocation":"WestUs2","azureGeo":"us","availability":"Available","utilizationPercent":27.59,"reason":"Preferred cluster with available capacity"},
                  {"clusterId":"asse","azureLocation":"SouthEastAsia","azureGeo":"as","availability":"Degraded","utilizationPercent":91.2,"reason":"Available fallback cluster"}]}
                """ : """
                [{"clusterId":"asse","uri":"https://asse.example.invalid","azureLocation":"SouthEastAsia"},
                 {"clusterId":"euw","uri":"https://euw.example.invalid","azureLocation":"WestEurope"},
                 {"clusterId":"usw2","uri":"https://usw2.example.invalid","azureLocation":"WestUs2"}]
                """));
        })
        { AllowAutoRedirect = false };
        Dictionary<string, TimeSpan?> latency = new()
        {
            ["https://asse.example.invalid"] = null,
            ["https://euw.example.invalid"] = TimeSpan.FromMilliseconds(39),
            ["https://usw2.example.invalid"] = TimeSpan.FromMilliseconds(203),
        };
        await using var sdk = new TunnelBackend(() => Task.FromResult("user-token"), handler,
            measureLatency: (endpoint, _) => Task.FromResult(latency[endpoint!]));
        Assert.AreEqual(string.Join(Environment.NewLine,
            "REGION  LOCATION       LATENCY  AVAILABILITY  LOAD  REASON",
            "euw     WestEurope     39 ms    -             -     -",
            "usw2    WestUs2        203 ms   Available     28%   Preferred cluster with available capacity",
            "asse    SouthEastAsia  -        Degraded      91%   Available fallback cluster",
            "Without --region, 'create' uses the recommended region usw2."),
            TunnelRegion.FormatTable(await sdk.ListRegionsAsync(CancellationToken.None)));
    }

    [TestMethod]
    public async Task RegionsWithoutAUsableEndpointAreListedWithUnknownLatency()
    {
        using var handler = new HttpStub((request, _) => Task.FromResult(HttpStub.Json(request.RequestUri!.AbsolutePath == "/clusters"
            ? """[{"clusterId":"euw","uri":"","azureLocation":"WestEurope"}]""" : """{"recommendations":[]}""")))
        { AllowAutoRedirect = false };
        // Without an injected measurement the real probe runs, and an unusable endpoint must not fail the listing.
        await using var sdk = new TunnelBackend(() => Task.FromResult("user-token"), handler);
        Assert.AreEqual(string.Join(Environment.NewLine,
            "REGION  LOCATION    LATENCY  AVAILABILITY  LOAD  REASON",
            "euw     WestEurope  -        -             -     -"), TunnelRegion.FormatTable(await sdk.ListRegionsAsync(CancellationToken.None)));
    }

    [TestMethod]
    public async Task LatencyProbeTimesOnlyTheWarmRequestAndReportsUnreachableEndpointsAsUnknown()
    {
        var clock = new SteppingClock();
        Func<CancellationToken, Task<HttpResponseMessage>> respond = _ =>
        {
            // The first request also pays for DNS, TCP, and TLS setup.
            clock.Elapsed += TimeSpan.FromMilliseconds(clock.Elapsed == TimeSpan.Zero ? 900 : 39);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };
        List<Uri> requested = [];
        using var http = new HttpClient(new HttpStub((request, ct) =>
        {
            Assert.AreEqual("DevTunnelSvc", request.Headers.UserAgent.Single().Product?.Name);
            requested.Add(request.RequestUri!);
            return respond(ct);
        }));
        var probe = new LatencyProbe(http, clock, TimeSpan.FromMilliseconds(50));
        const string endpoint = "https://euw.example.invalid";
        Assert.AreEqual(TimeSpan.FromMilliseconds(39), await probe.MeasureAsync(endpoint, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { new Uri(endpoint), new Uri(endpoint) }, requested);
        Assert.IsNull(await probe.MeasureAsync("not an absolute URI", CancellationToken.None));
        respond = _ => throw new HttpRequestException("unreachable");
        Assert.IsNull(await probe.MeasureAsync(endpoint, CancellationToken.None));
        respond = async ct => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); };
        Assert.IsNull(await probe.MeasureAsync(endpoint, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.MeasureAsync(endpoint, new CancellationToken(canceled: true)));
    }

    private sealed class SteppingClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }

    [TestMethod]
    public async Task ManagementSdkListsTunnelsFromEveryRegionWithStatusAndPorts()
    {
        // Each tunnel is read from its own cluster; "gone-tunnel" was deleted after the listing.
        // The legacy-format ID "bcdfghjk" is valid in the service but rejected by TunnelKey.Parse.
        Dictionary<string, string> details = new()
        {
            ["use2/blue-tunnel"] = """
                {"tunnelId":"blue-tunnel","clusterId":"use2","expiration":"2026-12-25T13:02:00Z",
                 "status":{"hostConnectionCount":{"current":1,"limit":1},"clientConnectionCount":2},
                 "ports":[{"portNumber":8080,"protocol":"tcp"},{"portNumber":3000,"protocol":"auto"}]}
                """,
            ["use2/red-tunnel"] = """{"tunnelId":"red-tunnel","clusterId":"use2","expiration":"2026-12-01T10:00:00+02:00","status":{},"ports":[]}""",
            ["euw/bcdfghjk"] = """{"tunnelId":"bcdfghjk","clusterId":"euw"}""",
        };
        using var handler = new HttpStub((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("user-token", request.Headers.Authorization?.Parameter);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath == "/tunnels")
            {
                Assert.AreEqual("true", query["global"]);
                // Like the service, the cross-region listing returns empty port lists.
                return Task.FromResult(HttpStub.Json("""
                    {"value":[
                      {"clusterId":"use2","value":[{"tunnelId":"blue-tunnel","clusterId":"use2","ports":[]},{"tunnelId":"red-tunnel","clusterId":"use2","ports":[]}]},
                      {"clusterId":"euw","value":[{"tunnelId":"bcdfghjk","clusterId":"euw","ports":[]},{"tunnelId":"gone-tunnel","clusterId":"euw","ports":[]}]}]}
                    """));
            }
            Assert.AreEqual("true", query["includePorts"], true);
            var key = request.RequestUri.Host.Split('.')[0] + request.RequestUri.AbsolutePath["/tunnels".Length..];
            if (details.TryGetValue(key, out var json))
                return Task.FromResult(HttpStub.Json(json));
            // The service reports a missing tunnel as a ProblemDetails "Not Found".
            var missing = new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("""{"title":"Not Found","status":404}""", Encoding.UTF8, "application/problem+json") };
            missing.Headers.Add("X-Served-By", "tunnels-prod-rel-euw-v3-cluster");
            return Task.FromResult(missing);
        })
        { AllowAutoRedirect = false };
        await using var sdk = new TunnelBackend(() => Task.FromResult("user-token"), handler);
        var table = TunnelSummary.FormatTable(await sdk.ListAsync(CancellationToken.None));
        Assert.AreEqual(string.Join(Environment.NewLine,
            "TUNNEL            HOSTS  CLIENTS  EXPIRES            PORTS",
            "bcdfghjk.euw      -      -        -                  -",
            "blue-tunnel.use2  1      2        2026-12-25T13:02Z  3000,8080",
            "red-tunnel.use2   -      -        2026-12-01T08:00Z  -"), table);
    }

    [TestMethod]
    public async Task UpdaterVerifiesAssetsBeforeReplacingAnExecutable()
    {
        var directory = Directory.CreateTempSubdirectory("devtunnel-update-");
        var exe = Path.Combine(directory.FullName, "installed");
        var content = Encoding.UTF8.GetBytes("replacement binary");
        var download = OperatingSystem.IsWindows() ? Zip("devtunnelsvc.exe", content) : content;
        var name = ReleaseUpdate.AssetName(OperatingSystem.IsWindows(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        var digest = "wrong";
        var downloads = 0;
        using var http = new HttpClient(new HttpStub((r, _) =>
        {
            if (r.RequestUri!.Host == "api.github.com")
                return Task.FromResult(HttpStub.Json(JsonSerializer.Serialize(new { tag_name = "v1.2.3", assets = new[] { new { name, size = download.Length, digest } } })));
            Assert.AreEqual($"https://github.com/VasiliyNovikov/DevTunnelSvc/releases/download/v1.2.3/{name}", r.RequestUri.AbsoluteUri);
            downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(download) });
        }));
        var update = new ReleaseUpdate(http);
        try
        {
            await File.WriteAllTextAsync(exe, "original");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var check = await update.RunAsync("1.2.3-rc.1", exe, true, () => throw new AssertFailedException(), CancellationToken.None);
            Assert.Contains("v1.2.3", check);
            Assert.AreEqual(0, downloads);
            await Assert.ThrowsExactlyAsync<IOException>(() => update.RunAsync("1.2.2", exe, false, () => throw new IOException("in use"), CancellationToken.None));
            Assert.AreEqual(0, downloads);
            Assert.Contains("SHA-256", (await Assert.ThrowsExactlyAsync<InvalidDataException>(() => update.RunAsync("1.2.2", exe, false, () => new MemoryStream(), CancellationToken.None))).Message);
            Assert.AreEqual("original", await File.ReadAllTextAsync(exe));
            Assert.HasCount(1, directory.GetFiles());
            digest = $"sha256:{Convert.ToHexString(SHA256.HashData(download))}";
            var result = await update.RunAsync("1.2.2", exe, false, () => new MemoryStream(), CancellationToken.None);
            Assert.Contains("Installed v1.2.3", result);
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(exe));
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserExecute, File.GetUnixFileMode(exe));
            Assert.AreEqual(2, downloads);
            Assert.Contains("up to date", await update.RunAsync("1.3.0", exe, false, () => throw new AssertFailedException(), CancellationToken.None));
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, true, false)]
    [DataRow(false, true, true)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, false, false)]
    public async Task UpdatesInstallTheSelectedVerifiedReleaseAndLeaveTheServiceRunning(bool tray, bool initiallyRunning, bool validDigest)
    {
        var directory = Directory.CreateTempSubdirectory("devtunnel-tray-update-");
        var exe = Path.Combine(directory.FullName, "installed");
        var bytes = "replacement"u8.ToArray();
        var windows = OperatingSystem.IsWindows();
        var archive = windows ? Zip("devtunnelsvc.exe", bytes) : bytes;
        var name = ReleaseUpdate.AssetName(windows, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        var digest = validDigest ? $"sha256:{Convert.ToHexString(SHA256.HashData(archive))}" : "sha256:wrong";
        var running = initiallyRunning;
        var checks = 0;
        var confirms = 0;
        var starts = 0;
        var leases = 0;
        using var http = new HttpClient(new HttpStub((r, _) =>
        {
            if (r.RequestUri!.Host == "api.github.com")
            {
                Assert.AreEqual(initiallyRunning, running);
                checks++;
                return Task.FromResult(HttpStub.Json(JsonSerializer.Serialize(new { tag_name = "v2.0.0", assets = new[] { new { name, size = archive.Length, digest } } })));
            }
            Assert.IsFalse(running);
            Assert.AreEqual($"https://github.com/VasiliyNovikov/DevTunnelSvc/releases/download/v2.0.0/{name}", r.RequestUri.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }));
        try
        {
            await File.WriteAllTextAsync(exe, "original");
            bool Confirm(string question) { Assert.IsTrue(running); Assert.Contains("v2.0.0", question); confirms++; return true; }
            async Task Start()
            {
                using var lease = InstanceLease.Acquire(directory.FullName);
                Assert.AreEqual(validDigest ? "replacement" : "original", await File.ReadAllTextAsync(exe));
                starts++;
                running = true;
            }
            Task<string> Send(Request request, CancellationToken _)
            {
                if (!running) throw new IOException("not listening", new TimeoutException());
                if (request.Command == "service-pid") return Task.FromResult("42");
                Assert.AreEqual(new Request("shutdown-for-update", ServicePid: 42), request);
                running = false;
                return Task.FromResult("stopping");
            }
            IDisposable Lease()
            {
                Assert.IsFalse(running);
                if (++leases == 1 && initiallyRunning && !tray) throw new IOException("shutdown is finishing");
                return InstanceLease.Acquire(directory.FullName);
            }
            var updater = new ReleaseUpdate(http, new(Send, Start));
            var menu = new TrayUpdate(t => updater.FindNewerAsync("1.0.0", t), Confirm,
                _ => { running = false; return Task.CompletedTask; }, (release, t) => updater.ApplyAsync(release, exe, Lease, t), Start);
            async Task<string?> Update() => tray ? await menu.RunAsync("1.0.0", CancellationToken.None)
                : await updater.RunAsync("1.0.0", exe, false, Lease, CancellationToken.None);
            if (validDigest)
                Assert.AreEqual(initiallyRunning ? "Installed v2.0.0 and restarted DevTunnelSvc."
                    : "Installed v2.0.0 and started DevTunnelSvc.", await Update());
            else
            {
                var failure = (await Assert.ThrowsExactlyAsync<InvalidOperationException>(Update)).Message;
                Assert.Contains("SHA-256", failure);
                Assert.EndsWith(initiallyRunning ? "The service was restarted." : "The service was started.", failure);
            }
            Assert.AreEqual(1, checks);
            Assert.AreEqual(tray ? 1 : 0, confirms, "Only the tray asks; update --force runs unattended.");
            Assert.AreEqual(1, starts);
            Assert.IsTrue(running);
            Assert.AreEqual(validDigest ? "replacement" : "original", await File.ReadAllTextAsync(exe));
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    [DataRow("2.0.0", false, "up to date")]
    [DataRow("3.0.0", false, "up to date")]
    [DataRow("1.0.0", true, "Available: v2.0.0")]
    public async Task ForceIsReadOnlyForACurrentReleaseOrCheck(string installed, bool check, string expected)
    {
        var handoff = new UpdateHandoff((_, _) => throw new AssertFailedException("The service must not be contacted."),
            () => throw new AssertFailedException("The service must not be started."));
        using var http = new HttpClient(new HttpStub((r, _) =>
        {
            Assert.AreEqual("api.github.com", r.RequestUri!.Host, "No asset should be downloaded.");
            return Task.FromResult(HttpStub.Json("""{"tag_name":"v2.0.0","assets":[]}"""));
        }));
        var result = await new ReleaseUpdate(http, handoff).RunAsync(installed, "unused", check, () => throw new AssertFailedException(), CancellationToken.None);
        Assert.Contains(expected, result);
    }

    [TestMethod]
    public async Task AServiceThatNeverReleasesItsLeaseSurfacesTheLockErrorAfterABoundedWait()
    {
        var elapsed = TimeSpan.Zero;
        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => UpdateHandoff.WaitForLeaseAsync(
            () => throw new IOException("in use"), interval =>
            {
                Assert.IsGreaterThan(TimeSpan.Zero, interval);
                elapsed += interval;
                Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(30), elapsed);
                return Task.CompletedTask;
            }));
        Assert.AreEqual("in use", failure.Message);
        Assert.AreEqual(TimeSpan.FromSeconds(30), elapsed);
    }

    [TestMethod]
    public async Task ChangedReleaseIsRejectedBeforeTheWorkerStopsTheService()
    {
        using var http = new HttpClient(new HttpStub((r, _) =>
        {
            Assert.AreEqual("api.github.com", r.RequestUri!.Host);
            return Task.FromResult(HttpStub.Json("""{"tag_name":"v3.0.0","assets":[]}"""));
        }));
        var updater = new ReleaseUpdate(http, new((_, _) => throw new AssertFailedException(), () => throw new AssertFailedException()));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => updater.RunAsync("1.0.0", "unused", false,
            () => throw new AssertFailedException(), CancellationToken.None, "v2.0.0"));
        Assert.Contains("latest release changed", failure.Message);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task CopiedCliInstallsOnlyAtTheOriginalPathAndRestartsOnlyForForce(bool force, bool validDigest)
    {
        var root = Directory.CreateTempSubdirectory("devtunnel-copied-cli-");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipe = $"devtunnel-copy-{Guid.NewGuid():N}";
        var installed = Path.Combine(root.FullName, "installed");
        var copy = Path.Combine(root.CreateSubdirectory($".devtunnelsvc-update-{Guid.NewGuid():N}").FullName, "installed");
        var state = root.CreateSubdirectory("state").FullName;
        var replacement = "verified image"u8.ToArray();
        var name = ReleaseUpdate.AssetName(OperatingSystem.IsWindows(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        var asset = OperatingSystem.IsWindows() ? Zip("devtunnelsvc.exe", replacement) : replacement;
        var digest = validDigest ? $"sha256:{Convert.ToHexString(SHA256.HashData(asset))}" : "sha256:invalid";
        using var http = new HttpClient(new HttpStub((r, _) => Task.FromResult(r.RequestUri!.Host == "api.github.com"
            ? HttpStub.Json(JsonSerializer.Serialize(new { tag_name = "v2.0.0", assets = new[] { new { name, size = asset.Length, digest } } }))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(asset) })));
        var server = force ? ServiceLifetime.RunAsync(TextWriter.Null, pipe, state, Cli.NoBackgroundUpdates, limit.Token) : Task.CompletedTask;
        try
        {
            File.WriteAllText(installed, "original");
            File.Copy(installed, copy);
            List<string> commands = [];
            Task<int> Run(ProcessStartInfo info, CancellationToken t)
            {
                Assert.AreEqual(installed, info.FileName, "Never start a service from the disposable copy.");
                Assert.IsTrue(info.CreateNoWindow && t.CanBeCanceled);
                Assert.IsTrue(info.StandardOutputHandle is { IsClosed: false, IsInvalid: false });
                Assert.IsFalse(info.RedirectStandardError);
                Assert.IsNull(info.StandardErrorHandle, "Keep restart diagnostics on the caller's stderr.");
                using var free = InstanceLease.Acquire(state);
                commands.Add(string.Join(' ', info.ArgumentList));
                return Task.FromResult(0);
            }
            var parent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var flow = new CliUpdate(http, "1.0.0", () => new(copy, state, pipe, Run, () => parent.Task));
            Task<string> Apply() => flow.RunAsync(new(false, force, "v2.0.0"), limit.Token);
            if (validDigest) Assert.Contains(force ? "restarted" : "Run 'service start'", await Apply());
            else Assert.Contains("SHA-256", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(Apply)).Message);
            Assert.AreEqual("original", File.ReadAllText(copy));
            Assert.AreEqual(validDigest ? "verified image" : "original", File.ReadAllText(installed));
            Assert.AreEqual(force ? "service start,status" : "", string.Join(',', commands));
            var log = File.ReadAllText(Path.Combine(state, "app.log"));
            Assert.IsFalse(File.Exists(Path.Combine(state, "update.log")));
            if (force)
            {
                Assert.Contains("[service] Control pipe ready.", log);
                Assert.Contains("[update] Verifying the selected release", log);
                Assert.Contains($"Stopping service PID {Environment.ProcessId}", log);
                Assert.Contains("Installing verified release", log);
                Assert.Contains("Launching installed service start", log);
                Assert.Contains("service start exit code: 0", log);
                Assert.Contains("status exit code: 0", log);
                Assert.Contains(validDigest ? "Installed v2.0.0." : "Install failed: InvalidDataException", log);
            }
            else Assert.DoesNotContain("Stopping", log);
        }
        finally { await limit.CancelAsync(); await server; root.Delete(true); }
    }

    [TestMethod]
    public async Task ForceCannotStopAnInvalidOrReplacedServiceOrIgnoreCancellation()
    {
        using var cancel = new CancellationTokenSource();
        var target = "0";
        var cancelAfterLookup = false;
        Task<string> Send(Request r, CancellationToken _)
        {
            if (r.Command == "service-pid")
            {
                if (cancelAfterLookup) cancel.Cancel();
                return Task.FromResult(target);
            }
            Assert.AreEqual(new Request("shutdown-for-update", ServicePid: 42), r);
            throw new InvalidOperationException("instance has changed");
        }
        var handoff = new UpdateHandoff(Send, () => throw new AssertFailedException("A service that was not stopped must not be started."));
        Task<string> Run() => handoff.ApplyAsync("v2.0.0", _ => throw new AssertFailedException(), cancel.Token);
        Assert.Contains("invalid process ID", (await Assert.ThrowsExactlyAsync<InvalidDataException>(Run)).Message);
        target = "invalid";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(Run);
        target = "42";
        Assert.Contains("instance has changed", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(Run)).Message);
        cancelAfterLookup = true;
        await Assert.ThrowsAsync<OperationCanceledException>(Run);
    }

    [TestMethod]
    public async Task CancellationDuringShutdownStillRestartsTheOriginalInstallation()
    {
        using var cancel = new CancellationTokenSource();
        var restarts = 0;
        var handoff = new UpdateHandoff((request, t) =>
        {
            if (request.Command == "service-pid") return Task.FromResult("42");
            cancel.Cancel();
            t.ThrowIfCancellationRequested();
            return Task.FromResult("stopping");
        }, () => { restarts++; return Task.CompletedTask; });
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => handoff.ApplyAsync("v2.0.0",
            t => { t.ThrowIfCancellationRequested(); throw new AssertFailedException(); }, cancel.Token));
        Assert.IsInstanceOfType<OperationCanceledException>(failure.InnerException);
        Assert.AreEqual(1, restarts);
    }

    [TestMethod]
    public async Task InvalidPackagesAndVersionsFailClosed()
    {
        using var invalid = new MemoryStream(Zip("../devtunnelsvc.exe", [1]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReleaseUpdate.UnpackAsync(invalid, Stream.Null, CancellationToken.None));
        using var input = new MemoryStream([1, 2]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReleaseUpdate.CopySizedAsync(input, Stream.Null, 0, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReleaseUpdate.CopySizedAsync(input, Stream.Null, 1, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => ReleaseUpdate.CopySizedAsync(input, Stream.Null, 3, CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(() => ReleaseUpdate.IsNewer("1.2.2", "v1.2.3-rc.1"));
        Assert.AreEqual("devtunnelsvc-win-arm64.zip", ReleaseUpdate.AssetName(true, System.Runtime.InteropServices.Architecture.Arm64));
        Assert.AreEqual("devtunnelsvc-linux-x64", ReleaseUpdate.AssetName(false, System.Runtime.InteropServices.Architecture.X64));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("""{"device_code":"","user_code":"1234","expires_in":900}""")]
    [DataRow("""{"device_code":"test","user_code":"","expires_in":900}""")]
    [DataRow("""{"device_code":"test","user_code":"1234","expires_in":0}""")]
    public async Task MalformedDeviceCodesAreNeverDisplayedOrPolled(string json)
    {
        using var http = new HttpClient(new HttpStub((_, _) => Task.FromResult(HttpStub.Json(json))));
        using var output = new StringWriter();
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new DeviceLogin(http,
            (_, _) => throw new AssertFailedException("Unexpected token poll."),
            (_, _) => throw new AssertFailedException("Do not launch a browser without a usable code."),
            (_, _) => throw new AssertFailedException("Do not touch the clipboard without a usable code.")).SignInAsync(output, CancellationToken.None));
        Assert.Contains("sign-in code", error.Message);
        Assert.AreEqual("", output.ToString());
    }

    [TestMethod]
    public async Task LoginHttpFailureDoesNotReturnACredential()
    {
        using var http = new HttpClient(new HttpStub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            new DeviceLogin(http, Task.Delay).SignInAsync(TextWriter.Null, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("latest")]
    [DataRow("v1.2")]
    [DataRow("v1.2.3.4")]
    [DataRow("v01.2.3")]
    public void NoncanonicalTagsCannotSelectDownloadUrls(string? tag)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ReleaseUpdate.IsNewer("1.0.0", tag));
    }

    [TestMethod]
    public async Task MissingReleaseOrAssetNeverReplacesTheInstalledFile()
    {
        var metadata = "null";
        using var http = new HttpClient(new HttpStub((r, _) =>
        {
            Assert.AreEqual("api.github.com", r.RequestUri!.Host);
            return Task.FromResult(HttpStub.Json(metadata));
        }));
        var updater = new ReleaseUpdate(http);
        Assert.Contains("no release", (await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.RunAsync("1.0.0", "unused", false, () => new MemoryStream(), CancellationToken.None))).Message);
        metadata = """{"tag_name":"v1.1.0","assets":[]}""";
        Assert.Contains("Release v1.1.0 has no", (await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.RunAsync("1.0.0", "unused", false, () => new MemoryStream(), CancellationToken.None))).Message);
        Assert.IsFalse(ReleaseUpdate.IsNewer("1.1.0", "v1.1.0"));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => ReleaseUpdate.AssetName(false, System.Runtime.InteropServices.Architecture.X86));
    }

    [TestMethod]
    public async Task MissingOAuthResponseAfterTheDeviceCodeIsAnError()
    {
        using var http = new HttpClient(new HttpStub((r, _) => Task.FromResult(HttpStub.Json(
            r.RequestUri!.AbsolutePath.EndsWith("/code", StringComparison.Ordinal)
                ? """{"device_code":"test","user_code":"ABCD","expires_in":900}""" : "null"))));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new DeviceLogin(http, (d, _) =>
        {
            Assert.AreEqual(TimeSpan.FromSeconds(5), d);
            return Task.CompletedTask;
        }).SignInAsync(TextWriter.Null, CancellationToken.None));
        Assert.Contains("invalid response", failure.Message);
    }

    [TestMethod]
    public async Task ArchiveExtractionIsBoundedAndDoesNotCloseTheOwnedDownload()
    {
        using var valid = new MemoryStream(Zip("devtunnelsvc.exe", [42]));
        using var target = new MemoryStream();
        await ReleaseUpdate.UnpackAsync(valid, target, CancellationToken.None);
        Assert.IsTrue(valid.CanRead);
        CollectionAssert.AreEqual(new byte[] { 42 }, target.ToArray());
        using var extra = new MemoryStream();
        using (var zip = new ZipArchive(extra, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("devtunnelsvc.exe");
            zip.CreateEntry("unexpected.dll");
        }
        extra.Position = 0;
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReleaseUpdate.UnpackAsync(extra, Stream.Null, CancellationToken.None));
        Assert.Contains("single devtunnelsvc.exe", failure.Message);
        Assert.IsFalse(ReleaseUpdate.IsNewer("2.0.0-beta", "v1.0.0"));
        Assert.IsTrue(ReleaseUpdate.IsNewer("1.0.0", "v2.0.0"));
    }

    [TestMethod]
    public void WindowsReplacementRollsBackOnFailureAndRetainsOnlyThePreviousVersion()
    {
        var dir = Directory.CreateTempSubdirectory("devtunnel-rollback-");
        try
        {
            var path = Path.Combine(dir.FullName, "devtunnelsvc.exe");
            var next = path + ".new";
            File.WriteAllText(path, "installed");
            Assert.ThrowsExactly<FileNotFoundException>(() => ReleaseUpdate.ReplaceWindowsImage(path, next));
            Assert.AreEqual("installed", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".previous"));
            File.WriteAllText(next, "new");
            File.WriteAllText(path + ".previous", "older");
            ReleaseUpdate.ReplaceWindowsImage(path, next);
            Assert.AreEqual("new", File.ReadAllText(path));
            Assert.AreEqual("installed", File.ReadAllText(path + ".previous"));
        }
        finally { dir.Delete(true); }
    }

    private static byte[] Zip(string name, byte[] bytes)
    {
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry(name).Open())
            stream.Write(bytes);
        return result.ToArray();
    }
}
