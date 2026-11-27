using System.Text.Json;
using System.Text.Json.Serialization;
using DevTunnelSvc.Tunnels;

namespace DevTunnelSvc.Service;

internal sealed record SavedMapping(string Kind, string Tunnel, int TunnelPort, int LocalPort)
{
    internal static SavedMapping From(Mapping map) => new(map.Host ? "host" : "connect", map.Tunnel.ToString(), map.TunnelPort, map.LocalPort);

    internal Mapping ToMapping() => Mapping.Create(Kind switch
    {
        "host" => true,
        "connect" => false,
        _ => throw new InvalidDataException("Unknown saved mapping kind.")
    }, Tunnel, TunnelPort, LocalPort);
}

internal sealed record SavedMappings(int Version, SavedMapping[] Mappings);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SavedMappings))]
internal sealed partial class MappingJson : JsonSerializerContext;

internal sealed class MappingFile(string stateDirectory)
{
    private const int Version = 1, MaxBytes = 256 * 1024;
    private readonly string path = Path.Combine(stateDirectory, "mappings.json");

    internal Mapping[] Read()
    {
        StateFile.EnsureDirectory(stateDirectory);
        StateFile.RejectLink(path);
        try
        {
            using var file = StateFile.OpenSnapshot(path);
            StateFile.RequirePrivateFile(file, "Saved mappings must have mode 0600.");
            if (file.Length > MaxBytes) throw new InvalidDataException("Saved mappings exceed the storage limit.");
            return Decode(JsonSerializer.Deserialize(file, MappingJson.Default.SavedMappings));
        }
        catch (FileNotFoundException) { return []; }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"Cannot restore mappings from {path}: {ex.Message}", ex);
        }
    }

    private static Mapping[] Decode(SavedMappings? saved)
    {
        if (saved?.Version != Version) throw new InvalidDataException("Unsupported mapping file version.");
        var maps = saved.Mappings.Select(entry => entry?.ToMapping() ?? throw new InvalidDataException("Empty mapping entry.")).ToArray();
        if (maps.DistinctBy(m => m.Name).Count() != maps.Length) throw new InvalidDataException("Duplicate saved mapping name.");
        return maps;
    }

    internal void Save(IEnumerable<Mapping> mappings)
    {
        StateFile.EnsureDirectory(stateDirectory);
        var saved = new SavedMappings(Version, [.. mappings.Select(SavedMapping.From)]);
        if (saved.Mappings.Length == 0)
        {
            File.Delete(path);
            File.Delete(path + ".pending");
            return;
        }
        StateFile.RejectLink(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, MappingJson.Default.SavedMappings);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Saved mappings exceed the storage limit.");
        StateFile.WriteAtomically(path, bytes);
    }
}
