using System.Diagnostics;

namespace Reactor.Server.Tests;

/// <summary>A temporary git repository (git must be on the PATH), deleted on dispose.</summary>
internal sealed class GitRepo : IDisposable
{
    private readonly TempDir _dir = new();

    private GitRepo()
    {
    }

    public string Path => _dir.Path;

    /// <summary>A repository with <paramref name="files"/> (relative path → content) committed as its first commit.</summary>
    public static GitRepo Create(IReadOnlyDictionary<string, string> files)
    {
        var repo = new GitRepo();
        repo.Git("init", "-q");
        repo.Git("config", "user.name", "Test");
        repo.Git("config", "user.email", "test@example.com");
        repo.Git("config", "core.autocrlf", "false");
        foreach (var (path, content) in files)
        {
            repo.Write(path, content);
        }

        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "initial");
        return repo;
    }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string relative) => File.ReadAllText(Combine(relative));

    public bool Exists(string relative) => File.Exists(Combine(relative));

    /// <summary>Runs git in the repository; throws when it fails. Returns its trimmed output.</summary>
    public string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {error.Result}");
        }

        return output.Trim();
    }

    public void Dispose()
    {
        // Git writes its objects read-only, which Directory.Delete refuses on Windows.
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _dir.Dispose();
    }
}
