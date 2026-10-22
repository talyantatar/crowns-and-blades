// Minimal headless test runner shared by every bridge component (no test framework: offline build policy).
//
// How to add tests for a component:
//   1. Create CK3xBL.SelfTest/<Component>Tests.cs with `internal static class <Component>Tests { public static void Register(TestRegistry r) { ... } }`
//      and add each test with r.Add("<component>.<name>", () => { ... }) (sync) or r.Add("...", async () => { ... }) (async).
//   2. Add ONE line to Program.cs: `<Component>Tests.Register(registry);`
// Use Expect.* for assertions; a test fails by throwing. `dotnet run --project CK3xBL.SelfTest -- <filter>` runs only the tests whose
// name contains <filter> (case-insensitive); the exit code is the number of failures (0 = all passed).
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CK3xBL.SelfTest;

public sealed class TestRegistry
{
    private readonly List<(string Name, Func<Task> Run)> _tests = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string name, Action test) => Add(name, () => { test(); return Task.CompletedTask; });

    public void Add(string name, Func<Task> test)
    {
        if (!_names.Add(name)) throw new InvalidOperationException($"Duplicate test name '{name}'.");
        _tests.Add((name, test));
    }

    public int Count => _tests.Count;

    /// <summary>Runs every registered test (or those matching any filter argument). Returns the number of failures.</summary>
    public async Task<int> RunAsync(string[] filters, TextWriter? output = null)
    {
        output ??= Console.Out;
        var selected = _tests.Where(t => filters.Length == 0 || filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        int failed = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, run) in selected)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var task = run();
                // A hung async test must not hang the whole run.
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) != task)
                    throw new TimeoutException("test did not finish within 30 s");
                await task;
                output.WriteLine($"PASS  {name}  ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                failed++;
                output.WriteLine($"FAIL  {name}  ({sw.ElapsedMilliseconds} ms)");
                output.WriteLine("      " + e.GetType().Name + ": " + e.Message.Replace("\n", "\n      "));
                if (e is not ExpectationException && e.StackTrace is { } st)
                    output.WriteLine("      " + st.Replace("\n", "\n      "));
            }
        }
        output.WriteLine($"{selected.Count - failed}/{selected.Count} passed, {failed} failed ({total.ElapsedMilliseconds} ms)");
        return failed;
    }
}

public sealed class ExpectationException(string message) : Exception(message);

/// <summary>Assertions for self tests. Each throws <see cref="ExpectationException"/> with the caller's expression on failure.</summary>
public static class Expect
{
    public static void True(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new ExpectationException(message ?? $"expected true: {expr}");
    }

    public static void False(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition) throw new ExpectationException(message ?? $"expected false: {expr}");
    }

    public static void Equal<T>(T expected, T actual, string? message = null, [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new ExpectationException($"{message ?? expr}: expected <{expected}> but was <{actual}>");
    }

    public static void Near(double expected, double actual, double tolerance, string? message = null,
        [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new ExpectationException($"{message ?? expr}: expected {expected} ± {tolerance} but was {actual}");
    }

    public static T NotNull<T>(T? value, string? message = null, [CallerArgumentExpression(nameof(value))] string? expr = null) where T : class
        => value ?? throw new ExpectationException(message ?? $"expected non-null: {expr}");

    public static void Null(object? value, string? message = null, [CallerArgumentExpression(nameof(value))] string? expr = null)
    {
        if (value is not null) throw new ExpectationException(message ?? $"expected null: {expr} was <{value}>");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null,
        [CallerArgumentExpression(nameof(actual))] string? expr = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new ExpectationException($"{message ?? expr}: expected [{string.Join(", ", e)}] but was [{string.Join(", ", a)}]");
    }

    public static TException Throws<TException>(Action action, [CallerArgumentExpression(nameof(action))] string? expr = null)
        where TException : Exception
    {
        try { action(); }
        catch (TException e) { return e; }
        catch (Exception e) { throw new ExpectationException($"{expr}: expected {typeof(TException).Name} but got {e.GetType().Name}: {e.Message}"); }
        throw new ExpectationException($"{expr}: expected {typeof(TException).Name} but nothing was thrown");
    }
}
