using System;
using System.Threading.Tasks;

namespace KeyStats.Helpers;

/// <summary>
/// Runs statistics recomputation for a single consumer off the UI thread: at most one computation runs
/// at a time, requests arriving while it runs collapse into one pending run, and results are applied
/// only through the supplied UI dispatcher callback.
/// </summary>
/// <remarks>
/// A plain count notification must call <see cref="Request"/> without <c>conditionsChanged</c> so it
/// never invalidates the in-flight result; otherwise continuous input would keep discarding work and
/// the UI would never update. Selection changes pass <c>conditionsChanged: true</c> and make results
/// computed for the previous selection unapplicable. A result whose history revision no longer matches
/// is discarded and recomputed once through the same serialized loop, so no extra tasks pile up.
/// </remarks>
public sealed class BackgroundStatsRefresher<TResult>
{
    private enum ApplyOutcome
    {
        Applied,
        Skipped,
        Outdated,
        Failed
    }

    private readonly Func<Action, Task> _postToUi;
    private readonly Func<long> _revisionProvider;
    private readonly Func<TResult> _compute;
    private readonly Action<TResult> _apply;

    private readonly object _gate = new();
    private int _generation;
    private bool _isRunning;
    private bool _isPending;
    private bool _isClosed;

    /// <param name="postToUi">
    /// Queues the callback on the UI thread and returns the dispatcher operation's task, which
    /// completes when the callback ran or when the operation was aborted by a dispatcher shutdown.
    /// </param>
    public BackgroundStatsRefresher(
        Func<Action, Task> postToUi,
        Func<long> revisionProvider,
        Func<TResult> compute,
        Action<TResult> apply)
    {
        _postToUi = postToUi;
        _revisionProvider = revisionProvider;
        _compute = compute;
        _apply = apply;
    }

    /// <summary>
    /// Schedules a refresh, merging with an already pending one.
    /// </summary>
    public void Request(bool conditionsChanged = false)
    {
        lock (_gate)
        {
            if (_isClosed)
            {
                return;
            }

            if (conditionsChanged)
            {
                _generation++;
            }

            _isPending = true;
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
        }

        _ = Task.Run(RunAsync);
    }

    /// <summary>
    /// Stops applying results; a computation already in flight finishes without touching the UI.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            _isClosed = true;
            _isPending = false;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                int generation;
                lock (_gate)
                {
                    if (_isClosed || !_isPending)
                    {
                        return;
                    }

                    _isPending = false;
                    generation = _generation;
                }

                long revision;
                TResult result;
                try
                {
                    revision = _revisionProvider();
                    result = _compute();
                }
                catch (Exception)
                {
                    // A failed computation must not wedge the loop: drop this run and let the
                    // next request (count or selection) start a fresh computation.
                    continue;
                }

                var outcome = await PostApplyAsync(generation, revision, result).ConfigureAwait(false);
                if (outcome == ApplyOutcome.Outdated)
                {
                    lock (_gate)
                    {
                        if (_isClosed)
                        {
                            return;
                        }

                        _isPending = true;
                    }
                }
                else if (outcome == ApplyOutcome.Failed)
                {
                    return;
                }
            }
        }
        finally
        {
            EndRun();
        }
    }

    private void EndRun()
    {
        bool restart;
        lock (_gate)
        {
            _isRunning = false;
            restart = _isPending && !_isClosed;
            if (restart)
            {
                _isRunning = true;
            }
        }

        if (restart)
        {
            // A request that arrived while the run was failing or finishing must not be lost.
            _ = Task.Run(RunAsync);
        }
    }

    /// <returns>
    /// <see cref="ApplyOutcome.Applied"/> once the result was applied on the UI thread,
    /// <see cref="ApplyOutcome.Skipped"/> when conditions changed or the consumer closed,
    /// <see cref="ApplyOutcome.Outdated"/> when the history revision moved and a fresh
    /// computation is required, or <see cref="ApplyOutcome.Failed"/> when the UI callback
    /// could not run (dispatcher shut down) and no retry should be scheduled.
    /// </returns>
    private async Task<ApplyOutcome> PostApplyAsync(int generation, long revision, TResult result)
    {
        // RunContinuationsAsynchronously keeps the loop (and the next computation) off the UI thread.
        var completion = new TaskCompletionSource<ApplyOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task postTask;
        try
        {
            postTask = _postToUi(() =>
            {
                var outcome = ApplyOutcome.Skipped;
                try
                {
                    lock (_gate)
                    {
                        if (_isClosed || generation != _generation)
                        {
                            return;
                        }
                    }

                    if (revision != _revisionProvider())
                    {
                        outcome = ApplyOutcome.Outdated;
                        return;
                    }

                    _apply(result);
                    outcome = ApplyOutcome.Applied;
                }
                finally
                {
                    completion.TrySetResult(outcome);
                }
            });
        }
        catch (Exception)
        {
            return ApplyOutcome.Failed;
        }

        // A dispatcher shutdown aborts accepted operations without running them, so the wait must
        // also be released by the posted operation completing on its own.
        if (await Task.WhenAny(completion.Task, postTask).ConfigureAwait(false) == postTask &&
            !completion.Task.IsCompleted)
        {
            return ApplyOutcome.Failed;
        }

        return await completion.Task.ConfigureAwait(false);
    }
}
