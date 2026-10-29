using System.Reflection;

namespace Pmx2Vrm.Tests;

/// <summary>A tiny test runner: every public static void method named Test* in this assembly is a test.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(mi => mi.Name.StartsWith("Test") && mi.GetParameters().Length == 0 && mi.ReturnType == typeof(void))
            .Where(mi => args.Length == 0 || args.Any(a => mi.Name.Contains(a, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(mi => mi.DeclaringType!.Name).ThenBy(mi => mi.MetadataToken)
            .ToList();
        int failed = 0;
        foreach (var t in tests)
        {
            try
            {
                t.Invoke(null, null);
                Console.WriteLine($"PASS {t.DeclaringType!.Name}.{t.Name}");
            }
            catch (TargetInvocationException e)
            {
                failed++;
                Console.WriteLine($"FAIL {t.DeclaringType!.Name}.{t.Name}: {e.InnerException?.Message}");
                Console.WriteLine(e.InnerException?.StackTrace);
            }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool cond, string message)
    {
        if (!cond) throw new Exception(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    public static void Near(double expected, double actual, double tol, string what)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tol)
            throw new Exception($"{what}: expected {expected} (+-{tol}), got {actual}");
    }
}
