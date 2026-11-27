using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevTunnelSvc.Auth;
using DevTunnelSvc.Service;
using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc.Tests;

[TestClass]
public sealed class CredentialTests
{
    private DirectoryInfo directory = null!;
    private CredentialFile file = null!;
    private static readonly DateTimeOffset now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static GitHubCredential Initial { get; } = new("synthetic-access", now.AddSeconds(35), "synthetic-refresh", now.AddDays(7));
    private static GitHubCredential Rotated { get; } = new("new-access", now.AddHours(8), "new-refresh", now.AddDays(180));

    [TestInitialize]
    public void SetUp()
    {
        directory = Directory.CreateTempSubdirectory("devtunnel-credential-tests-");
        file = new(directory.FullName);
    }

    [TestCleanup]
    public void CleanUp() => directory.Delete(true);

    [TestMethod]
    public void SavedPairIsProtectedAndRemovableWithoutTouchingOtherFiles()
    {
        Assert.IsNull(file.Read());
        var sibling = Path.Combine(directory.FullName, "other-application");
        File.WriteAllText(sibling, "unchanged");
        file.Write(Initial);
        Assert.AreEqual(Initial, new CredentialFile(directory.FullName).Read());
        var bytes = File.ReadAllBytes(file.FilePath);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("github.dpapi", Path.GetFileName(file.FilePath));
            Assert.DoesNotContain("synthetic", Encoding.UTF8.GetString(bytes));
            var clear = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            Assert.AreEqual(Initial, JsonSerializer.Deserialize(clear, LoginJson.Default.GitHubCredential));
        }
        else
        {
            Assert.AreEqual("github.json", Path.GetFileName(file.FilePath));
            Assert.AreEqual((UnixFileMode)0x180, File.GetUnixFileMode(file.FilePath));
            Assert.AreEqual((UnixFileMode)0x1c0, File.GetUnixFileMode(Path.GetDirectoryName(file.FilePath)!));
            Assert.AreEqual(Initial, JsonSerializer.Deserialize(bytes, LoginJson.Default.GitHubCredential));
        }

