using System.Threading;
using System.Threading.Tasks;
using PgProj.Lsp.Debounce;
using Xunit;

namespace PgProj.Lsp.Tests;

/// <summary>Debounce + supersede-on-newer-edit: a newer schedule cancels an in-flight run and only one wins.</summary>
public sealed class DebounceSchedulerTests
{
    [Fact]
    public async Task A_burst_of_edits_runs_only_the_last()
    {
        using var sched = new DebouncedAnalysisScheduler(delayMs: 40);
        var runs = 0;
        var done = new SemaphoreSlim(0);

        for (var i = 0; i < 5; i++)
            sched.Schedule("doc", _ => { Interlocked.Increment(ref runs); done.Release(); return Task.CompletedTask; });

        Assert.True(await done.WaitAsync(2000));
        await Task.Delay(150); // settle window — no further runs should fire
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_newer_edit_cancels_an_in_flight_run()
    {
        using var sched = new DebouncedAnalysisScheduler(delayMs: 10);
        var firstObservedCancel = new TaskCompletionSource<bool>();
        var secondCompleted = new SemaphoreSlim(0);

        // First run blocks until cancelled, then reports whether it saw cancellation.
        sched.Schedule("doc", async token =>
        {
            try { await Task.Delay(2000, token); }
            catch (System.OperationCanceledException) { firstObservedCancel.TrySetResult(true); throw; }
            firstObservedCancel.TrySetResult(false);
        });

        await Task.Delay(60); // let the first run get past the debounce window and into the body

        // Second edit supersedes the first.
        sched.Schedule("doc", _ => { secondCompleted.Release(); return Task.CompletedTask; });

        var observed = await firstObservedCancel.Task.WaitAsync(System.TimeSpan.FromSeconds(2));
        Assert.True(observed); // the in-flight run observed cancellation
        Assert.True(await secondCompleted.WaitAsync(2000)); // the newer run completed
    }

    [Fact]
    public async Task Cancel_stops_a_pending_run()
    {
        using var sched = new DebouncedAnalysisScheduler(delayMs: 50);
        var ran = false;
        sched.Schedule("doc", _ => { ran = true; return Task.CompletedTask; });
        sched.Cancel("doc");
        await Task.Delay(150);
        Assert.False(ran);
    }
}
