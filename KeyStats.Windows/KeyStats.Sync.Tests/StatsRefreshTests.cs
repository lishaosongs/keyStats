using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using KeyStats.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public class StatsRefreshTests
{
    [TestMethod]
    public void Request_ComputesOffUiThreadAndAppliesOnUiThread()
    {
        using var ui = new FakeUiThread();
        var applied = new List<string>();
        var computeThreadId = 0;
        var applyThreadId = 0;

        var refresher = new BackgroundStatsRefresher<string>(
            ui.Post,
            () => 0,
            () =>
            {
                computeThreadId = Thread.CurrentThread.ManagedThreadId;
                return "today";
            },
            result =>
            {
                applyThreadId = Thread.CurrentThread.ManagedThreadId;
                applied.Add(result);
            });

        refresher.Request();

        WaitFor(() => applied.Count == 1, "The refresh result was never applied.");
        Assert.AreNotEqual(ui.ManagedThreadId, computeThreadId, "The computation must not run on the UI thread.");
        Assert.AreEqual(ui.ManagedThreadId, applyThreadId, "The result must be applied on the UI thread.");
        Assert.AreEqual("today", applied[0]);
    }

    [TestMethod]
    public void ChangedConditions_ApplyOnlyNewestSelection()
    {
        using var ui = new FakeUiThread();
        var applied = new List<string>();
        var computeCount = 0;
        var selection = 1;
        using var firstComputeStarted = new ManualResetEventSlim(false);
        using var releaseFirstCompute = new ManualResetEventSlim(false);

        var refresher = new BackgroundStatsRefresher<string>(
            ui.Post,
            () => 0,
            () =>
            {
                var captured = selection;
                if (Interlocked.Increment(ref computeCount) == 1)
                {
                    firstComputeStarted.Set();
                    releaseFirstCompute.Wait(TimeSpan.FromSeconds(5));
                }

                return "selection-" + captured;
            },
            applied.Add);

        refresher.Request(conditionsChanged: true);
        Assert.IsTrue(firstComputeStarted.Wait(TimeSpan.FromSeconds(5)), "The first computation never started.");

        selection = 2;
        refresher.Request(conditionsChanged: true);
        selection = 3;
        refresher.Request(conditionsChanged: true);
        releaseFirstCompute.Set();

        WaitFor(() => applied.Count == 1, "The newest selection was never applied.");
        Assert.AreEqual("selection-3", applied[0], "A result computed for an outdated selection was applied.");
        Assert.AreEqual(2, computeCount, "Outdated selections should be discarded instead of recomputed one by one.");
    }

    [TestMethod]
    public void CountOnlyRequests_StillApplyAndNeverRunTwoComputationsAtOnce()
    {
        using var ui = new FakeUiThread();
        var applied = 0;
        var computeCount = 0;
        var concurrentComputations = 0;
        var maxConcurrentComputations = 0;
        using var firstComputeStarted = new ManualResetEventSlim(false);
        using var releaseFirstCompute = new ManualResetEventSlim(false);

        var refresher = new BackgroundStatsRefresher<int>(
            ui.Post,
            () => 0,
            () =>
            {
                var running = Interlocked.Increment(ref concurrentComputations);
                InterlockedMax(ref maxConcurrentComputations, running);
                var isFirst = Interlocked.Increment(ref computeCount) == 1;
                try
                {
                    if (isFirst)
                    {
                        firstComputeStarted.Set();
                        releaseFirstCompute.Wait(TimeSpan.FromSeconds(5));
                    }

                    return running;
                }
                finally
                {
                    Interlocked.Decrement(ref concurrentComputations);
                }
            },
            _ => Interlocked.Increment(ref applied));

        refresher.Request();
        Assert.IsTrue(firstComputeStarted.Wait(TimeSpan.FromSeconds(5)), "The first computation never started.");

        for (var i = 0; i < 64; i++)
        {
            refresher.Request();
        }

        releaseFirstCompute.Set();

        WaitFor(() => Volatile.Read(ref applied) == 2, "Continuous count notifications starved the applied result.");
        Assert.AreEqual(2, computeCount, "Count-only notifications should collapse into a single pending computation.");
        Assert.AreEqual(1, maxConcurrentComputations, "More than one computation ran at the same time.");
    }

    [TestMethod]
    public void CloseWhileWaitingForUi_DiscardsResultAndIgnoresLaterRequests()
    {
        using var ui = new FakeUiThread();
        var applied = 0;
        var computeCount = 0;

        var refresher = new BackgroundStatsRefresher<string>(
            ui.Post,
            () => 0,
            () =>
            {
                Interlocked.Increment(ref computeCount);
                return "stale";
            },
            _ => Interlocked.Increment(ref applied));

        ui.Pause();
        refresher.Request();
        Assert.IsTrue(ui.WaitForBlockedPost(), "The result was never posted to the UI thread.");

        refresher.Close();
        ui.Resume();
        ui.Drain();

        Assert.AreEqual(0, Volatile.Read(ref applied), "A closed refresher must not apply results anymore.");

        refresher.Request();
        ui.Drain();
        Assert.AreEqual(1, Volatile.Read(ref computeCount), "A closed refresher must not start new computations.");
    }

    [TestMethod]
    public void HistoryRevisionChange_DiscardsStaleResultAndRefetches()
    {
        using var ui = new FakeUiThread();
        var applied = new List<string>();
        var computeCount = 0;
        long revision = 1;
        using var firstComputeStarted = new ManualResetEventSlim(false);
        using var releaseFirstCompute = new ManualResetEventSlim(false);

        var refresher = new BackgroundStatsRefresher<string>(
            ui.Post,
            () => Interlocked.Read(ref revision),
            () =>
            {
                var captured = Interlocked.Read(ref revision);
                if (Interlocked.Increment(ref computeCount) == 1)
                {
                    firstComputeStarted.Set();
                    releaseFirstCompute.Wait(TimeSpan.FromSeconds(5));
                }

                return "revision-" + captured;
            },
            applied.Add);

        refresher.Request();
        Assert.IsTrue(firstComputeStarted.Wait(TimeSpan.FromSeconds(5)), "The first computation never started.");

        Interlocked.Exchange(ref revision, 2);
        releaseFirstCompute.Set();

        WaitFor(() => applied.Count == 1, "The result for the current history revision was never applied.");
        Assert.AreEqual("revision-2", applied[0], "A result computed before the history revision changed was applied.");
        Assert.AreEqual(2, computeCount, "The outdated result should trigger exactly one fresh computation.");
    }

    [TestMethod]
    public void HiddenConsumer_DeclinesApplyAndStillRefreshesWhenRequestedAgain()
    {
        using var ui = new FakeUiThread();
        var applied = new List<string>();
        var visible = false;

        var refresher = new BackgroundStatsRefresher<string>(
            ui.Post,
            () => 0,
            () => "today",
            result =>
            {
                if (!visible)
                {
                    return;
                }

                applied.Add(result);
            });

        refresher.Request();
        ui.Drain();
        Assert.AreEqual(0, applied.Count, "A hidden consumer must not receive results.");

        visible = true;
        refresher.Request();
        WaitFor(() => applied.Count == 1, "The visible consumer was not refreshed after being shown again.");
        Assert.AreEqual("today", applied[0]);
    }

    [TestMethod]
    public void ComputeFailure_DoesNotWedgeTheRefresher()
    {
        using var ui = new FakeUiThread();
        var applied = 0;
        var computeCount = 0;
        var shouldThrow = true;

        var refresher = new BackgroundStatsRefresher<string>(
            ui.Post,
            () => 0,
            () =>
            {
                Interlocked.Increment(ref computeCount);
                if (shouldThrow)
                {
                    throw new InvalidOperationException("boom");
                }

                return "recovered";
            },
            _ => Interlocked.Increment(ref applied));

        refresher.Request();
        WaitFor(() => Volatile.Read(ref computeCount) >= 1, "The failing computation never ran.");

        shouldThrow = false;
        refresher.Request();
        WaitFor(() => Volatile.Read(ref applied) == 1, "The refresher did not recover from a failed computation.");
    }

    [TestMethod]
    public void UnavailableUiCallback_EndsTheRunWithoutHangingOrApplying()
    {
        var applied = 0;
        var postCount = 0;
        var computeCount = 0;

        var refresher = new BackgroundStatsRefresher<string>(
            _ =>
            {
                Interlocked.Increment(ref postCount);
                return Task.CompletedTask;
            },
            () => 0,
            () =>
            {
                Interlocked.Increment(ref computeCount);
                return "value";
            },
            _ => Interlocked.Increment(ref applied));

        refresher.Request();
        WaitFor(() => Volatile.Read(ref postCount) == 1, "The result was never posted to the UI thread.");

        // A later request must start a fresh run: the failed UI post released the single-run gate,
        // and the earlier run neither hung nor retried on its own.
        refresher.Request();
        WaitFor(() => Volatile.Read(ref postCount) == 2, "The refresher stayed blocked after the UI post failed.");

        Assert.AreEqual(0, Volatile.Read(ref applied), "Nothing can be applied without a UI callback.");
        Assert.AreEqual(2, Volatile.Read(ref computeCount), "A failed UI post must not trigger extra computations.");
    }

    [TestMethod]
    public void ThrowingUiCallback_EndsTheRunWithoutHanging()
    {
        var computeCount = 0;

        var refresher = new BackgroundStatsRefresher<string>(
            _ => throw new InvalidOperationException("dispatcher shut down"),
            () => 0,
            () =>
            {
                Interlocked.Increment(ref computeCount);
                return "value";
            },
            _ => { });

        refresher.Request();
        WaitFor(() => Volatile.Read(ref computeCount) == 1, "The computation never ran.");

        refresher.Request();
        WaitFor(() => Volatile.Read(ref computeCount) == 2, "The refresher stayed blocked after the UI post threw.");
    }

    [TestMethod]
    public void ApplyFailure_DoesNotWedgeTheRefresher()
    {
        using var ui = new FakeUiThread();
        var applied = 0;
        var shouldThrow = true;

        var refresher = new BackgroundStatsRefresher<string>(
            ui.Post,
            () => 0,
            () => "value",
            _ =>
            {
                if (shouldThrow)
                {
                    throw new InvalidOperationException("apply failed");
                }

                Interlocked.Increment(ref applied);
            });

        refresher.Request();
        WaitFor(() => ui.ErrorCount == 1, "The apply exception never reached the UI thread.");

        shouldThrow = false;
        refresher.Request();
        WaitFor(() => Volatile.Read(ref applied) == 1, "The refresher did not recover after an apply failure.");
    }

    [TestMethod]
    public void ShutDownDispatcher_AbortsAcceptedOperationAndReleasesTheWait()
    {
        using var dispatcher = new DispatcherHost();
        var computeCount = 0;
        var applied = 0;

        var refresher = new BackgroundStatsRefresher<string>(
            action => dispatcher.Post(action),
            () => 0,
            () =>
            {
                Interlocked.Increment(ref computeCount);
                return "value";
            },
            _ => Interlocked.Increment(ref applied));

        refresher.Request();
        WaitFor(() => Volatile.Read(ref computeCount) == 1, "The computation never ran.");
        Assert.IsTrue(dispatcher.WaitForAcceptedPost(), "The operation was never accepted by the dispatcher.");

        // The operation is queued but the dispatcher is blocked; shutting it down aborts the
        // accepted operation, which must release the refresher instead of leaving it waiting.
        dispatcher.Shutdown();

        refresher.Request();
        WaitFor(() => Volatile.Read(ref computeCount) == 2, "The refresher stayed blocked after the dispatcher shut down.");
        Assert.AreEqual(0, Volatile.Read(ref applied), "Nothing can be applied by a shut-down dispatcher.");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private static void WaitFor(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail(message);
    }

    /// <summary>
    /// A real WPF dispatcher on its own STA thread, blocked so queued operations stay accepted
    /// but unexecuted; used to verify that a dispatcher shutdown releases accepted posts.
    /// </summary>
    private sealed class DispatcherHost : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new(false);
        private readonly ManualResetEventSlim _blocking = new(false);
        private readonly ManualResetEventSlim _gate = new(false);
        private readonly ManualResetEventSlim _accepted = new(false);
        private Dispatcher? _dispatcher;

        public DispatcherHost()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.IsTrue(_started.Wait(TimeSpan.FromSeconds(5)), "The dispatcher thread did not start.");
            Assert.IsTrue(_blocking.Wait(TimeSpan.FromSeconds(5)), "The dispatcher was not blocked.");
        }

        public Task Post(Action action)
        {
            var operation = _dispatcher!.BeginInvoke(DispatcherPriority.Background, action);
            _accepted.Set();
            return operation.Task;
        }

        public bool WaitForAcceptedPost() => _accepted.Wait(TimeSpan.FromSeconds(5));

        public void Shutdown()
        {
            // The dispatcher thread is blocked inside a Send-priority operation, so a synchronous
            // InvokeShutdown would wait on that thread. Queue the shutdown (higher priority than the
            // Background post) and then release the gate so the owner thread aborts queued operations.
            _dispatcher!.BeginInvokeShutdown(DispatcherPriority.Send);
            _gate.Set();
        }

        public void Dispose()
        {
            _gate.Set();
            try
            {
                _dispatcher?.InvokeShutdown();
            }
            catch (InvalidOperationException)
            {
            }

            _thread.Join(TimeSpan.FromSeconds(5));
            _started.Dispose();
            _blocking.Dispose();
            _gate.Dispose();
            _accepted.Dispose();
        }

        private void Run()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            // Occupy the dispatcher so subsequently queued operations are accepted but not run.
            _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                _started.Set();
                _blocking.Set();
                _gate.Wait();
            }));
            Dispatcher.Run();
        }
    }

    /// <summary>
    /// Stands in for the WPF dispatcher queue: posted actions run on a dedicated thread,
    /// and <see cref="Pause"/> holds them until <see cref="Resume"/>.
    /// </summary>
    private sealed class FakeUiThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _gate = new(true);
        private readonly ManualResetEventSlim _blockedPost = new(false);
        private readonly List<Exception> _errors = new();

        public FakeUiThread()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public int ManagedThreadId => _thread.ManagedThreadId;

        public int ErrorCount
        {
            get
            {
                lock (_errors)
                {
                    return _errors.Count;
                }
            }
        }

        public Task Post(Action action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    action();
                }
                finally
                {
                    completion.TrySetResult(true);
                }
            });
            return completion.Task;
        }

        public void Pause()
        {
            _blockedPost.Reset();
            _gate.Reset();
        }

        public void Resume() => _gate.Set();

        public bool WaitForBlockedPost() => _blockedPost.Wait(TimeSpan.FromSeconds(5));

        /// <summary>
        /// Blocks until every action posted so far has run; the queue is drained in order.
        /// </summary>
        public void Drain()
        {
            if (!_gate.IsSet)
            {
                throw new InvalidOperationException("Drain cannot run while paused.");
            }

            using var drained = new ManualResetEventSlim(false);
            _queue.Add(drained.Set);
            Assert.IsTrue(drained.Wait(TimeSpan.FromSeconds(5)), "The UI queue did not drain.");
        }

        public void Dispose()
        {
            _gate.Set();
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
            _gate.Dispose();
            _blockedPost.Dispose();
        }

        private void Run()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                if (!_gate.IsSet)
                {
                    _blockedPost.Set();
                    _gate.Wait();
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // Mirrors a dispatcher that survives a handled callback exception.
                    lock (_errors)
                    {
                        _errors.Add(ex);
                    }
                }
            }
        }
    }
}
