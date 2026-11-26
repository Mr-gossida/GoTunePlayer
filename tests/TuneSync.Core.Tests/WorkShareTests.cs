using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.Core.Tests;

public class WorkShareTests
{
    private static WorkShare.Device[] Devices(int count) => [.. Enumerable.Range(0, count).Select(i => new WorkShare.Device($"d{i}"))];

    private static double[] Sizes(int count) => [.. Enumerable.Repeat(1.0, count)];

    [Fact]
    public async Task One_device_does_every_item_in_order()
    {
        var order = new List<int>();
        var reports = new List<int>();
        var results = await WorkShare.RunAsync(Devices(1), Sizes(5), (d, i, _) =>
        {
            order.Add(i);
            return Task.FromResult(i * 10);
        }, hedge: true, reports.Add, CancellationToken.None);

        Assert.Equal([0, 10, 20, 30, 40], results);
        Assert.Equal([0, 1, 2, 3, 4], order);
        Assert.Equal([1, 2, 3, 4, 5], reports);
    }

    [Fact]
    public async Task Two_devices_share_and_results_stay_in_item_order()
    {
        var byDevice = new int[2];
        var results = await WorkShare.RunAsync(Devices(2), Sizes(20), async (d, i, ct) =>
        {
            await Task.Delay(20, ct);
            Interlocked.Increment(ref byDevice[d]);
            return i;
        }, hedge: false, null, CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 20), results);
        Assert.True(byDevice[0] > 0 && byDevice[1] > 0);
        Assert.Equal(20, byDevice.Sum());
    }

    [Fact]
    public async Task A_much_slower_device_stops_taking_work()
    {
        var devices = Devices(2);
        var byDevice = new int[2];
        await WorkShare.RunAsync(devices, Sizes(60), async (d, i, ct) =>
        {
            await Task.Delay(d == 0 ? 10 : 150, ct);
            Interlocked.Increment(ref byDevice[d]);
            return i;
        }, hedge: false, null, CancellationToken.None);

        Assert.True(devices[1].Retired);
        Assert.False(devices[0].Retired);
        Assert.True(byDevice[1] <= 5, $"the slow device did {byDevice[1]}");
    }

    [Fact]
    public async Task An_idle_device_takes_over_a_piece_stuck_on_the_other()
    {
        var devices = Devices(2);
        var stuck = -1;
        var stuckCancelled = false;
        // The first piece device 1 takes takes it far longer than usual; device 0 runs out of
        // pieces and does that one too.
        var results = await WorkShare.RunAsync(devices, Sizes(7), async (d, i, ct) =>
        {
            if (d == 1 && Interlocked.CompareExchange(ref stuck, i, -1) == -1)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { stuckCancelled = true; throw; }
            }
            else await Task.Delay(30, ct);
            return $"{i}@{d}";
        }, hedge: true, null, CancellationToken.None);

        Assert.Equal($"{stuck}@0", results[stuck]);
        Assert.True(stuckCancelled);
    }

    [Fact]
    public async Task A_failing_device_leaves_its_piece_to_the_other()
    {
        var devices = Devices(2);
        var results = await WorkShare.RunAsync(devices, Sizes(6), async (d, i, ct) =>
        {
            await Task.Delay(10, ct);
            if (d == 1) throw new InvalidOperationException("driver gave up");
            return i;
        }, hedge: false, null, CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 6), results);
        Assert.True(devices[1].Failed);
    }

    [Fact]
    public async Task With_every_device_failing_the_error_comes_through()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkShare.RunAsync(Devices(2), Sizes(4), async (d, i, ct) =>
        {
            await Task.Delay(10, ct);
            throw new InvalidDataException("bad model");
#pragma warning disable CS0162
            return i;
#pragma warning restore CS0162
        }, hedge: true, null, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_it_returns_only_once_no_work_runs()
    {
        using var cts = new CancellationTokenSource();
        var running = 0;
        var task = WorkShare.RunAsync(Devices(2), Sizes(10), async (d, i, ct) =>
        {
            Interlocked.Increment(ref running);
            try
            {
                if (i == 1) cts.Cancel();
                // Like whisper.cpp: notices the cancellation only a while later.
                await Task.Delay(100, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                return i;
            }
            finally { Interlocked.Decrement(ref running); }
        }, hedge: true, null, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, Volatile.Read(ref running));
    }
}
