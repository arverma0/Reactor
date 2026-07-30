using System.Text;
using Reactor.Execution;
using Reactor.Handlers;

namespace Reactor.Backends.Tests.Support;

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reactor-backend-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

internal static class Pipelines
{
    public static PipelineRunner Runner(ICodergenBackend? backend) => new(new PipelineRunnerOptions
    {
        Backend = backend,
        LogsBaseDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reactor-backend-tests", "runs"),
    });

    public static RunOptions Options(TempDir dir, List<Events.PipelineEvent>? events = null) => new()
    {
        LogsRoot = dir.Combine("run"),
        WorkingDirectory = dir.Path,
        Backoff = BackoffConfig.Immediate,
        MaxSteps = 200,
        OnEvent = events is null ? null : e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        },
    };

    public static List<string> Logs(List<Events.PipelineEvent> events, string nodeId)
    {
        lock (events)
        {
            return events.OfType<Events.StageLog>().Where(e => e.NodeId == nodeId).Select(e => e.Message).ToList();
        }
    }
}

/// <summary>
/// Generates a fake CLI agent script: it records its arguments (one line per invocation) and stdin, prints canned stdout,
/// optionally writes stderr, sleeps, and exits with a given code.
/// </summary>
internal sealed class FakeCli
{
    private FakeCli(string directory, string executable)
    {
        Directory = directory;
        Executable = executable;
    }

    public string Directory { get; }

    /// <summary>Path to the script to configure as the backend's executable.</summary>
    public string Executable { get; }

    /// <param name="directory">Where the script and its recordings live.</param>
    /// <param name="stdout">Canned JSON lines to print.</param>
    /// <param name="exitCode">Exit code.</param>
    /// <param name="stderr">Optional stderr text.</param>
    /// <param name="sleepSeconds">Seconds to sleep (before printing, unless <paramref name="printBeforeSleep"/>).</param>
    /// <param name="printBeforeSleep">Print the output first, then sleep (a run that is killed mid-way).</param>
    /// <param name="sessionStateRoot">
    /// When set, the script copies <paramref name="sessionEvents"/> to <c>&lt;root&gt;/&lt;--session-id value&gt;/events.jsonl</c>,
    /// like the Copilot CLI's on-disk session state.
    /// </param>
    /// <param name="sessionEvents">The session-state events to write.</param>
    public static FakeCli Create(string directory, string stdout, int exitCode = 0, string? stderr = null, int sleepSeconds = 0, bool printBeforeSleep = false, string? sessionStateRoot = null, string? sessionEvents = null)
    {
        System.IO.Directory.CreateDirectory(directory);

        // A real CLI ends each JSON line with a newline; without one, a run killed mid-way would hold back its last line.
        var lines = stdout.Length == 0 || stdout.EndsWith('\n') ? stdout : stdout + "\n";
        File.WriteAllText(Path.Combine(directory, "output.jsonl"), lines.ReplaceLineEndings(OperatingSystem.IsWindows() ? "\r\n" : "\n"), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "stderr.txt"), stderr ?? "", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "session-events.jsonl"), sessionEvents ?? "", new UTF8Encoding(false));
        string script;
        string executable;
        if (OperatingSystem.IsWindows())
        {
            executable = Path.Combine(directory, "fake-agent.cmd");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal");
            sb.AppendLine("echo %*>> \"%~dp0args.txt\"");
            sb.AppendLine("more > \"%~dp0stdin.txt\"");
            sb.AppendLine("echo %CD%> \"%~dp0cwd.txt\"");
            if (sessionStateRoot is not null)
            {
                // `shift` also shifts %0, so the script's directory is captured first.
                sb.AppendLine("set \"HERE=%~dp0\"");
                sb.AppendLine(":args");
                sb.AppendLine("if \"%~1\"==\"\" goto argsdone");
                sb.AppendLine("if \"%~1\"==\"--session-id\" set \"SID=%~2\"");
                sb.AppendLine("shift");
                sb.AppendLine("goto args");
                sb.AppendLine(":argsdone");
                sb.AppendLine($"mkdir \"{sessionStateRoot}\\%SID%\" 2> nul");
                sb.AppendLine($"copy /y \"%HERE%session-events.jsonl\" \"{sessionStateRoot}\\%SID%\\events.jsonl\" > nul");
            }

            var here = sessionStateRoot is null ? "%~dp0" : "%HERE%";
            if (printBeforeSleep)
            {
                sb.AppendLine($"type \"{here}output.jsonl\"");
            }

            if (sleepSeconds > 0)
            {
                sb.AppendLine($"ping -n {sleepSeconds + 1} 127.0.0.1 > nul");
            }

            if (!printBeforeSleep)
            {
                sb.AppendLine($"type \"{here}output.jsonl\"");
            }

            if (!string.IsNullOrEmpty(stderr))
            {
                sb.AppendLine($"type \"{here}stderr.txt\" 1>&2");
            }

            sb.AppendLine($"exit /b {exitCode}");
            script = sb.ToString();
        }
        else
        {
            executable = Path.Combine(directory, "fake-agent.sh");
            var sb = new StringBuilder();
            sb.Append("#!/bin/sh\n");
            sb.Append("here=$(dirname \"$0\")\n");
            sb.Append("echo \"$@\" >> \"$here/args.txt\"\n");
            sb.Append("cat > \"$here/stdin.txt\"\n");
            sb.Append("pwd > \"$here/cwd.txt\"\n");
            if (sessionStateRoot is not null)
            {
                sb.Append("sid=\"\"\n");
                sb.Append("while [ $# -gt 0 ]; do if [ \"$1\" = \"--session-id\" ]; then sid=\"$2\"; fi; shift; done\n");
                sb.Append($"mkdir -p \"{sessionStateRoot}/$sid\"\n");
                sb.Append($"cp \"$here/session-events.jsonl\" \"{sessionStateRoot}/$sid/events.jsonl\"\n");
            }

            if (printBeforeSleep)
            {
                sb.Append("cat \"$here/output.jsonl\"\n");
            }

            if (sleepSeconds > 0)
            {
                sb.Append($"sleep {sleepSeconds}\n");
            }

            if (!printBeforeSleep)
            {
                sb.Append("cat \"$here/output.jsonl\"\n");
            }

            if (!string.IsNullOrEmpty(stderr))
            {
                sb.Append("cat \"$here/stderr.txt\" 1>&2\n");
            }

            sb.Append($"exit {exitCode}\n");
            script = sb.ToString();
        }

        File.WriteAllText(executable, script, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new FakeCli(directory, executable);
    }

    public List<string> Invocations =>
        File.Exists(Path.Combine(Directory, "args.txt"))
            ? File.ReadAllLines(Path.Combine(Directory, "args.txt")).Where(l => l.Trim().Length > 0).ToList()
            : [];

    public string Stdin => File.Exists(Path.Combine(Directory, "stdin.txt")) ? File.ReadAllText(Path.Combine(Directory, "stdin.txt")) : "";

    public string WorkingDirectory => File.Exists(Path.Combine(Directory, "cwd.txt")) ? File.ReadAllText(Path.Combine(Directory, "cwd.txt")).Trim() : "";
}
