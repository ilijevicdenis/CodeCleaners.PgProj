using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PgProj.Lsp.Debounce;

/// <summary>
/// Per-document debounce + supersede-on-newer-edit scheduler. Each <see cref="Schedule"/> for a key cancels
/// any pending/in-flight run for that key and starts a fresh one after a quiet window, so a burst of
/// keystrokes triggers exactly one analysis (the last one) — and an analysis already running when a newer
/// edit lands is cancelled (its <see cref="CancellationToken"/> trips) and its result discarded. This is the
/// cancellation contract the tests assert: a superseded run never publishes.
///
/// Pure timing/cancellation machinery — it knows nothing about LSP or the engine; the work is a delegate.
/// </summary>
public sealed class DebouncedAnalysisScheduler : IDisposable
{
    private readonly int _delayMs;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private long _seq;
    private bool _disposed;

    public DebouncedAnalysisScheduler(int delayMs = 150) => _delayMs = delayMs;

    /// <summary>
    /// (Re)schedules <paramref name="work"/> for <paramref name="key"/>. Cancels the previous run for the key,
    /// waits the debounce window, then invokes <paramref name="work"/> with a token that trips if a later
    /// <see cref="Schedule"/> for the same key supersedes this one. Cancellation is swallowed (it is the
    /// expected outcome of being superseded), so callers never see an <see cref="OperationCanceledException"/>.
    /// </summary>
    public void Schedule(string key, Func<CancellationToken, Task> work)
    {
        if (_disposed) return;

        var cts = new CancellationTokenSource();
        // Swap in the new cts and cancel whatever was there (the prior, now-superseded, run for this key).
        if (_pending.TryGetValue(key, out var prior))
            TryCancel(prior);
        _pending[key] = cts;

        var id = Interlocked.Increment(ref _seq);
        var task = RunAsync(key, cts, work);
        _inFlight[id] = task;
        _ = task.ContinueWith(_ => _inFlight.TryRemove(id, out Task? _), TaskScheduler.Default);
    }

    /// <summary>
    /// Awaits every run currently in flight (the debounce delay + the work body) so a caller — e.g. the server
    /// on <c>shutdown</c> — can let pending diagnostics flush before it stops reading. Best-effort: runs
    /// scheduled after the snapshot is taken are not awaited.
    /// </summary>
    public Task DrainAsync() => Task.WhenAll(_inFlight.Values);

    private async Task RunAsync(string key, CancellationTokenSource cts, Func<CancellationToken, Task> work)
    {
        try
        {
            await Task.Delay(_delayMs, cts.Token).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            await work(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when superseded — discard silently.
        }
        finally
        {
            // Only clear the slot if it is still ours (a newer Schedule may have replaced it already).
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                _pending.TryRemove(key, out _);
            cts.Dispose();
        }
    }

    /// <summary>Cancels any pending/in-flight run for a key (e.g. on didClose).</summary>
    public void Cancel(string key)
    {
        if (_pending.TryRemove(key, out var cts)) TryCancel(cts);
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _pending) TryCancel(kv.Value);
        _pending.Clear();
    }
}
