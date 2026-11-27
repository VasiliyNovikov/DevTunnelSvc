#!/usr/bin/env dotnet
#:property PublishAot=false
#:include Repository.cs
using System.Diagnostics;
using System.Text.Json;

var fixture = Directory.CreateTempSubdirectory("devtunnel-duplication-");
try
{
    var eng = fixture.CreateSubdirectory("eng").FullName;
    foreach (var props in new[] { "Directory.Build.props", "Directory.Packages.props" })
        File.Copy(Path.Combine(Repository.Root, props), Path.Combine(fixture.FullName, props));
    foreach (var source in new[] { "duplication.cs", "Repository.cs" })
        File.Copy(Path.Combine(Repository.Root, "eng", source), Path.Combine(eng, source));

    const string imports = "using A.B.C.D.E.F.G.H.I.J.K.L.M;\n";
    const string call = "Probe(1,2,3,4,5,6,7,8,9,10,11);"; // Exactly 25 tokens.
    var product = fixture.CreateSubdirectory("DevTunnelSvc");
    File.WriteAllText(Path.Combine(product.FullName, "Original.cs"), imports + call);
    File.WriteAllText(Path.Combine(product.CreateSubdirectory("obj").FullName, "Generated.cs"), call);
    var tests = Path.Combine(fixture.CreateSubdirectory("DevTunnelSvc.Tests").FullName, "Conditional.cs");
    File.WriteAllText(tests, $"#if WINDOWS\n{imports}{call.Replace("6,7", "6, /* split */\n7", StringComparison.Ordinal)}\n#endif");
    var script = Path.Combine(eng, "Example.cs");
    File.WriteAllText(script, call + "\n" + call);

    var clones = RunGate(1).EnumerateArray().ToArray();
    if (clones is not [var clone] || clone.GetProperty("tokens").GetInt32() != 25)
        throw new InvalidDataException("Expected one 25-token clone.");
    var places = clone.GetProperty("copies").EnumerateArray().Select(c => $"{c.GetProperty("file")}:{c.GetProperty("startLine")}-{c.GetProperty("endLine")}");
    if (!places.Order(StringComparer.Ordinal).SequenceEqual(["DevTunnelSvc.Tests/Conditional.cs:3-4", "DevTunnelSvc/Original.cs:2-2", "eng/Example.cs:1-1", "eng/Example.cs:2-2"]))
        throw new InvalidDataException("The duplication report did not locate all four copies.");

    File.WriteAllText(tests, imports + "Other" + call["Probe".Length..]); // Only the last 24 tokens match.
    File.WriteAllText(script, new string(';', 30)); // Matching windows here overlap.
    if (RunGate(0).GetArrayLength() != 0) throw new InvalidDataException("Expected an empty report after removing the copies.");

    JsonElement RunGate(int expected)
    {
        var result = Process.Run(new ProcessStartInfo("dotnet", [Path.Combine(eng, "duplication.cs")]) { WorkingDirectory = fixture.FullName });
        if (result.ExitCode != expected) throw new InvalidDataException($"Duplication gate exited with {result.ExitCode}, expected {expected}.");
        return JsonElement.Parse(File.ReadAllBytes(Path.Combine(fixture.FullName, "artifacts", "duplication", "report.json")));
    }
}
finally { fixture.Delete(true); }
