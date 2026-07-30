using System.Diagnostics;
using System.Globalization;

namespace Reactor.Workspaces;

/// <summary>
/// Object sizes from one long-running <c>git cat-file --batch-check</c> (so a diff does not start another process). Falls
/// back to a one-off call when the process cannot be used.
/// </summary>
internal sealed class GitObjectSizes(string directory) : IAsyncDisposable
{
    private static readonly string[] Arguments = ["cat-file", "--batch-check=%(objectname) %(objectsize)"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private bool _disposed;

    /// <summary>The sizes of <paramref name="ids"/> (objects that do not exist are left out).</summary>
    public async Task<Dictionary<string, long>> SizesAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                try
                {
                    if (_process is null)
                    {
                        _process = await GitCli.StartAsync(directory, Arguments).ConfigureAwait(false);
                        _ = _process.StandardError.ReadToEndAsync(CancellationToken.None);
                    }

                    return await QueryAsync(_process, ids, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    Stop();
                }
            }

            var once = await GitCli.RunAsync(directory, Arguments, new GitCall { Input = string.Join('\n', ids) + "\n" }, cancellationToken).ConfigureAwait(false);
            return Parse(once.Output.Split('\n'));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<Dictionary<string, long>> QueryAsync(Process process, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(60));

        // One output line per input line; read while writing so neither side blocks on a full pipe.
        var reader = Task.Run(async () =>
        {
            var lines = new List<string>(ids.Count);
            while (lines.Count < ids.Count)
            {
                lines.Add(await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false)
                          ?? throw new IOException("git cat-file exited."));
            }

            return lines;
        }, CancellationToken.None);
        foreach (var id in ids)
        {
            await process.StandardInput.WriteLineAsync(id.AsMemory(), limit.Token).ConfigureAwait(false);
        }

        await process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
        try
        {
            return Parse(await reader.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("git cat-file did not answer.");
        }
    }

    private static Dictionary<string, long> Parse(IEnumerable<string> lines)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var parts = line.Trim().Split(' ');
            if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                sizes[parts[0]] = size;
            }
        }

        return sizes;
    }

    private void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            Stop();
        }
        finally
        {
            _gate.Release();
        }
    }
}
