using System.Globalization;
using System.Xml.Linq;

internal static class Repository
{
    internal const string BaselineFile = "code-metrics-baseline.props";
    private static readonly Lazy<Dictionary<string, string>> thresholds = new(() =>
        XElement.Load(Path.Combine(Root, BaselineFile)).Elements("PropertyGroup").Elements()
            .ToDictionary(p => p.Name.LocalName, p => p.Value, StringComparer.Ordinal));

    internal static string Root => Directory.GetParent((string)AppContext.GetData("EntryPointFileDirectoryPath")!)!.FullName;

    internal static int Threshold(string name, int max = int.MaxValue)
    {
        if (!thresholds.Value.TryGetValue(name, out var text) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 1 || value > max)
            throw new InvalidDataException($"{BaselineFile}: {name} must be an integer from 1 to {max}.");
        return value;
    }

    internal static string ReportPath(string gate) =>
        Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "artifacts", gate)).FullName, "report.json");
}
