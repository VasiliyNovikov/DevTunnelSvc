using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.DevTunnels.Contracts;

namespace DevTunnelSvc.Auth;

internal sealed record DeviceCode([property: JsonPropertyName("device_code")] string Code, string UserCode, int ExpiresIn, int Interval);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DeviceCode))]
[JsonSerializable(typeof(TokenResult))]
[JsonSerializable(typeof(GitHubCredential))]
internal sealed partial class LoginJson : JsonSerializerContext;

internal sealed class DeviceLogin(HttpClient http, Func<TimeSpan, CancellationToken, Task> delay,
    Func<Uri, CancellationToken, Task>? openBrowser = null, Func<string, CancellationToken, Task<bool>>? copyUserCode = null)
{
    private static readonly Uri DeviceEndpoint = new("https://github.com/login/device/code");
    private static readonly Uri VerificationPage = new("https://github.com/login/device");

    public async Task<GitHubCredential> SignInAsync(TextWriter output, CancellationToken ct)
    {
        var clientId = TunnelServiceProperties.Production.GitHubAppClientId;
        using var start = await GitHubOAuth.PostAsync(http, DeviceEndpoint, new() { ["client_id"] = clientId, ["scope"] = "offline_access" }, ct);
        start.EnsureSuccessStatusCode();
        var code = await start.Content.ReadFromJsonAsync(LoginJson.Default.DeviceCode, ct);
        if (code is null || string.IsNullOrEmpty(code.Code) || string.IsNullOrEmpty(code.UserCode) || code.ExpiresIn <= 0)
            throw new InvalidDataException("GitHub did not return a valid sign-in code.");
        using var expires = CancellationTokenSource.CreateLinkedTokenSource(ct);
        expires.CancelAfter(TimeSpan.FromSeconds(code.ExpiresIn));
        await ShowCodeAsync(code.UserCode, output, expires.Token);
        // A launcher may live as long as the browser. Stop waiting for it when authorization ends.
        var opening = OpenBrowserAsync(output, expires.Token);
        try { return await PollAsync(code, clientId, expires.Token); }
        finally { await expires.CancelAsync(); await opening; }
    }

    private async Task ShowCodeAsync(string userCode, TextWriter output, CancellationToken ct)
    {
        output.WriteLine($"Enter {userCode} at {VerificationPage.AbsoluteUri} to sign in with GitHub.");
        if (copyUserCode is not null)
        {
            var copied = await copyUserCode(userCode, ct);
            output.WriteLine(copied ? "Code copied to the clipboard." : "Clipboard unavailable. Enter the printed code manually.");
        }
        await output.FlushAsync(ct);
    }

    private async Task OpenBrowserAsync(TextWriter output, CancellationToken ct)
    {
        if (openBrowser is null) return;
        try { await openBrowser(VerificationPage, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            output.WriteLine("Could not open the browser automatically. Open the URL above on any device and enter the code.");
        }
    }

    private async Task<GitHubCredential> PollAsync(DeviceCode code, string clientId, CancellationToken ct)
    {
        var interval = code.Interval > 0 ? code.Interval : 5;
        while (true)
        {
            await delay(TimeSpan.FromSeconds(interval), ct);
            var result = await GitHubOAuth.ExchangeAsync(http, new()
            {
                ["client_id"] = clientId,
                ["device_code"] = code.Code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, ct);
            if (result is { Error: null, AccessToken.Length: > 0 })
                return result.ToCredential(DateTimeOffset.UtcNow);
            interval = NextInterval(result?.Error, interval);
        }
    }

    private static int NextInterval(string? error, int current) => error switch
    {
        "slow_down" => current + 5,
        "authorization_pending" => current,
        _ => throw new InvalidOperationException($"GitHub sign-in failed: {error ?? "invalid response"}."),
    };
}
