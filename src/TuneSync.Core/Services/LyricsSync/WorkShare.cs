using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace TuneSync.Core.Services.LyricsSync;

/// <summary>Shares independent pieces of work (a song's chunks for the vocal separation, its pieces
/// for the listening) between devices - the graphics built into the CPU and the CPU itself - each
/// taking the next piece as soon as it's free, so whichever is faster does more of it.
///
/// On integrated graphics neither is plainly the faster one. Measured on a Core Ultra 5 225 with
/// its Intel graphics (tools/SyncBench songs, 5 CPU threads): separating a song took 59 s on the
/// graphics, 67 s on the CPU and 40 s on both; listening to most pieces took the two about as
/// long. Each runs slower while the other works (they share the memory and the power), so a device
/// found much slower than another (<see cref="SlowerBy"/>) stops taking work: it would cost the
/// other more than it adds.
///
/// With <c>hedge</c>, a device that runs out of pieces while another is still on one takes the
/// same piece too when it should finish it sooner (by how fast each has been): the first result
/// is kept and the other attempt cancelled. Whisper on that Intel graphics took 30 s over a piece
/// it had to decode again (unsure of what it heard), which the CPU did in 5 s; without this, such
/// a piece left last on the graphics held up the whole listen.</summary>
public static class WorkShare
{
    /// <summary>A device's speed, kept from run to run (so a later listen starts knowing it).</summary>
    public sealed class Device(string name)
    {
        public string Name { get; } = name;

        private readonly List<double> _secondsPerUnit = [];

        /// <summary>Found much slower than another device, or failed: takes no more work.</summary>
        public bool Retired { get; internal set; }

        /// <summary>Its work threw: retired for good (unless no other device is left).</summary>
        public bool Failed { get; internal set; }

        internal int Done => _secondsPerUnit.Count;

        internal void Record(double seconds, double units) => _secondsPerUnit.Add(seconds / Math.Max(units, 1e-9));

        /// <summary>Its usual time per unit of work: the median, as one piece can take many times
        /// as long as the rest (see the hedging above) without the device being slow.</summary>
        internal double? SecondsPerUnit
        {
            get
            {
                if (_secondsPerUnit.Count == 0) return null;
                var sorted = _secondsPerUnit.Order().ToList();
                var mid = sorted.Count / 2;
                return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
            }
        }
    }

    /// <summary>A device this many times slower per unit than another stops taking work.</summary>
    public const double SlowerBy = 3.0;

    /// <summary>Pieces a device has to have done before it's judged slower.</summary>
    private const int JudgedAfter = 3;

    /// <summary>How often an idle device looks again whether to take a piece another is still on.</summary>
    private static readonly TimeSpan Recheck = TimeSpan.FromMilliseconds(250);

    private sealed class Attempt(int item, int device, CancellationToken stop)
    {
        public int Item { get; } = item;
        public int Device { get; } = device;
        public CancellationTokenSource Cancel { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);
        public long Started { get; } = Stopwatch.GetTimestamp();
    }

