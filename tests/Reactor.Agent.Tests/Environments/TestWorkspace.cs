using System.Text;
using System.Text.Json.Nodes;
using Reactor.Agent.Environments;

namespace Reactor.Agent.Tests.Environments;

/// <summary>
/// A throwaway working directory (under the test output folder) with a <see cref="LocalExecutionEnvironment"/> rooted in it,
/// plus helpers for invoking tools. Deleted on dispose.
/// </summary>
public sealed class TestWorkspace : IAsyncDisposable
{
    public TestWorkspace(LocalEnvironmentOptions? options = null)
    {
        Root = Path.Combine(AppContext.BaseDirectory, "tw", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        Environment = new LocalExecutionEnvironment((options ?? new LocalEnvironmentOptions { UseRipgrep = false }) with
        {
            WorkingDirectory = Root,
        });
    }

    public string Root { get; }

    public LocalExecutionEnvironment Environment { get; }

    public static bool IsWindows => OperatingSystem.IsWindows();

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string MakeDirectory(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string ReadText(string relative) => Encoding.UTF8.GetString(File.ReadAllBytes(PathOf(relative)));

    public byte[] ReadBytes(string relative) => File.ReadAllBytes(PathOf(relative));

    public bool Exists(string relative) => File.Exists(PathOf(relative));

    public void Touch(string relative, DateTime lastWriteUtc) => File.SetLastWriteTimeUtc(PathOf(relative), lastWriteUtc);

    public ToolContext Context(
        SessionConfig? config = null,
        Action<string>? onOutput = null,
        int? profileShellTimeoutMs = null,
        CancellationToken cancellationToken = default,
        IExecutionEnvironment? environment = null) => new()
    {
        Environment = environment ?? Environment,
        Config = config ?? new SessionConfig(),
        OnOutputDelta = onOutput,
        ProfileShellTimeoutMs = profileShellTimeoutMs,
        CancellationToken = cancellationToken,
        ToolCallId = "call_test",
    };

    /// <summary>Runs a tool with arguments parsed from JSON text.</summary>
    public Task<ToolOutput> RunAsync(RegisteredTool tool, string json, ToolContext? context = null) =>
        tool.Executor((JsonObject)JsonNode.Parse(json)!, context ?? Context());

    /// <summary>Runs a tool with arguments built in memory.</summary>
    public Task<ToolOutput> RunAsync(RegisteredTool tool, JsonObject arguments, ToolContext? context = null) =>
        tool.Executor(arguments, context ?? Context());

    public async ValueTask DisposeAsync()
    {
        await Environment.CleanupAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }
}

/// <summary>Builds in-memory tool arguments (CLR-backed JSON values, which are type-exact).</summary>
public static class Args
{
    public static JsonObject Of(params (string Name, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            result[name] = value switch
            {
                null => null,
                JsonNode node => node,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                decimal m => JsonValue.Create(m),
                string[] items => new JsonArray(items.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
                _ => throw new ArgumentException($"Unsupported argument type {value.GetType()}"),
            };
        }

        return result;
    }
}
