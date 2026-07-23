namespace Reactor.Backends;

/// <summary>
/// The steering-file protocol. A supervisor (a <c>house</c> manager node) or the server's steer endpoint writes
/// <c>steering.md</c> into a running stage's directory; the stage's backend takes it and passes the text to its agent.
/// Taking renames the file to <c>steering.consumed-N.md</c> before reading it, so text appended while it is being taken
/// is never lost (writers hold the file without delete sharing, which makes the rename wait for a complete write).
/// </summary>
internal static class SteeringFile
{
    public const string FileName = "steering.md";

    /// <summary>Takes the pending steering text from a stage directory: null when there is none or it is still being written.</summary>
    public static async Task<string?> TryTakeAsync(string stageDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(stageDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        string taken;
        try
        {
            taken = NextConsumedPath(stageDirectory);
            File.Move(path, taken, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // Still being written (or just taken by someone else); look again next time.
        }

        try
        {
            return await File.ReadAllTextAsync(taken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Polls <paramref name="stageDirectory"/> every <paramref name="interval"/> until cancelled, passing each non-empty
    /// steering message to <paramref name="deliver"/>. Nothing is taken while <paramref name="accepting"/> returns false.
    /// </summary>
    public static async Task WatchAsync(
        string? stageDirectory,
        TimeSpan interval,
        Func<string, Task> deliver,
        Func<bool>? accepting,
        CancellationToken cancellationToken)
    {
        if (stageDirectory is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (accepting?.Invoke() == false)
                {
                    continue;
                }

                var text = await TryTakeAsync(stageDirectory, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await deliver(text.Trim()).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string NextConsumedPath(string stageDirectory)
    {
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(stageDirectory, $"steering.consumed-{n}.md");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
