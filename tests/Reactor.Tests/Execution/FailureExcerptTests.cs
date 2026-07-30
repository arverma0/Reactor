using System.Globalization;
using System.Text;
using Reactor.Execution;

namespace Reactor.Tests.Execution;

public class FailureExcerptTests
{
    private const int Max = FailureExcerpt.DefaultMaxChars;

    [Fact]
    public void Output_that_fits_is_returned_whole()
    {
        Assert.Equal("oops", FailureExcerpt.Extract("  oops \n", ""));
        Assert.Equal("out\nerr", FailureExcerpt.Extract("out\n", "err\n"));
        Assert.Equal("err", FailureExcerpt.Extract(null, "err"));
        Assert.Equal("", FailureExcerpt.Extract(null, null));
    }

    [Fact]
    public void A_failure_in_the_middle_of_a_long_qa_log_is_kept_with_the_end_of_the_log()
    {
        var log = new StringBuilder("> atlas@1.0.0 qa\n> node qa/run.mjs\n\n");
        for (var i = 1; i <= 465; i++)
        {
            log.Append(i == 230
                ? "FAIL  QA-15@390x844  Composition: lit bbox covers 0.62 of the viewport (limit 0.55) in systems/rabinovich-fabrikant\n"
                : string.Create(CultureInfo.InvariantCulture, $"PASS  QA-{i % 40:00}@{(i % 2 == 0 ? "1440x900" : "390x844")}  Composition: lit bbox within limits for systems/attractor-{i:000}, palette dusk ({i % 7 + 3} checks, {i % 50} ms)\n"));
        }

        log.Append("\n464 passed, 1 failed\nQA FAIL — report: qa/report.json\n");
        Assert.True(log.Length > 50_000);

        var excerpt = FailureExcerpt.Extract(log.ToString(), "");

        Assert.True(excerpt.Length <= Max, $"{excerpt.Length} characters");
        Assert.Contains("FAIL  QA-15@390x844  Composition: lit bbox covers 0.62 of the viewport (limit 0.55)", excerpt);
        Assert.EndsWith("464 passed, 1 failed\nQA FAIL — report: qa/report.json", excerpt, StringComparison.Ordinal);
        Assert.StartsWith("…\n", excerpt, StringComparison.Ordinal);
        Assert.True(excerpt.IndexOf("FAIL  QA-15", StringComparison.Ordinal) < excerpt.IndexOf("QA FAIL", StringComparison.Ordinal));
    }

    [Fact]
    public void Vitest_failures_on_stdout_and_npm_noise_on_stderr_are_both_kept()
    {
        var stdout = new StringBuilder("\n RUN  v3.2.4 /work/atlas\n\n");
        for (var i = 0; i < 60; i++)
        {
            stdout.Append(i == 30
                ? " ✓ src/ui/error-boundary.test.tsx > renders Error: fallback when a child throws\n"
                : string.Create(CultureInfo.InvariantCulture, $" ✓ src/model/module-{i:00}.test.ts ({i + 3} tests) {i + 4}ms\n"));
        }

        stdout.Append(" ❯ src/seeds.test.ts (12 tests | 1 failed) 412ms\n");
        stdout.Append("   × rabinovichFabrikant > runs within budget 108ms\n");
        stdout.Append("     → expected 108.53 to be less than 100\n");
        for (var i = 60; i < 120; i++)
        {
            stdout.Append(string.Create(CultureInfo.InvariantCulture, $" ✓ src/render/module-{i:000}.test.ts ({i % 9 + 1} tests) {i}ms\n"));
        }

        stdout.Append("""

            ⎯⎯⎯⎯⎯⎯⎯ Failed Tests 1 ⎯⎯⎯⎯⎯⎯⎯

             FAIL  src/seeds.test.ts > rabinovichFabrikant > runs within budget
            AssertionError: expected 108.53 to be less than 100
             ❯ src/seeds.test.ts:42:7
                 40|   it("runs within budget", () => {
                 41|     const ms = time(() => run(sys));
                 42|     expect(ms).toBeLessThan(100);
                   |                ^
                 43|   });

            ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯[1/1]⎯

             Test Files  1 failed | 63 passed (64)
                  Tests  1 failed | 1210 passed (1211)
               Start at  12:20:01
               Duration  9.41s (transform 1.20s, setup 0ms, collect 4.11s, tests 3.02s, environment 2ms, prepare 9.80s)

            """);
        const string stderr = """
            npm error Lifecycle script `test` failed with error:
            npm error code 1
            npm error path /work/atlas
            npm error workspace atlas@1.0.0
            npm error location /work/atlas
            npm error command failed
            npm error command sh -c vitest run
            """;

        var excerpt = FailureExcerpt.Extract(stdout.ToString(), stderr);

        Assert.True(excerpt.Length <= Max, $"{excerpt.Length} characters");
        Assert.Contains("× rabinovichFabrikant > runs within budget", excerpt);
        Assert.Contains("FAIL  src/seeds.test.ts > rabinovichFabrikant > runs within budget", excerpt);
        Assert.Contains("AssertionError: expected 108.53 to be less than 100", excerpt);
        Assert.Contains("Tests  1 failed | 1210 passed", excerpt);
        Assert.Contains("npm error command sh -c vitest run", excerpt);
        Assert.DoesNotContain("renders Error: fallback", excerpt);
        Assert.DoesNotContain("module-10", excerpt);
    }

