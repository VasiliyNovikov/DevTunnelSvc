#!/usr/bin/env dotnet
#:property PublishAot=false
#:property PublishSingleFile=true
#:include FixtureFiles.cs
var root = Environment.GetEnvironmentVariable("DEVTUNNELSVC_INSTALL_FIXTURE") ?? throw new InvalidOperationException("Missing installer fixture.");
var arch = Path.GetFileName(root);
var home = Path.Combine(root, "user home");
var image = Environment.ProcessPath!;
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
            var url when url == $"https://github.com/VasiliyNovikov/DevTunnelSvc/releases/download/v1.2.3/devtunnelsvc-linux-{arch}" => "payload",
            _ => throw new ArgumentException("Unexpected download URL.")
        };
        var destination = Path.GetFullPath(args[index + 1]);
        if (!destination.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Download escaped the fixture.");
        File.Copy(Path.Combine(root, source), destination);
        return 0;
    case "systemctl":
        throw new InvalidOperationException("Unexpected systemctl call.");
}

var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
if (!image.StartsWith(home + Path.DirectorySeparatorChar, pathComparison)) throw new InvalidOperationException("Ran a CLI from PATH instead of the download.");
File.AppendAllText(Path.Combine(root, "calls"), string.Join(' ', args) + "\n");
if (args is ["--version"]) { Console.WriteLine("1.2.3"); return 0; }
var target = FixtureFiles.InstalledCli(home);
if (!string.Equals(image, target, pathComparison))
    throw new InvalidOperationException("Did not run the CLI from the isolated install directory.");
switch (args)
{
    case ["service", "install"]: return 0;
    case ["service", "start"]:
        if (File.Exists(Path.Combine(root, "refuse-start"))) { Console.Error.WriteLine("Fixture startup failed."); return 7; }
        Console.WriteLine(File.Exists(Path.Combine(root, "signed")) ? "Service running; signed in." : "Service running; signed out.");
        return 0;
    case ["login"]: File.WriteAllText(Path.Combine(root, "signed"), "test"); return 0;
    default: throw new ArgumentException("Unexpected fixture CLI command.");
}
