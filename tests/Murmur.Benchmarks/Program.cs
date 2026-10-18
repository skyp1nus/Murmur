using BenchmarkDotNet.Running;

namespace Murmur.Benchmarks;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(WithInProcessToolchain(args));

    /// <summary>
    /// BenchmarkDotNet 0.15.8 does not recognise net11.0 and fails validation when it generates
    /// a child project ("GetRuntimeVersion not implemented for NotRecognized"), so benchmarks run
    /// in-process by default. Drop this once BenchmarkDotNet knows net11.0.
    /// </summary>
    internal static string[] WithInProcessToolchain(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--inProcess", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-i", StringComparison.Ordinal))
            {
                return args;
            }
        }

        return [.. args, "--inProcess"];
    }
}