    [Fact]
    public void Dotnet_test_failures_are_found_between_passing_tests()
    {
        var log = new StringBuilder("  Determining projects to restore...\n  All projects are up-to-date for restore.\n");
        for (var i = 0; i < 80; i++)
        {
            log.Append(string.Create(CultureInfo.InvariantCulture, $"  Passed Reactor.Tests.Engine.ErrorTests.Handles_error_case_{i:00} [{i % 9 + 1} ms]\n"));
        }

        log.Append("""
              Failed Reactor.Tests.Engine.EngineTests.Node_timeout_fails_the_stage [205 ms]
              Error Message:
               Assert.Contains() Failure: Sub-string not found
              String:    "Stage 'slow' failed"
              Not found: "timed out after 200ms"
              Stack Trace:
                 at Reactor.Tests.Engine.EngineTests.Node_timeout_fails_the_stage() in /src/tests/Reactor.Tests/Engine/EngineTests.cs:line 485

            """);
        for (var i = 80; i < 160; i++)
        {
            log.Append(string.Create(CultureInfo.InvariantCulture, $"  Passed Reactor.Tests.Graphs.DotParserTests.Parses_case_{i:000} [{i % 7 + 1} ms]\n"));
        }

        log.Append("\nFailed!  - Failed:     1, Passed:   160, Skipped:     0, Total:   161, Duration: 40 s - Reactor.Tests.dll (net10.0)\n");

        var excerpt = FailureExcerpt.Extract(log.ToString(), "");

        Assert.True(excerpt.Length <= Max, $"{excerpt.Length} characters");
        Assert.Contains("Failed Reactor.Tests.Engine.EngineTests.Node_timeout_fails_the_stage [205 ms]", excerpt);
        Assert.Contains("Assert.Contains() Failure: Sub-string not found", excerpt);
        Assert.Contains("Failed!  - Failed:     1, Passed:   160", excerpt);
        Assert.DoesNotContain("Handles_error_case_40", excerpt);
    }

    [Fact]
    public void A_traceback_in_a_long_log_keeps_its_exception_line()
    {
        var log = new StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            log.Append(string.Create(CultureInfo.InvariantCulture, $"INFO  worker-{i % 4}: processed batch {i} (0 errors, 128 records)\n"));
        }

        log.Append("""
            Traceback (most recent call last):
              File "/app/pipeline/run.py", line 88, in <module>
                main()
              File "/app/pipeline/run.py", line 61, in main
                load(config["source"])
              File "/app/pipeline/load.py", line 17, in load
                raise ValueError(f"unsupported source: {source!r}")
            ValueError: unsupported source: 'ftp://legacy'

            """);
        for (var i = 0; i < 40; i++)
        {
            log.Append(string.Create(CultureInfo.InvariantCulture, $"INFO  cleanup step {i}: ok\n"));
        }

        var excerpt = FailureExcerpt.Extract(log.ToString(), "");

        Assert.True(excerpt.Length <= Max, $"{excerpt.Length} characters");
        Assert.Contains("Traceback (most recent call last):", excerpt);
        Assert.Contains("ValueError: unsupported source: 'ftp://legacy'", excerpt);
        Assert.EndsWith("INFO  cleanup step 39: ok", excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("processed batch 50", excerpt);
    }

