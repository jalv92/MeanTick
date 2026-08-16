// Test harness for MeanTickCore.cs and MeanTickExits.cs — the two pure files that
// carry every decision. No framework: an assert counter and a `Main` that exits
// non-zero, which is all a build gate needs and all anyone has to learn.
//
// Run: dotnet run --project tests
using System;

public static class T
{
    public static int Failures;
    public static int Checks;

    public static void Check(bool ok, string name)
    {
        Checks++;
        if (ok) { Console.WriteLine("  PASS " + name); return; }
        Failures++;
        Console.WriteLine("  FAIL " + name);
    }

    public static void CheckClose(double a, double b, string name, double eps = 1e-9)
    {
        Check(Math.Abs(a - b) <= eps, name + " (" + a.ToString("R") + " vs " + b.ToString("R") + ")");
    }

    // Bit equality, not epsilon: the Python mirror has to reproduce these numbers
    // exactly, so a test that tolerates a last-bit difference tolerates the drift
    // it exists to catch.
    public static void CheckBits(double a, double b, string name)
    {
        Check(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            name + " (" + a.ToString("R") + " vs " + b.ToString("R") + ")");
    }

    public static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        DetectionTests.Run();
        // ExitTests.Run();  // uncommented in Task 6

        Console.WriteLine();
        Console.WriteLine(T.Failures == 0
            ? string.Format("OK  {0} checks", T.Checks)
            : string.Format("FAIL  {0} of {1} checks", T.Failures, T.Checks));
        return T.Failures == 0 ? 0 : 1;
    }
}
