namespace DevTunnelSvc.Tunnels;

internal static class TextTable
{
    // Columns are left-aligned two spaces apart; the last column is not padded.
    public static string Format(string[] header, IEnumerable<string[]> rows)
    {
        string[][] lines = [header, .. rows];
        var widths = header.Select((_, column) => lines.Max(line => line[column].Length)).ToArray();
        return string.Join(Environment.NewLine,
            lines.Select(line => string.Join("  ", line.Select((cell, column) => cell.PadRight(widths[column]))).TrimEnd()));
    }
}
