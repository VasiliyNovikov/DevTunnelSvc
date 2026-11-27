using System.Text.Json;
using System.Text.Json.Serialization;
using DevTunnelSvc.Service;

namespace DevTunnelSvc.Updates;

internal sealed record AutoUpdateSettings(int Version, bool Enabled, DateTimeOffset? LastAttemptUtc = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AutoUpdateSettings))]
internal sealed partial class AutoUpdateJson : JsonSerializerContext;

internal sealed class AutoUpdateFile(string directory)
{
    private readonly string path = Path.Combine(directory, "auto-update.json");

    internal AutoUpdateSettings? Read()
    {
        StateFile.EnsureDirectory(directory);
        StateFile.RejectLink(path);
        try
        {
            using var stream = File.OpenRead(path);
            StateFile.RequirePrivateFile(stream, "Auto-update settings must have mode 0600.");
            if (stream.Length > 4096) throw new InvalidDataException("Invalid auto-update settings.");
            var settings = JsonSerializer.Deserialize(stream, AutoUpdateJson.Default.AutoUpdateSettings);
            return settings is { Version: 1 } ? settings : throw new InvalidDataException("Unsupported auto-update settings version.");
        }
        catch (FileNotFoundException) { return null; }
    }

    internal void Save(bool enabled, DateTimeOffset? attempted = null)
    {
        StateFile.EnsureDirectory(directory);
        if (!enabled) { File.Delete(path); return; }
        StateFile.RejectLink(path);
        StateFile.WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(new AutoUpdateSettings(1, true, attempted), AutoUpdateJson.Default.AutoUpdateSettings));
    }
}
