using System.Text.Json.Nodes;
using Reactor.Workspaces;

namespace Reactor.Tests.Support;

/// <summary>A temporary git repository (deleted on dispose) with helpers to commit and run git.</summary>
internal sealed class TempRepo : IDisposable
{
    private readonly TempDir _dir = new();

    private TempRepo()
    {
    }

    public string Path => _dir.Path;

    public string Combine(params string[] parts) => _dir.Combine(parts);

    /// <summary>A repository with <paramref name="files"/> (relative path → content) committed as its first commit.</summary>
    public static async Task<TempRepo> CreateAsync(IReadOnlyDictionary<string, string> files)
    {
        var repo = new TempRepo();
        await repo.GitAsync("init", "-q");
        await repo.GitAsync("config", "user.name", "Test");
        await repo.GitAsync("config", "user.email", "test@example.com");
        await repo.GitAsync("config", "core.autocrlf", "false");
        foreach (var (path, content) in files)
        {
            repo.Write(path, content);
        }

        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "-m", "initial");
        return repo;
    }

    public void Write(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string relative) => File.ReadAllText(Combine(relative));

    public async Task<string> GitAsync(params string[] arguments) =>
        (await GitCli.RunCheckedAsync(Path, arguments)).Output.Trim();

    /// <summary>The paths of the repository's registered worktrees (the main work tree first).</summary>
    public async Task<List<string>> WorktreesAsync() =>
        (await GitAsync("worktree", "list", "--porcelain")).Split('\n')
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => System.IO.Path.GetFullPath(l["worktree ".Length..].Trim()))
            .ToList();

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

internal static class ChangesFile
{
    /// <summary>Reads <c>&lt;stage&gt;/changes.json</c> of a run.</summary>
    public static JsonObject Read(string logsRoot, string nodeId, string? visit = null)
    {
        var directory = visit is null ? System.IO.Path.Combine(logsRoot, nodeId) : System.IO.Path.Combine(logsRoot, nodeId, "history", visit);
        return JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(directory, "changes.json")))!.AsObject();
    }

    /// <summary>(path, change) pairs of a changes.json record.</summary>
    public static List<(string Path, string Change)> Entries(JsonObject record) =>
        record["changes"]!.AsArray().Select(c => (c!["path"]!.GetValue<string>(), c["change"]!.GetValue<string>())).ToList();
}
