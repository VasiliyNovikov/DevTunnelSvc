using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Platform;
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
    public async Task ManagementSdkCreatesAnOwnerOnlyTunnelAndReturnsItsCluster()
    {
        var created = false;
        using var handler = new HttpStub(async (request, ct) =>
        {
            Assert.AreEqual("github", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("user-token", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Get)
                return HttpStub.Json("""{"recommendedClusterId":"use2"}""");
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            Assert.AreEqual(HttpMethod.Put, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.IsFalse(body.RootElement.TryGetProperty("accessControl", out var acl) && acl.ValueKind != JsonValueKind.Null);
            Assert.Contains("*", request.Headers.GetValues("If-None-Match"));
            created = true;
            return HttpStub.Json("""{"tunnelId":"blue-tunnel","clusterId":"use2"}""");
        })
        { AllowAutoRedirect = false };
        await using var sdk = new TunnelBackend(() => Task.FromResult("user-token"), handler);
        Assert.AreEqual("blue-tunnel.use2", await sdk.CreateAsync(CancellationToken.None));
        Assert.IsTrue(created);
        Assert.IsTrue(await sdk.DeleteAsync(TunnelKey.Parse("blue-tunnel.use2"), CancellationToken.None));
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
    [DataRow("latest")]
    [DataRow("v1.2")]
    [DataRow("v1.2.3.4")]
    [DataRow("v01.2.3")]
    public void NoncanonicalTagsCannotSelectDownloadUrls(string tag)
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
