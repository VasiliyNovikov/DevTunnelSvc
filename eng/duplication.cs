#!/usr/bin/env dotnet
#:property PublishAot=false
#:include Repository.cs
#:package Microsoft.CodeAnalysis.CSharp
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

const int minCloneTokens = 25;
var root = Repository.Root;
var reportPath = Repository.ReportPath("duplication");
File.Delete(reportPath);

var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithPreprocessorSymbols("WINDOWS");
var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
var sources = SourceFiles(new DirectoryInfo(root)).Select(ReadSource).OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
if (vocabulary.Count == 0)
    throw new InvalidDataException("No C# tokens found to check for duplication.");

var windows = new Dictionary<ReadOnlyMemory<int>, List<(int File, int Start)>>(new TokenComparer());
var clones = new Dictionary<ReadOnlyMemory<int>, HashSet<(int File, int Start)>>(new TokenComparer());
for (var file = 0; file < sources.Length; file++)
{
    var tokens = sources[file].Ids;
    for (var start = 0; start <= tokens.Length - minCloneTokens; start++)
    {
        var window = tokens.AsMemory(start, minCloneTokens);
        if (!windows.TryGetValue(window, out var previous))
            windows.Add(window, previous = []);
        foreach (var (otherFile, otherStart) in previous)
        {
            var other = sources[otherFile].Ids;
            var limit = Math.Min(tokens.Length - start, other.Length - otherStart);
            if (otherFile == file)
                limit = Math.Min(limit, start - otherStart);
            // Report only non-overlapping copies, once at the beginning of each maximal match.
            if (limit < minCloneTokens || (start > 0 && otherStart > 0 && tokens[start - 1] == other[otherStart - 1]))
                continue;

            var length = minCloneTokens;
            while (length < limit && tokens[start + length] == other[otherStart + length])
                length++;
            var match = tokens.AsMemory(start, length);
            if (!clones.TryGetValue(match, out var copies))
                clones.Add(match, copies = []);
            copies.Add((otherFile, otherStart));
            copies.Add((file, start));
        }
        previous.Add((file, start));
    }
}

var report = clones.Select(group => new
{
    tokens = group.Key.Length,
    copies = group.Value.Order().Select(copy => sources[copy.File].Location(copy.Start, group.Key.Length)).ToArray()
}).OrderByDescending(clone => clone.tokens * (clone.copies.Length - 1))
    .ThenBy(clone => clone.copies[0].File, StringComparer.Ordinal).ThenBy(clone => clone.copies[0].StartLine).ToArray();
File.WriteAllBytes(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
}));
foreach (var clone in report)
    Console.Error.WriteLine($"{clone.tokens} tokens: {string.Join(", ", clone.copies.Select(c => $"{c.File}:{c.StartLine}-{c.EndLine}"))}");
var covered = sources.Select(source => new bool[source.Ids.Length]).ToArray();
foreach (var (sequence, copies) in clones)
    foreach (var (file, start) in copies)
        Array.Fill(covered[file], true, start, sequence.Length);
Console.WriteLine($"Duplicated tokens: {covered.Sum(file => file.Count(hit => hit))} of {sources.Sum(source => source.Ids.Length)} " +
    $"in {sources.Length} C# files; clone groups of {minCloneTokens}+ tokens: {report.Length}. See {reportPath}.");
return report.Length == 0 ? 0 : 1;

Source ReadSource(FileInfo file)
{
    var path = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file.FullName), parse, path);
    var tokens = tree.GetRoot().DescendantTokens()
        .Where(t => !t.IsKind(SyntaxKind.EndOfFileToken) && t.Parent?.FirstAncestorOrSelf<UsingDirectiveSyntax>() is null).ToArray();
    return new Source(path, tokens, tokens.Select(t =>
    {
        if (!vocabulary.TryGetValue(t.Text, out var id))
            vocabulary.Add(t.Text, id = vocabulary.Count);
        return id;
    }).ToArray());
}

static IEnumerable<FileInfo> SourceFiles(DirectoryInfo directory)
{
    foreach (var file in directory.EnumerateFiles("*.cs"))
        if (!file.Name.StartsWith('.')) yield return file;
    foreach (var child in directory.EnumerateDirectories())
    {
        if (child.Name.StartsWith('.') || child.Name is "bin" or "obj" or "artifacts" or "StrykerOutput" or "TestResults" ||
            child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            continue;
        foreach (var file in SourceFiles(child))
            yield return file;
    }
}

sealed record Source(string Path, SyntaxToken[] Tokens, int[] Ids)
{
    public Copy Location(int start, int length) => new(Path,
        Tokens[start].GetLocation().GetLineSpan().StartLinePosition.Line + 1,
        Tokens[start + length - 1].GetLocation().GetLineSpan().EndLinePosition.Line + 1);
}

sealed record Copy(string File, int StartLine, int EndLine);

sealed class TokenComparer : IEqualityComparer<ReadOnlyMemory<int>>
{
    public bool Equals(ReadOnlyMemory<int> x, ReadOnlyMemory<int> y) => x.Span.SequenceEqual(y.Span);
    public int GetHashCode(ReadOnlyMemory<int> tokens)
    {
        var hash = new HashCode();
        hash.AddBytes(MemoryMarshal.AsBytes(tokens.Span));
        return hash.ToHashCode();
    }
}