        file.Write(Rotated);
        Assert.AreEqual(Rotated, file.Read());
        File.WriteAllText(file.FilePath + ".pending", "interrupted test write");
        file.Delete();
        file.Delete();
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(file.FilePath)!));
        Assert.IsNull(file.Read());
        Assert.AreEqual("unchanged", File.ReadAllText(sibling));
    }

    [TestMethod]
    public void CorruptOrOversizedStateIsNotTreatedAsSignedOutAndCanBeRemoved()
    {
        file.Write(Initial);
        File.WriteAllText(file.FilePath, "not a credential: synthetic-secret");
        var error = Assert.ThrowsExactly<InvalidDataException>(() => file.Read());
        Assert.Contains("logout", error.Message);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        File.WriteAllBytes(file.FilePath, []);
        Assert.ThrowsExactly<InvalidDataException>(() => file.Read());
        File.WriteAllBytes(file.FilePath, new byte[65537]);
        Assert.ThrowsExactly<InvalidDataException>(() => file.Read());
        file.Delete();
        Assert.IsNull(file.Read());
    }

    [TestMethod]
    public void LinuxRequiresPrivateFileAndDirectoryAndDoesNotFollowLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        file.Write(Initial);
        var auth = Path.GetDirectoryName(file.FilePath)!;
        File.SetUnixFileMode(file.FilePath, (UnixFileMode)0x1a4);
        Assert.Contains("0600", Assert.ThrowsExactly<UnauthorizedAccessException>(() => file.Read()).Message);
        file.Delete();
        File.SetUnixFileMode(auth, (UnixFileMode)0x1ed);
        Assert.Contains("0700", Assert.ThrowsExactly<UnauthorizedAccessException>(() => file.Write(Initial)).Message);
        File.SetUnixFileMode(auth, (UnixFileMode)0x1c0);
        var target = Path.Combine(directory.FullName, "unrelated");
        File.WriteAllText(target, "untouched");
        File.CreateSymbolicLink(file.FilePath, target);
        Assert.ThrowsExactly<IOException>(() => file.Read());
        Assert.ThrowsExactly<IOException>(() => file.Write(Initial));
        file.Delete();
        Assert.AreEqual("untouched", File.ReadAllText(target));
        Directory.Delete(auth);
        Directory.CreateSymbolicLink(auth, directory.FullName);
        Assert.ThrowsExactly<IOException>(() => file.Write(Initial));
        Directory.Delete(auth);
        File.SetUnixFileMode(directory.FullName, (UnixFileMode)0x1f8);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => file.Write(Initial));
    }

    [TestMethod]
    public async Task ConcurrentSdkRequestsShareOneRefreshAndARestartUsesTheRotatedPair()
    {
        file.Write(Initial);
        var rotating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task<GitHubCredential> Renew(string token, CancellationToken ct)
        {
            Assert.AreEqual(Initial.RefreshToken, token);
            Interlocked.Increment(ref count);
            rotating.SetResult();
            await proceed.Task.WaitAsync(ct);
            return Rotated;
        }
        using var session = new SessionCredential(file, Renew, new Clock(now));
        Assert.IsTrue(session.IsSignedIn);
        var first = session.RequireTokenAsync(limit.Token);
        await rotating.Task.WaitAsync(limit.Token);
        var second = session.RequireTokenAsync(limit.Token);
        proceed.SetResult();
        CollectionAssert.AreEqual(new[] { Rotated.AccessToken, Rotated.AccessToken }, await Task.WhenAll(first, second));
        AssertRotatedOnce(count);
        using var restarted = WithoutRefresh(new(directory.FullName));
        Assert.AreEqual(Rotated.AccessToken, await restarted.RequireTokenAsync(limit.Token));
    }

    [TestMethod]
    public async Task LogoutCannotBeUndoneByAnInFlightTokenRotation()
    {
        file.Write(Initial);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rotation = new TaskCompletionSource<GitHubCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new SessionCredential(file, (_, ct) => rotation.Task.WaitAsync(ct), new Clock(now));
        var refresh = session.RequireTokenAsync(limit.Token);
        var logout = session.ReplaceAsync(null, limit.Token);
        Assert.IsFalse(logout.IsCompleted);
        rotation.SetResult(Rotated);
        await Task.WhenAll(refresh, logout);
        Assert.IsFalse(session.IsSignedIn);
        Assert.IsNull(file.Read());
        Assert.Contains("login", (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.RequireTokenAsync(limit.Token))).Message);
    }

    [TestMethod]
    public async Task AFailedRotationWriteIsRetriedWithoutSpendingTheOldRefreshTokenAgain()
    {
        file.Write(Initial);
        var staged = file.FilePath + ".pending";
        var count = 0;
        using var session = new SessionCredential(file, (_, _) =>
        {
            count++;
            Directory.CreateDirectory(staged);
            return Task.FromResult(Rotated);
        }, new Clock(now));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => session.RequireTokenAsync(CancellationToken.None));
        Assert.AreEqual(Initial, file.Read());
        Directory.Delete(staged);
        Assert.AreEqual(Rotated.AccessToken, await session.RequireTokenAsync(CancellationToken.None));
        AssertRotatedOnce(count);
    }

    [TestMethod]
    public async Task NonExpiringAndStillValidUnrefreshableTokensWorkUntilTheirActualExpiry()
    {
        using var session = WithoutRefresh(file);
        await session.ReplaceAsync(new("legacy-token"), CancellationToken.None);
        Assert.AreEqual("legacy-token", await session.RequireTokenAsync(CancellationToken.None));
        file.Write(Initial with { RefreshExpiresAt = now });
        Assert.IsTrue(session.IsSignedIn);
        Assert.AreEqual(Initial.AccessToken, await session.RequireTokenAsync(CancellationToken.None));
        file.Write(new("old", now));
        Assert.IsFalse(session.IsSignedIn);
        Assert.Contains("login", (await Assert.ThrowsExactlyAsync<SignInExpiredException>(() => session.RequireTokenAsync(CancellationToken.None))).Message);
    }

    [TestMethod]
    public async Task RefreshExchangesOnlyTheRefreshGrantAndSavesTheNewExpiry()
    {
        using var http = new HttpClient(new HttpStub(async (r, ct) =>
        {
            var fields = await HttpStub.GitHubFormAsync(r, ct);
            Assert.AreEqual("https://github.com/login/oauth/access_token", r.RequestUri!.AbsoluteUri);
            Assert.AreEqual(3, fields.Count);
            Assert.AreEqual("refresh_token", fields["grant_type"]);
            Assert.AreEqual(Initial.RefreshToken, fields["refresh_token"]);
            return HttpStub.Json("""{"access_token":"new-access","expires_in":120,"refresh_token":"new-refresh","refresh_token_expires_in":600}""");
        }));
        file.Write(Initial);
        using var session = RefreshingWith(http);
        var before = DateTimeOffset.UtcNow;
        using var api = new HttpStub((request, _) =>
        {
            Assert.AreEqual("github", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("new-access", request.Headers.Authorization?.Parameter);
            Assert.AreEqual("new-refresh", file.Read()?.RefreshToken, "Persist rotation before sending the new access token to the SDK.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        })
        { AllowAutoRedirect = false };
        await using var sdk = new TunnelBackend(() => session.RequireTokenAsync(CancellationToken.None), api);
        Assert.IsTrue(await sdk.DeleteAsync(TunnelKey.Parse("blue-tunnel.use2"), CancellationToken.None));
        var saved = file.Read()!;
        Assert.AreEqual("new-refresh", saved.RefreshToken);
        Assert.IsTrue(saved.ExpiresAt >= before.AddSeconds(120) && saved.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(120));
        Assert.IsTrue(saved.RefreshExpiresAt >= before.AddSeconds(600) && saved.RefreshExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(600));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK, "bad_refresh_token")]
    [DataRow(HttpStatusCode.BadRequest, "invalid_grant")]
    public async Task RevokedRefreshRequiresSignInButAnOutageDoesNotDestroyTheGrant(HttpStatusCode status, string errorCode)
    {
        file.Write(Initial);
        var outage = true;
        using var http = HttpStub.Respond(_ => HttpStub.Json($$"""{"error":"{{errorCode}}","error_description":"synthetic-private-data"}""",
            outage ? HttpStatusCode.ServiceUnavailable : status));
        using var session = RefreshingWith(http);
        await TokenFailsAsync<HttpRequestException>(session);
        Assert.AreEqual(Initial, file.Read());
        outage = false;
        var error = await TokenFailsAsync<SignInExpiredException>(session);
        Assert.Contains("login", error.Message);
        Assert.DoesNotContain("synthetic-private-data", error.ToString());
        Assert.IsNull(file.Read());
    }

    [TestMethod]
    public void InvalidCredentialsDoNotOverwriteAWorkingSignInOrLeakTokensInDiagnostics()
    {
        file.Write(Initial);
        foreach (var invalid in new[] { Initial with { AccessToken = "" }, Initial with { ExpiresAt = null },
            Initial with { RefreshToken = "x\nsensitive" }, Initial with { RefreshToken = null } })
            Assert.ThrowsExactly<ArgumentException>(() => file.Write(invalid));
        Assert.AreEqual(Initial, file.Read());
        Assert.DoesNotContain("synthetic", new Request("login", SignIn: Initial).ToString());
    }

    [TestMethod]
    [DataRow("""{"access_token":"sensitive","expires_in":-1,"refresh_token":"rotated"}""", HttpStatusCode.OK)]
    [DataRow("""{"access_token":"sensitive","expires_in":60}""", HttpStatusCode.OK)]
    [DataRow("""{"error":"temporarily_unavailable","access_token":"sensitive"}""", HttpStatusCode.BadRequest)]
    public async Task IncompleteOrMalformedRefreshDoesNotReplaceTheSavedPair(string payload, HttpStatusCode status)
    {
        file.Write(Initial);
        using var http = HttpStub.Respond(_ => HttpStub.Json(payload, status));
        using var session = RefreshingWith(http);
        var error = await TokenFailsAsync<InvalidDataException>(session);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.AreEqual(Initial, file.Read());
    }

    private static Task<T> TokenFailsAsync<T>(SessionCredential session) where T : Exception =>
        Assert.ThrowsExactlyAsync<T>(() => session.RequireTokenAsync(CancellationToken.None));

    private SessionCredential RefreshingWith(HttpClient http) => new(file, (token, ct) => GitHubOAuth.RefreshAsync(http, token, ct), new Clock(now));
    private static SessionCredential WithoutRefresh(CredentialFile store) => new(store, (_, _) => throw new AssertFailedException("Unexpected refresh."), new Clock(now));

    private void AssertRotatedOnce(int count)
    {
        Assert.AreEqual(1, count);
        Assert.AreEqual(Rotated, file.Read());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
