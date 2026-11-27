#!/usr/bin/env dotnet
#:property PublishAot=false
#:include Repository.cs
#:package Microsoft.CodeAnalysis.AnalyzerUtilities
#:package Microsoft.CodeAnalysis.CSharp.Workspaces
#:package Microsoft.CodeAnalysis.Workspaces.MSBuild
using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeMetrics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

var projectFile = Path.Combine(Repository.Root, "DevTunnelSvc", "DevTunnelSvc.csproj");
var report = Repository.ReportPath("metrics");
File.Delete(report);
var maxSourceLines = Repository.Threshold("maxMethodSourceLines");
var maxExecutableLines = Repository.Threshold("maxMethodExecutableLines");

// Also enforce the four SDK analyzer thresholds when this check runs outside CI.
var build = await Process.RunAsync("dotnet", ["build", projectFile, "-c", "Release"]);
if (build.ExitCode != 0)
    return build.ExitCode;

using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["Configuration"] = "Release" });
var project = await workspace.OpenProjectAsync(projectFile);
var compilation = await project.GetCompilationAsync() ?? throw new InvalidDataException("No production compilation.");
var errors = workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure).Select(d => d.Message)
    .Concat(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())).ToArray();
if (errors.Length != 0)
    throw new InvalidDataException(string.Join(Environment.NewLine, errors));

var sources = project.Documents.Select(d => d.FilePath).ToHashSet(StringComparer.Ordinal);
var tree = await CodeAnalysisMetricData.ComputeAsync(new CodeMetricsAnalysisContext(compilation, CancellationToken.None));
var symbols = Walk(tree).Where(m => !m.Symbol.IsImplicitlyDeclared && m.Symbol.DeclaringSyntaxReferences.Any(r =>
    sources.Contains(r.SyntaxTree.FilePath) &&
    compilation.Options.SyntaxTreeOptionsProvider?.IsGenerated(r.SyntaxTree, CancellationToken.None) != GeneratedKind.MarkedGenerated &&
    // A primary constructor's reported source span is the entire type.
    !(m.Symbol is IMethodSymbol && r.GetSyntax() is TypeDeclarationSyntax))).ToArray();
var types = symbols.Where(m => m.Symbol is INamedTypeSymbol)
    .Select(m => new { Name = m.Symbol.ToDisplayString(), Coupling = m.CoupledNamedTypes.Count, m.DepthOfInheritance }).ToArray();
var methods = symbols.Where(m => m.Symbol is IMethodSymbol)
    .Select(m => new { Name = m.Symbol.ToDisplayString(), m.CyclomaticComplexity, m.MaintainabilityIndex, m.SourceLines, m.ExecutableLines }).ToArray();
if (types.Length == 0 || methods.Length == 0)
    throw new InvalidDataException("No production types or methods were measured.");

File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new { types, methods }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Measured {types.Length} types and {methods.Length} methods. See {report}.");
var failures = methods.Where(m => m.SourceLines > maxSourceLines || m.ExecutableLines > maxExecutableLines).ToArray();
foreach (var method in failures)
    Console.Error.WriteLine($"{method.Name}: {method.SourceLines}/{maxSourceLines} source lines, {method.ExecutableLines}/{maxExecutableLines} executable lines.");
return failures.Length == 0 ? 0 : 1;

static IEnumerable<CodeAnalysisMetricData> Walk(CodeAnalysisMetricData node)
{
    yield return node;
    foreach (var descendant in node.Children.SelectMany(Walk))
        yield return descendant;
}