    /// <summary>Runs <paramref name="work"/>(device, item, token) once for every item, sharing the
    /// items between the devices that aren't <see cref="Device.Retired"/>; returns the results in
    /// item order. Returns only once nothing runs any more (cancelled or not): the work's models
    /// may be freed as soon as it has.</summary>
    /// <param name="sizes">Each item's size, in whatever unit its time is about proportional to.</param>
    /// <param name="hedge">Let an idle device take a piece another is still on (see above).</param>
    /// <param name="progress">Items done so far.</param>
    public static async Task<T[]> RunAsync<T>(IReadOnlyList<Device> devices, IReadOnlyList<double> sizes,
        Func<int, int, CancellationToken, Task<T>> work, bool hedge, Action<int>? progress, CancellationToken ct)
    {
        var n = sizes.Count;
        var results = new T[n];
        if (n == 0) return results;
        // Every device retired (they all failed last time): try them again rather than give up.
        if (devices.All(x => x.Retired))
            foreach (var x in devices) x.Retired = false;
        // Longest first when sharing: what's left for last is then short, so whichever device ends
        // up on it doesn't keep the other waiting long.
        var queue = devices.Count(d => !d.Retired) > 1
            ? Enumerable.Range(0, n).OrderByDescending(i => sizes[i]).ToList()
            : Enumerable.Range(0, n).ToList();
        var finished = new bool[n];
        var left = n;
        var running = new List<Attempt>();
        var gate = new object();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExceptionDispatchInfo? failure = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Under the lock: wakes the devices waiting for something to change.
        void Changed()
        {
            var old = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
        }

        int Active() => devices.Count(d => !d.Retired);

        double Elapsed(long since) => Stopwatch.GetElapsedTime(since).TotalSeconds;

        // Under the lock: the next item for device d, or a piece another is still on.
        Attempt? Next(int d)
        {
            if (queue.Count > 0)
            {
                var item = queue[0];
                queue.RemoveAt(0);
                return new Attempt(item, d, stop.Token);
            }
            if (!hedge) return null;
            Attempt? best = null;
            var bestGain = 0.0;
            foreach (var other in running)
            {
                if (other.Device == d || finished[other.Item] || running.Count(r => r.Item == other.Item) > 1) continue;
                var otherRate = devices[other.Device].SecondsPerUnit ?? devices[d].SecondsPerUnit;
                var myRate = devices[d].SecondsPerUnit ?? otherRate;
                if (otherRate is not { } theirs || myRate is not { } mine) continue;
                var elapsed = Elapsed(other.Started);
                // Already longer than usual: assume it takes as long again (such a piece is one
                // whisper.cpp decodes over and over, not one that's nearly done).
                var otherLeft = theirs * sizes[other.Item] - elapsed;
                if (otherLeft < 0) otherLeft = elapsed;
                var gain = otherLeft - mine * sizes[other.Item];
                if (gain > bestGain) (best, bestGain) = (other, gain);
            }
            return best is null ? null : new Attempt(best.Item, d, stop.Token);
        }

        // Under the lock: a device much slower than the fastest stops taking work.
        void Judge()
        {
            var judged = devices.Where(x => !x.Retired && x.Done >= JudgedAfter).ToList();
            if (judged.Count < 2) return;
            var fastest = judged.Min(x => x.SecondsPerUnit!.Value);
            foreach (var x in judged)
                if (x.SecondsPerUnit > fastest * SlowerBy && Active() > 1) x.Retired = true;
        }

        async Task Worker(int d)
        {
            while (true)
            {
                Attempt? attempt;
                Task wait;
                lock (gate)
                {
                    // A device retired for being slow stays by, in case the others fail.
                    if (left == 0 || stop.IsCancellationRequested || devices[d] is { Retired: true, Failed: true }) return;
                    attempt = devices[d].Retired ? null : Next(d);
                    if (attempt is not null) running.Add(attempt);
                    wait = changed.Task;
                }
                if (attempt is null)
                {
                    await Task.WhenAny(wait, Task.Delay(Recheck, stop.Token)).ConfigureAwait(false);
                    continue;
                }

                List<CancellationTokenSource> losers = [];
                try
                {
                    var result = await work(d, attempt.Item, attempt.Cancel.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        running.Remove(attempt);
                        if (!finished[attempt.Item])
                        {
                            finished[attempt.Item] = true;
                            results[attempt.Item] = result;
                            left--;
                            devices[d].Record(Elapsed(attempt.Started), sizes[attempt.Item]);
                            losers.AddRange(running.Where(r => r.Item == attempt.Item).Select(r => r.Cancel));
                            Judge();
                            progress?.Invoke(n - left);
                        }
                        Changed();
                    }
                }
                catch (OperationCanceledException) when (attempt.Cancel.IsCancellationRequested)
                {
                    // Lost to another device, or the whole run was cancelled.
                    lock (gate)
                    {
                        running.Remove(attempt);
                        Changed();
                    }
                }
                catch (Exception ex)
                {
                    // This device failed (a graphics driver giving up, say): the others carry on
                    // with its piece. With none left, the run fails with it.
                    var last = false;
                    lock (gate)
                    {
                        running.Remove(attempt);
                        devices[d].Retired = devices[d].Failed = true;
                        // Only a device retired for being slow left: better slow than not at all.
                        if (Active() == 0)
                            foreach (var x in devices.Where(x => !x.Failed)) x.Retired = false;
                        if (Active() == 0)
                        {
                            failure ??= ExceptionDispatchInfo.Capture(ex);
                            last = true;
                        }
                        else if (!finished[attempt.Item] && !running.Any(r => r.Item == attempt.Item))
                            queue.Insert(0, attempt.Item);
                        Changed();
                    }
                    if (last) stop.Cancel();
                    return;
                }
                finally
                {
                    attempt.Cancel.Dispose();
                }
                // Outside the lock: a cancelled attempt's own handling takes the lock.
                foreach (var loser in losers)
                {
                    try { loser.Cancel(); }
                    catch (ObjectDisposedException) { /* it ended meanwhile */ }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, devices.Count).Select(d => Task.Run(() => Worker(d), CancellationToken.None))).ConfigureAwait(false);
        failure?.Throw();
        ct.ThrowIfCancellationRequested();
        if (left > 0) throw new InvalidOperationException("No device was left to do the work.");
        return results;
    }
}
