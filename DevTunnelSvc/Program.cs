using System.Reflection;

namespace DevTunnelSvc;

internal static class Program
{
    private const string Usage = "Usage: DevTunnelSvc [-h|--help|--version]";

    private static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is [] or ["-h"] or ["--help"])
        {
            output.WriteLine(Usage);
            return 0;
        }

        if (args is ["--version"])
        {
            output.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
            return 0;
        }

        error.WriteLine($"Invalid arguments. {Usage}");
        return 1;
    }
}
