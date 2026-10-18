namespace Murmur.Core.Tests.Fakes;

internal static class Allocations
{
    /// <summary>
    /// Bytes allocated on the current thread by <paramref name="body"/> in steady state: the minimum over several rounds,
    /// so one-off runtime work on the test thread (tiered-JIT call counting, type loading) is not blamed on the code
    /// under test. Code that allocates on every call never reaches 0.
    /// </summary>
    public static long SteadyState(Action body, int rounds = 10)
    {
        body();
        var min = long.MaxValue;
        for (var r = 0; r < rounds && min != 0; r++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            body();
            min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            if (min != 0)
            {
                Thread.Sleep(50); // let background tier-1 compilation finish
            }
        }

        return min;
    }
}
