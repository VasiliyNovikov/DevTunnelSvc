// Compiled by Windows PowerShell 5.1's Add-Type against its .NET Framework System.Management.Automation.
using System;
using System.IO;
using System.Management.Automation;
using Microsoft.Win32;

namespace DevTunnelSvc.InstallerTests
{
    internal static class Fixture
    {
        internal static string Root
        {
            get
            {
                var root = Environment.GetEnvironmentVariable("DEVTUNNELSVC_INSTALL_FIXTURE");
                if (String.IsNullOrEmpty(root)) throw new InvalidOperationException("Missing installer fixture.");
                return root;
            }
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    [Cmdlet("Get", "FixtureProcessor"), Alias("Get-CimInstance")]
    public sealed class GetFixtureProcessor : Cmdlet
    {
        [Parameter(Mandatory = true)] public string ClassName { get; set; }
        [Parameter(Mandatory = true)] public string Property { get; set; }
        protected override void ProcessRecord()
        {
            Fixture.Require(ClassName == "Win32_Processor" && Property == "Architecture", "Unexpected hardware query.");
            var cpu = new PSObject();
            var arch = Path.GetFileName(Fixture.Root);
            Fixture.Require(arch == "x64" || arch == "arm64", "Unknown fixture architecture.");
            cpu.Properties.Add(new PSNoteProperty("Architecture", arch == "x64" ? 9 : 12));
            WriteObject(cpu);
        }
    }

    [Cmdlet("Get", "FixtureEnvironment"), Alias("Get-Item")]
    public sealed class GetFixtureEnvironment : Cmdlet
    {
        [Parameter(Mandatory = true)] public string LiteralPath { get; set; }
        protected override void ProcessRecord()
        {
            Fixture.Require(LiteralPath == @"HKCU:\Environment", "Unexpected registry access.");
            WriteObject(new FixtureEnvironmentKey());
        }
    }

    public sealed class FixtureEnvironmentKey
    {
        public string GetValue(string name, string fallback, RegistryValueOptions options)
        {
            Fixture.Require(name == "Path" && options == RegistryValueOptions.DoNotExpandEnvironmentNames, "Expected unexpanded user PATH.");
            var history = File.ReadAllLines(Path.Combine(Fixture.Root, "path-history"));
            return history[history.Length - 1];
        }
        public string[] GetValueNames() { return new[] { "Path" }; }
        public RegistryValueKind GetValueKind(string name) { return RegistryValueKind.ExpandString; }
        public void Close() { }
    }

    [Cmdlet("Set", "FixtureUserPath"), Alias("New-ItemProperty")]
    public sealed class SetFixtureUserPath : Cmdlet
    {
        [Parameter(Mandatory = true)] public string LiteralPath { get; set; }
        [Parameter(Mandatory = true)] public string Name { get; set; }
        [Parameter(Mandatory = true)] public RegistryValueKind PropertyType { get; set; }
        [Parameter(Mandatory = true)] public string Value { get; set; }
        [Parameter] public SwitchParameter Force { get; set; }
        protected override void ProcessRecord()
        {
            Fixture.Require(LiteralPath == @"HKCU:\Environment" && Name == "Path" && PropertyType == RegistryValueKind.ExpandString && Force,
                "Unexpected registry write.");
            File.AppendAllText(Path.Combine(Fixture.Root, "path-history"), Value + "\n");
        }
    }

    public abstract class FixtureWebRequest : PSCmdlet
    {
        [Parameter(Mandatory = true)] public string Uri { get; set; }
        [Parameter] public SwitchParameter UseBasicParsing { get; set; }
    }

    [Cmdlet("Get", "FixtureRelease"), Alias("Invoke-RestMethod")]
    public sealed class GetFixtureRelease : FixtureWebRequest
    {
        protected override void ProcessRecord()
        {
            Fixture.Require(Uri == "https://api.github.com/repos/VasiliyNovikov/DevTunnelSvc/releases/latest", "Unexpected metadata URL.");
            var json = File.ReadAllText(Path.Combine(Fixture.Root, "release.json"));
            using (var parser = PowerShell.Create(RunspaceMode.CurrentRunspace))
            {
                var result = parser.AddCommand("ConvertFrom-Json").AddParameter("InputObject", json).Invoke();
                Fixture.Require(!parser.HadErrors && result.Count == 1, "Invalid release fixture.");
                WriteObject(result[0]);
            }
        }
    }

    [Cmdlet("Copy", "FixtureAsset"), Alias("Invoke-WebRequest")]
    public sealed class CopyFixtureAsset : FixtureWebRequest
    {
        [Parameter(Mandatory = true)] public string OutFile { get; set; }
        protected override void ProcessRecord()
        {
            var root = Fixture.Root;
            var asset = "devtunnelsvc-win-" + Path.GetFileName(root) + ".zip";
            var version = File.ReadAllText(Path.Combine(root, "version"));
            Fixture.Require(Uri == "https://github.com/VasiliyNovikov/DevTunnelSvc/releases/download/v" + version + "/" + asset, "Unexpected package URL.");
            Fixture.Require(Path.GetFullPath(OutFile).StartsWith(Path.Combine(root, "user home") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Download escaped the fixture.");
            File.Copy(Path.Combine(root, "payload"), OutFile);
        }
    }
}
