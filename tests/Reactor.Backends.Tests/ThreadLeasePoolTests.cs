namespace Reactor.Backends.Tests;

public sealed class ThreadLeasePoolTests
{
    private sealed class Resource(int id)
    {
        public int Id { get; } = id;

        public bool Disposed { get; set; }
    }

    private static int _ids;

    private static Resource New() => new(Interlocked.Increment(ref _ids));

    private static ThreadLeasePool<Resource> Pool(TimeSpan? idle = null) =>
        new(r =>
        {
            r.Disposed = true;
            return ValueTask.CompletedTask;
        })
        {
            IdleTimeout = idle ?? TimeSpan.FromHours(1),
        };

    [Fact]
    public async Task A_thread_reuses_its_resource_across_leases()
    {
        var pool = Pool();

        Resource first;
        await using (var lease = await pool.AcquireAsync("t", New))
        {
            Assert.True(lease.IsNew);
            Assert.True(lease.Pooled);
            Assert.False(lease.ContextLost);
            first = lease.Value;
        }

        await using (var again = await pool.AcquireAsync("t", New))
        {
            Assert.False(again.IsNew);
            Assert.False(again.ContextLost);
            Assert.Same(first, again.Value);
        }
    }

    [Fact]
    public async Task Contending_leases_fork_and_report_lost_context()
    {
        var pool = Pool();
        await using var held = await pool.AcquireAsync("t", New);

        await using (var fork = await pool.AcquireAsync("t", New))
        {
            Assert.False(fork.Pooled);
            Assert.True(fork.IsNew);
            Assert.True(fork.ContextLost);
            Assert.NotSame(held.Value, fork.Value);
            Assert.False(fork.Value.Disposed);
            var forked = fork.Value;
            await fork.DisposeAsync();
            Assert.True(forked.Disposed);
        }

        Assert.False(held.Value.Disposed);
    }

    [Fact]
    public async Task An_invalidated_resource_is_recreated_with_lost_context()
    {
        var pool = Pool();
        Resource original;
        await using (var lease = await pool.AcquireAsync("t", New))
        {
            original = lease.Value;
            await pool.InvalidateAsync(lease);
        }

        Assert.True(original.Disposed);
        await using var next = await pool.AcquireAsync("t", New);
        Assert.True(next.IsNew);
        Assert.True(next.Pooled);
        Assert.True(next.ContextLost);
        Assert.NotSame(original, next.Value);
    }

    [Fact]
    public async Task Idle_resources_are_evicted_and_disposed()
    {
        var pool = Pool(TimeSpan.FromMilliseconds(1));
        Resource original;
        await using (var lease = await pool.AcquireAsync("t", New))
        {
            original = lease.Value;
        }

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await using var next = await pool.AcquireAsync("t", New);

        Assert.True(original.Disposed);
        Assert.True(next.IsNew);
        Assert.True(next.ContextLost);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public async Task A_failing_factory_does_not_wedge_the_thread()
    {
        var pool = Pool();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync("t", () => throw new InvalidOperationException("boom")));

        await using var lease = await pool.AcquireAsync("t", New);
        Assert.True(lease.Pooled);
        Assert.True(lease.IsNew);
        Assert.False(lease.ContextLost);
    }

    [Fact]
    public async Task Thread_less_leases_are_never_pooled()
    {
        var pool = Pool();
        Resource value;
        await using (var lease = await pool.AcquireAsync(null, New))
        {
            Assert.False(lease.Pooled);
            Assert.False(lease.ContextLost);
            value = lease.Value;
        }

        Assert.True(value.Disposed);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public async Task Dispose_all_disposes_pooled_resources()
    {
        var pool = Pool();
        Resource value;
        await using (var lease = await pool.AcquireAsync("t", New))
        {
            value = lease.Value;
        }

        await pool.DisposeAllAsync();

        Assert.True(value.Disposed);
        Assert.Equal(0, pool.Count);
    }
}
