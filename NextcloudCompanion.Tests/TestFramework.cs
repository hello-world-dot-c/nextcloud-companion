using System.Reflection;

namespace NextcloudCompanion.Tests;

/// <summary>Marks a test method. The same name as xunit's attribute, so moving to xunit later is a package change.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute;

public sealed class AssertionFailed(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new AssertionFailed(message ?? "expected true");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new AssertionFailed(message ?? "expected false");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionFailed($"{message ?? "values differ"}: expected <{expected}>, actual <{actual}>");
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null) throw new AssertionFailed($"{message ?? "expected null"}: <{value}>");
    }

    public static void NotNull(object? value, string? message = null)
    {
        if (value is null) throw new AssertionFailed(message ?? "expected a value");
    }

    public static void Contains(string expectedPart, string actual, string? message = null)
    {
        if (actual is null || !actual.Contains(expectedPart, StringComparison.Ordinal))
            throw new AssertionFailed($"{message ?? "text does not contain"}: <{expectedPart}> not in <{actual}>");
    }

    public static void DoesNotContain(string unexpectedPart, string actual, string? message = null)
    {
        if (actual is not null && actual.Contains(unexpectedPart, StringComparison.Ordinal))
            throw new AssertionFailed($"{message ?? "text contains"}: <{unexpectedPart}> in <{actual}>");
    }
}

public static class TestRunner
{
    public static int Main(string[] args)
    {
        string? filter = args.FirstOrDefault();
        int passed = 0, failed = 0;
        var failures = new List<string>();

        foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => m.GetCustomAttribute<FactAttribute>() is not null))
            {
                string name = $"{type.Name}.{method.Name}";
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                object? instance = Activator.CreateInstance(type);
                try
                {
                    var result = method.Invoke(instance, null);
                    if (result is Task task)
                        task.GetAwaiter().GetResult();
                    passed++;
                    Console.WriteLine($"  pass  {name}");
                }
                catch (Exception ex)
                {
                    failed++;
                    var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                    string text = inner is AssertionFailed ? inner.Message : inner.ToString();
                    failures.Add($"{name}: {text}");
                    Console.WriteLine($"  FAIL  {name}\n        {text.Replace("\n", "\n        ")}");
                }
                finally
                {
                    (instance as IDisposable)?.Dispose();
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
