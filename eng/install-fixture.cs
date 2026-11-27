#!/usr/bin/env dotnet
#:property PublishAot=false
#:property PublishSingleFile=true
#:include FixtureFiles.cs
var root = Environment.GetEnvironmentVariable("DEVTUNNELSVC_INSTALL_FIXTURE") ?? throw new InvalidOperationException("Missing installer fixture.");
var arch = Path.GetFileName(root);
var version = File.ReadAllText(Path.Combine(root, "version"));
var home = Path.Combine(root, "user home");
var image = Environment.ProcessPath!;
const string fixtureUid = "1001";
void Record(string call) => File.AppendAllText(Path.Combine(root, "calls"), call + "\n");
switch (Path.GetFileNameWithoutExtension(image))
{
    case "dotnet":
        throw new InvalidOperationException("The bootstrap installer must not invoke dotnet.");
    case "uname":
        Console.WriteLine((args, arch) switch
        {
            (["-s"], _) => "Linux",
            (["-m"], "x64") => "x86_64",
            (["-m"], "arm64") => "aarch64",
            _ => throw new ArgumentException("Unexpected uname query.")
        });
        return 0;
    case "curl":
        var index = Array.IndexOf(args, "--output");
        if (index < 0 || index + 2 >= args.Length) throw new ArgumentException("Expected a curl output file and URL.");
        var source = args[^1] switch
        {
            "https://api.github.com/repos/VasiliyNovikov/DevTunnelSvc/releases/latest" => "release.json",
            var url when url == $"https://github.com/VasiliyNovikov/DevTunnelSvc/releases/download/v{version}/devtunnelsvc-linux-{arch}" => "payload",
            _ => throw new ArgumentException("Unexpected download URL.")
        };
        var destination = Path.GetFullPath(args[index + 1]);
        if (!destination.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Download escaped the fixture.");
        File.Copy(Path.Combine(root, source), destination);
        return 0;
    case "id":
        if (args is not ["-u"]) throw new ArgumentException("Expected a query for the invoking UID.");
        Console.WriteLine(fixtureUid);
        return 0;
    case "loginctl":
        var lingering = Path.Combine(root, "lingering");
        if (args is ["show-user", fixtureUid, "--property=Linger", "--value"])
        {
            Console.WriteLine(File.Exists(lingering) ? "yes" : "no");
            return 0;
        }
        if (args is not ["--no-ask-password", "enable-linger", fixtureUid]) throw new ArgumentException("Unexpected loginctl call.");
        Record("enable-linger");
        if (File.Exists(Path.Combine(root, "refuse-linger"))) return 1;
        File.WriteAllText(lingering, "yes");
        return 0;
    case "sudo" or "systemctl":
        throw new InvalidOperationException($"Unexpected installer call to {Path.GetFileName(image)}.");
}

var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
if (!image.StartsWith(home + Path.DirectorySeparatorChar, pathComparison)) throw new InvalidOperationException("Ran a CLI from PATH instead of the download.");
Record(string.Join(' ', args));
if (args is ["--version"]) { Console.WriteLine(version); return 0; }
var target = FixtureFiles.InstalledCli(home);
if (!string.Equals(image, target, pathComparison))
    throw new InvalidOperationException("Did not run the CLI from the isolated install directory.");
switch (args)
{
    case ["install"] when version != "0.1.16":
    case ["service", "install"] when version == "0.1.16": return 0;
    case ["start"] when version != "0.1.16":
    case ["service", "start"] when version == "0.1.16":
        if (File.Exists(Path.Combine(root, "refuse-start"))) { Console.Error.WriteLine("Fixture startup failed."); return 7; }
        Console.WriteLine(File.Exists(Path.Combine(root, "signed")) ? "Service running; signed in." : "Service running; signed out.");
        return 0;
    case ["login"]: File.WriteAllText(Path.Combine(root, "signed"), "test"); return 0;
    default: throw new ArgumentException("Unexpected fixture CLI command.");
}
