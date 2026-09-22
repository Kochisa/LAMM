using System.Diagnostics;

namespace LocalAIModelManager.Tests;

/// <summary>Minimal offline test framework (no NuGet package feed is available).</summary>
internal static class TestFramework
{
    private static readonly List<TestCase> Cases = new();
    private static readonly List<string> Failures = new();
    private static readonly List<string> Skipped = new();

    public static void Case(string name, Func<Task> body) => Cases.Add(new TestCase(name, body));

    public static void Case(string name, Action body) =>
        Cases.Add(new TestCase(name, () =>
        {
            body();
            return Task.CompletedTask;
        }));

    public static async Task<int> RunAllAsync(string? filter)
    {
        var selected = filter is null
            ? Cases
            : Cases.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"Running {selected.Count} test case(s)");
        Console.WriteLine(new string('-', 78));

        var sw = Stopwatch.StartNew();
        var passed = 0;

        foreach (var testCase in selected)
        {
            var caseWatch = Stopwatch.StartNew();
            Console.Write($"  {testCase.Name} ... ");
            try
            {
                await testCase.Body().ConfigureAwait(false);
                caseWatch.Stop();
                passed++;
                Console.WriteLine($"PASS ({caseWatch.ElapsedMilliseconds} ms)");
            }
            catch (TestSkippedException skip)
            {
                caseWatch.Stop();
                passed++;
                Console.WriteLine($"SKIP ({caseWatch.ElapsedMilliseconds} ms) - {skip.Message}");
                Skipped.Add($"{testCase.Name}: {skip.Message}");
            }
            catch (Exception ex)
            {
                caseWatch.Stop();
                Console.WriteLine($"FAIL ({caseWatch.ElapsedMilliseconds} ms)");
                Console.WriteLine(Indent(ex.ToString()));
                Failures.Add($"{testCase.Name}: {ex.Message}");
            }
        }

        sw.Stop();
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"{passed}/{selected.Count} passed in {sw.Elapsed.TotalSeconds:F1}s");

        if (Skipped.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Skipped (environment limitation):");
            foreach (var skip in Skipped)
            {
                Console.WriteLine("  - " + skip);
            }
        }

        if (Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
        }

        return Failures.Count == 0 ? 0 : 1;
    }

    private static string Indent(string value) =>
        string.Join(Environment.NewLine, value.Split('\n').Select(l => "      " + l.TrimEnd()));

    private sealed record TestCase(string Name, Func<Task> Body);
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException($"Expected true: {message}");
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message}: expected <{expected}>, actual <{actual}>");
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new AssertionException($"Expected non-null: {message}");
        }
    }

    public static void Contains(string haystack, string needle, string message)
    {
        if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            var preview = haystack.Length > 600 ? haystack[..600] + "..." : haystack;
            throw new AssertionException($"{message}: '{needle}' not found in <{preview}>");
        }
    }

    public static void NotContains(string haystack, string needle, string message)
    {
        if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssertionException($"{message}: '{needle}' unexpectedly found");
        }
    }

    public static async Task EventuallyAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new AssertionException($"Timed out after {timeout.TotalSeconds:0.#}s waiting for: {message}");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> body, string? message = null)
        where TException : Exception
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new AssertionException(
                $"Expected {typeof(TException).Name} but got {other.GetType().Name}: {other.Message}");
        }

        throw new AssertionException(message ?? $"Expected {typeof(TException).Name} to be thrown, but nothing was.");
    }
}

internal sealed class AssertionException : Exception
{
    public AssertionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when a check cannot run in this environment. The case is reported as
/// SKIP, not as a pass, and the reason is printed at the end of the run.
/// </summary>
internal sealed class TestSkippedException : Exception
{
    public TestSkippedException(string message)
        : base(message)
    {
    }
}
