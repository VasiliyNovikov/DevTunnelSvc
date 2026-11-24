#:property PublishAot=false
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

if (args is not (["coverage"] or ["mutation"]))
{
    Console.Error.WriteLine("Usage: dotnet eng/quality.cs <coverage|mutation>");
    return 2;
}

var root = Directory.GetParent((string)AppContext.GetData("EntryPointFileDirectoryPath")!)!.FullName;
var restore = Dotnet(root, "tool", "restore");
if (restore != 0)
    return restore;

return args[0] == "coverage" ? Coverage(root) : Mutation(root);

static int Coverage(string root)
{
    var output = Directory.CreateDirectory(Path.Combine(root, "artifacts", "coverage")).FullName;
    var cobertura = Path.Combine(output, "coverage.cobertura.xml");
    var crap = Path.Combine(output, "crap.json");
    File.Delete(cobertura);
    File.Delete(crap);

    var tests = Dotnet(root, "test", "DevTunnelSvc.slnx", "-c", "Release", "-p:CollectCoverage=true");
    if (tests != 0)
        return tests;

    var coverage = XDocument.Load(cobertura).Root!;
    if (!coverage.Descendants("package").Any(p => (string?)p.Attribute("name") == "DevTunnelSvc" &&
        p.Descendants("line").Any(line => (int)line.Attribute("hits")! > 0)))
        throw new InvalidDataException("Cobertura did not measure the production assembly.");

    var result = Dotnet(root, "tool", "run", "dotnet-crap", "--", "analyze",
        Path.Combine("DevTunnelSvc", "DevTunnelSvc.csproj"), "--coverage", cobertura,
        "--threshold", "25", "--output", crap);
    if (result != 0)
        return result;

    using var report = JsonDocument.Parse(File.ReadAllBytes(crap));
    if (!report.RootElement.GetProperty("methods").EnumerateArray().Any(m => m.GetProperty("coverage").GetDouble() > 0))
        throw new InvalidDataException("CRAP analysis did not match coverage to any production method.");
    return 0;
}

static int Mutation(string root)
{
    var output = Path.Combine(root, "artifacts", "mutation");
    var json = Path.Combine(output, "reports", "mutation-report.json");
    var html = Path.Combine(output, "reports", "mutation-report.html");
    Directory.CreateDirectory(Path.GetDirectoryName(json)!);
    File.Delete(json);
    File.Delete(html);

    var tests = Dotnet(Path.Combine(root, "DevTunnelSvc.Tests"), "tool", "run", "dotnet-stryker", "--",
        "--skip-version-check", "--output", output);
    if (tests != 0)
        return tests;

    if (new FileInfo(html).Length == 0)
        throw new InvalidDataException("Stryker produced an empty HTML report.");
    using var report = JsonDocument.Parse(File.ReadAllBytes(json));
    var totalKilled = 0;
    foreach (var file in report.RootElement.GetProperty("files").EnumerateObject())
    {
        var statuses = file.Value.GetProperty("mutants").EnumerateArray()
            .Select(m => m.GetProperty("status").GetString()).ToArray();
        if (statuses.Any(s => s is not ("Killed" or "Survived" or "NoCoverage" or "Timeout" or "Ignored" or "CompileError" or "RuntimeError")))
            throw new InvalidDataException($"Unrecognized mutant status in {file.Name}.");
        var killed = statuses.Count(s => s == "Killed");
        if (killed == 0 && statuses.Any(s => s is "Survived" or "NoCoverage" or "Timeout"))
            throw new InvalidDataException($"No mutant was killed in {file.Name}.");
        totalKilled += killed;
    }

    if (totalKilled == 0)
        throw new InvalidDataException("No production mutant was killed; check mutation scope and test discovery.");
    return 0;
}

static int Dotnet(string directory, params string[] arguments)
{
    using var process = Process.Start(new ProcessStartInfo("dotnet", arguments) { WorkingDirectory = directory })
        ?? throw new InvalidOperationException("Could not start the .NET CLI.");
    process.WaitForExit();
    return process.ExitCode;
}