    [Fact]
    public void Repeated_failures_are_shown_once_and_without_failure_lines_the_end_is_kept()
    {
        var log = new StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            log.Append(i is 20 or 50 ? "FAIL  tests/login.spec.ts > signs in\n" : string.Create(CultureInfo.InvariantCulture, $"step {i:000}: building module {i} of the application bundle\n"));
        }

        var excerpt = FailureExcerpt.Extract(log.ToString(), "");
        Assert.Equal(1, CountOf(excerpt, "FAIL  tests/login.spec.ts"));
        Assert.Contains("step 099", excerpt);

        var quiet = FailureExcerpt.Extract(log.ToString().Replace("FAIL  tests/login.spec.ts > signs in", "step: waiting", StringComparison.Ordinal), "");
        Assert.StartsWith("…\n", quiet, StringComparison.Ordinal);
        Assert.EndsWith("step 099: building module 99 of the application bundle", quiet, StringComparison.Ordinal);
        Assert.True(quiet.Length <= Max);
    }

    [Fact]
    public void Very_long_lines_are_clipped_to_fit()
    {
        var minified = "Error: Cannot read properties of undefined (reading 'map') at " + new string('x', 20_000);
        var log = string.Join('\n', Enumerable.Range(0, 50).Select(i => string.Create(CultureInfo.InvariantCulture, $"line {i}"))) + "\n" + minified + "\n" + new string('y', 5_000);

        var excerpt = FailureExcerpt.Extract(log, 600);

        Assert.True(excerpt.Length <= 600, $"{excerpt.Length} characters");
        Assert.Contains("Error: Cannot read properties of undefined (reading 'map')", excerpt);
    }

    [Theory]
    [InlineData("FAIL  src/a.test.ts > adds", 2)]
    [InlineData("--- FAIL: TestParse (0.00s)", 2)]
    [InlineData("BUILD FAILED", 2)]
    [InlineData("[ERROR] Failed to execute goal", 2)]
    [InlineData("TypeError: Cannot read properties of undefined", 2)]
    [InlineData("AssertionError [ERR_ASSERTION]: Expected values to be strictly equal", 2)]
    [InlineData("System.InvalidOperationException: Sequence contains no elements", 2)]
    [InlineData("src/app.ts(12,5): error TS2322: Type 'string' is not assignable to type 'number'.", 2)]
    [InlineData("Program.cs(3,1): error CS1002: ; expected", 2)]
    [InlineData("error[E0308]: mismatched types", 2)]
    [InlineData("main.c:3:5: error: expected ';' before 'return'", 2)]
    [InlineData("thread 'main' panicked at src/main.rs:2:5:", 2)]
    [InlineData("npm ERR! code ELIFECYCLE", 2)]
    [InlineData("npm error code 1", 2)]
    [InlineData("   × rabinovich > runs within budget 108ms", 2)]
    [InlineData("  ✘  2 [chromium] › example.spec.ts:8:1 › get started link (30.0s)", 2)]
    [InlineData("  ✕ renders the header (12 ms)", 2)]
    [InlineData("Tests:       1 failed, 12 passed, 13 total", 2)]
    [InlineData("Tests run: 5, Failures: 1, Errors: 0, Skipped: 0", 2)]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", 2)]
    [InlineData("  Failed Reactor.Tests.X.Y [12 ms]", 2)]
    [InlineData("Test timeout of 30000ms exceeded.", 2)]
    [InlineData("Error loading configuration", 1)]
    [InlineData("Build failed with an unexpected problem", 1)]
    [InlineData("E       assert 1 == 2", 1)]
    [InlineData(" ✓ src/errors.test.ts > throws Error: on invalid input", 0)]
    [InlineData("PASS  src/error-handling.test.ts", 0)]
    [InlineData("  Passed Reactor.Tests.ErrorTests.Reports_failed_stages [3 ms]", 0)]
    [InlineData("ok  \tgithub.com/acme/pkg\t0.12s", 0)]
    [InlineData("Tests: 0 failed, 12 passed", 0)]
    [InlineData("Found 0 errors. Watching for file changes.", 0)]
    [InlineData("Tests run: 5, Failures: 0, Errors: 0, Skipped: 0", 0)]
    [InlineData("    0 Error(s)", 0)]
    [InlineData("no failures found", 0)]
    [InlineData("compiled src/errorHandler.ts in 12ms", 0)]
    [InlineData("", 0)]
    public void Lines_are_classified_by_how_strongly_they_suggest_a_failure(string line, int expected) =>
        Assert.Equal(expected, FailureExcerpt.Classify(line));

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
