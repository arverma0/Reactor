using System.Diagnostics;

namespace Reactor.Agent.Environments;

/// <summary>How a <see cref="ShellKind"/> is launched: the executable and the way the command is passed to it.</summary>
internal sealed class ShellInvocation
{
    private readonly ShellStyle _style;

    private ShellInvocation(string fileName, string displayName, ShellStyle style)
    {
        FileName = fileName;
        DisplayName = displayName;
        _style = style;
    }

    private enum ShellStyle
    {
        Cmd,
        Posix,
        PowerShell,
    }

    /// <summary>The executable started for each command.</summary>
    public string FileName { get; }

    /// <summary>Human-readable shell name (<see cref="IExecutionEnvironment.ShellName"/>).</summary>
    public string DisplayName { get; }

    /// <summary>True when the command is passed through an argument list (and can therefore be wrapped by <c>setsid</c>).</summary>
    public bool UsesArgumentList => _style != ShellStyle.Cmd;

    /// <summary>Resolves the shell for <paramref name="kind"/> on the current platform.</summary>
    public static ShellInvocation Resolve(ShellKind kind)
    {
        var isWindows = OperatingSystem.IsWindows();
        return kind switch
        {
            ShellKind.Default => isWindows ? Cmd() : File.Exists("/bin/bash") ? Posix("/bin/bash") : Posix("/bin/sh"),
            ShellKind.Cmd => Cmd(),
            ShellKind.Bash => Posix(isWindows ? "bash" : File.Exists("/bin/bash") ? "/bin/bash" : "bash"),
            ShellKind.Sh => Posix(isWindows ? "sh" : "/bin/sh"),
            ShellKind.PowerShell => new ShellInvocation("powershell.exe", "powershell.exe", ShellStyle.PowerShell),
            ShellKind.Pwsh => new ShellInvocation("pwsh", "pwsh", ShellStyle.PowerShell),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shell kind."),
        };
    }

    /// <summary>
    /// Creates the start info for <paramref name="command"/>. When <paramref name="setsidPath"/> is given (Unix), the shell is
    /// launched through <c>setsid</c> so it leads a new process group that can be signalled as a whole.
    /// </summary>
    public ProcessStartInfo CreateStartInfo(string command, string? setsidPath)
    {
        var startInfo = new ProcessStartInfo { UseShellExecute = false };
        if (_style == ShellStyle.Cmd)
        {
            // cmd's /s switch strips exactly the outer quotes and keeps the command verbatim, so the raw argument string
            // must be used (ArgumentList would escape embedded quotes in a way cmd does not understand).
            startInfo.FileName = FileName;
            startInfo.Arguments = "/d /s /c \"" + command + "\"";
            return startInfo;
        }

        if (setsidPath is not null)
        {
            startInfo.FileName = setsidPath;
            startInfo.ArgumentList.Add(FileName);
        }
        else
        {
            startInfo.FileName = FileName;
        }

        if (_style == ShellStyle.PowerShell)
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
        }

        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static ShellInvocation Cmd()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        var fileName = !string.IsNullOrWhiteSpace(comSpec) ? comSpec
            : OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "cmd.exe";
        return new ShellInvocation(fileName, "cmd.exe", ShellStyle.Cmd);
    }

    private static ShellInvocation Posix(string path) => new(path, path, ShellStyle.Posix);
}

/// <summary>Finds executables on <c>PATH</c>.</summary>
internal static class ExecutableLocator
{
    /// <summary>Returns the full path of <paramref name="name"/> found on PATH (honoring PATHEXT on Windows), or null.</summary>
    public static string? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), name + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entries are skipped.
                }
            }
        }

        return null;
    }

    /// <summary>The first existing file among <paramref name="candidates"/>, falling back to a PATH search for <paramref name="name"/>.</summary>
    public static string? FindAny(string name, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Find(name);
    }
}
