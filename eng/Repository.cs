internal static class Repository
{
    internal static string Root => Directory.GetParent((string)AppContext.GetData("EntryPointFileDirectoryPath")!)!.FullName;

    internal static string ReportPath(string gate) =>
        Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "artifacts", gate)).FullName, "report.json");
}
