using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading;
using KeyStats.Models;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class StatsSaveConcurrencyTests
{
    private static readonly DateTime Today = DateTime.Today;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SaveGateSerializesWritesAndTakesTheSnapshotAfterItIsAcquired()
    {
        var harness = SaveHarness.Create();
        try
        {
            harness.CurrentStats.KeyPresses = 11;

            // Materialise the save gate before holding it.
            InvokePrivate(harness.Manager, "SaveStats");
            var gate = GetField<object>(harness.Manager, "_saveGate");
            Assert.IsNotNull(gate);

            var queuedValueWhileBlocked = -1;
            var valueWrittenByQueuedSave = -1;
            var saveThread = new Thread(() =>
            {
                InvokePrivate(harness.Manager, "SaveStats");
                valueWrittenByQueuedSave = ReadStatsFile(harness.StatsPath).KeyPresses;
            });

            lock (gate!)
            {
                // Value written by the first save must stay on disk while a second save waits.
                saveThread.Start();
                Assert.IsTrue(SpinUntil(() => ReadStatsFile(harness.StatsPath).KeyPresses == 11, TimeSpan.FromSeconds(2)));

                Assert.IsTrue(Monitor.TryEnter(harness.StateLock, TimeSpan.FromSeconds(5)),
                    "A pending save must not hold the statistics lock.");
                try
                {
                    harness.CurrentStats.KeyPresses = 27;
                    queuedValueWhileBlocked = ReadStatsFile(harness.StatsPath).KeyPresses;
                }
                finally
                {
                    Monitor.Exit(harness.StateLock);
                }
            }

            saveThread.Join(TimeSpan.FromSeconds(20));
            Assert.IsFalse(saveThread.IsAlive, "Queued save did not finish after the gate was released.");

            Assert.AreEqual(11, queuedValueWhileBlocked, "The queued save must not have written yet.");
            Assert.AreEqual(27, valueWrittenByQueuedSave,
                "The snapshot must be taken after the gate is acquired, otherwise the older value would win.");
            Assert.AreEqual(27, ReadStatsFile(harness.StatsPath).KeyPresses);
        }
        finally
        {
            harness.Cleanup();
        }
    }

    [TestMethod]
    public void StatisticsLockStaysAvailableWhileHistoryIsBeingWritten()
    {
        var harness = SaveHarness.Create(historyDays: 6_000, keysPerDay: 20);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var tmpPath = harness.HistoryPath + ".tmp";
            var saveFinished = false;
            var saveThread = new Thread(() =>
            {
                InvokePrivate(harness.Manager, "SaveHistory");
                saveFinished = true;
            });
            saveThread.Start();

            var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 30.0);
            while (!File.Exists(tmpPath) && !saveFinished && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.SpinWait(200);
            }

            Assert.IsTrue(File.Exists(tmpPath), "The durable temp file was never observed; the write finished too fast to probe.");

            var probes = 0;
            var updates = 0;
            while (!saveFinished && File.Exists(tmpPath) && Stopwatch.GetTimestamp() < deadline)
            {
                probes++;
                if (!Monitor.TryEnter(harness.StateLock, TimeSpan.FromSeconds(10)))
                {
                    Assert.Fail("The statistics lock was held for the whole duration of the disk write.");
                }

                try
                {
                    harness.CurrentStats.KeyPresses++;
                    updates++;
                }
                finally
                {
                    Monitor.Exit(harness.StateLock);
                }

                Thread.Yield();
            }

            saveThread.Join(TimeSpan.FromSeconds(30));
            stopwatch.Stop();
            TestContext.WriteLine(
                $"history write probed {probes} times, {updates} counter updates accepted, total {stopwatch.ElapsedMilliseconds}ms, " +
                $"history.json {new FileInfo(harness.HistoryPath).Length} bytes");

            Assert.IsTrue(saveFinished, "The history save did not complete.");
            Assert.IsTrue(updates > 0, "No counter update was attempted during the write window.");

            InvokePrivate(harness.Manager, "SaveStats");
            Assert.AreEqual(updates, ReadStatsFile(harness.StatsPath).KeyPresses,
                "Updates made during the write must not be lost.");

            var persisted = ReadHistory(harness.HistoryPath);
            Assert.AreEqual(6_000, persisted.Count);
        }
        finally
        {
            harness.Cleanup();
        }
    }

    [TestMethod]
    public void CrossDayInputPersistsWithoutDeadlockAndKeepsBothFilesConsistent()
    {
        var harness = SaveHarness.Create(statisticsDayOffset: -1);
        try
        {
            harness.CurrentStats.KeyPresses = 3;
            var previousRevision = harness.Manager.HistoryRevision;

            var onKeyPressed = typeof(StatsManager).GetMethod("OnKeyPressed", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(onKeyPressed);
            var inputThread = new Thread(() => onKeyPressed!.Invoke(harness.Manager, new object[] { "A", "editor", "Editor" }));
            inputThread.Start();

            // The rollover archives yesterday inside the statistics lock and only then queues the
            // save, so the lock must stay reachable the whole time.
            var lockProbes = 0;
            var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 10.0);
            while (inputThread.IsAlive && Stopwatch.GetTimestamp() < deadline)
            {
                if (!Monitor.TryEnter(harness.StateLock, TimeSpan.FromSeconds(5)))
                {
                    Assert.Fail("Cross-day input blocked the statistics lock.");
                }

                lockProbes++;
                Monitor.Exit(harness.StateLock);
                Thread.Yield();
            }

            inputThread.Join(TimeSpan.FromSeconds(20));
            Assert.IsFalse(inputThread.IsAlive, "Cross-day input deadlocked.");
            Assert.IsTrue(lockProbes > 0);
            Assert.IsTrue(harness.Manager.HistoryRevision > previousRevision, "Rollover must advance the history revision.");
            Assert.AreEqual(Today, harness.Manager.GetCurrentStatsCopy().Date.Date);

            InvokePrivate(harness.Manager, "FlushPendingSave");

            var yesterdayKey = Key(Today.AddDays(-1));
            var history = ReadHistory(harness.HistoryPath);
            Assert.IsTrue(history.ContainsKey(yesterdayKey), "The archived day must be persisted.");
            Assert.AreEqual(3, history[yesterdayKey].KeyPresses, "Yesterday is archived before the new day's counters start.");
            Assert.IsFalse(history[yesterdayKey].KeyPressCounts.ContainsKey("A"));

            var today = ReadStatsFile(harness.StatsPath);
            Assert.AreEqual(Today, today.Date.Date);
            Assert.AreEqual(1, today.KeyPresses);
            Assert.AreEqual(1, today.KeyPressCounts["A"]);
            Assert.AreEqual(1, today.AppStats["editor"].KeyPresses);
        }
        finally
        {
            harness.Cleanup();
        }
    }

    [TestMethod]
    public void ImportAndResetWhileSavesArePendingPersistTheirStateLast()
    {
        var harness = SaveHarness.Create(historyDays: 3);
        try
        {
            InvokePrivate(harness.Manager, "SaveStats");
            var gate = GetField<object>(harness.Manager, "_saveGate")!;

            var queuedSaver = new Thread(() => InvokePrivate(harness.Manager, "SaveHistory"));
            lock (gate)
            {
                queuedSaver.Start();

                // The import runs while an older save is queued behind the gate.
                harness.Manager.ImportStatsData(CreateExportPayload(
                    new DailyStats(Today)
                    {
                        KeyPresses = 5,
                        KeyPressCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["W"] = 5 }
                    },
                    new Dictionary<string, DailyStats>(StringComparer.Ordinal)
                    {
                        [Key(Today.AddDays(-2))] = new DailyStats(Today.AddDays(-2))
                        {
                            KeyPresses = 8,
                            KeyPressCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["Q"] = 8 }
                        }
                    }));
            }

            queuedSaver.Join(TimeSpan.FromSeconds(20));
            Assert.IsFalse(queuedSaver.IsAlive);

            var history = ReadHistory(harness.HistoryPath);
            Assert.AreEqual(2, history.Count, "Imported state must win over the previously queued save.");
            Assert.AreEqual(8, history[Key(Today.AddDays(-2))].KeyPresses);
            Assert.AreEqual(5, ReadStatsFile(harness.StatsPath).KeyPresses);
            CollectionAssert.AreEqual(
                new[] { ("Q", 8), ("W", 5) },
                harness.Manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All).ToList());

            // Reset must survive the same race.
            harness.Manager.ResetStats();
            Assert.AreEqual(0, ReadStatsFile(harness.StatsPath).KeyPresses);
            history = ReadHistory(harness.HistoryPath);
            Assert.AreEqual(5, history[Key(Today)].KeyPresses, "Reset archives the counters it clears.");
            Assert.AreEqual(0, harness.Manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.Today).Count);
        }
        finally
        {
            harness.Cleanup();
        }
    }

    [TestMethod]
    public void SavesQueuedBehindTheShutdownFlushAreDropped()
    {
        var harness = SaveHarness.Create();
        try
        {
            var manager = harness.Manager;
            harness.CurrentStats.KeyPresses = 42;
            InvokePrivate(manager, "SaveStats");
            var gate = GetField<object>(manager, "_saveGate")!;

            var queued = new[]
            {
                new Thread(() => InvokePrivate(manager, "SaveStats")),
                new Thread(() => InvokePrivate(manager, "SaveHistory")),
                new Thread(() => InvokePrivate(manager, "FlushSettings"))
            };

            lock (gate)
            {
                foreach (var thread in queued)
                {
                    thread.Start();
                }

                Assert.IsTrue(SpinUntil(() => queued.All(thread => thread.IsAlive), TimeSpan.FromSeconds(2)));

                // Shutdown wins the gate while the ordinary saves are queued behind it.
                manager.Dispose();

                // Everything that would make a late save write different data than the exit state.
                harness.CurrentStats.KeyPresses = 999;
                harness.CurrentStats.KeyPressCounts["A"] = 7;
                harness.History[Key(Today.AddDays(-1))] = new DailyStats(Today.AddDays(-1)) { KeyPresses = 123 };
                SetField(manager, "<Settings>k__BackingField", new AppSettings { MainWindowWidth = 4_321 });
            }

            foreach (var thread in queued)
            {
                thread.Join(TimeSpan.FromSeconds(20));
                Assert.IsFalse(thread.IsAlive);
            }

            Assert.AreEqual(42, ReadStatsFile(harness.StatsPath).KeyPresses,
                "A save queued behind the shutdown flush must not write the newer counters.");
            var history = ReadHistory(harness.HistoryPath);
            Assert.IsFalse(history.ContainsKey(Key(Today.AddDays(-1))),
                "A history save queued behind the shutdown flush must not add days after exit.");
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(harness.SettingsPath))!;
            Assert.IsNull(settings.MainWindowWidth, "A settings save queued behind the shutdown flush must be dropped.");
        }
        finally
        {
            harness.Cleanup();
        }
    }

    [TestMethod]
    public void DisposeWritesFinalStateAndLateWorkCannotOverwriteIt()
    {
        var harness = SaveHarness.Create();
        var manager = harness.Manager;
        harness.CurrentStats.KeyPresses = 42;
        harness.CurrentStats.LeftClicks = 7;

        // Queue a debounced save so a timer callback is scheduled when the app shuts down.
        InvokePrivate(manager, "ScheduleSave");
        manager.Dispose();

        Assert.IsFalse(SaveHarness.TimersAlive(manager), "Save timers must be disposed on exit.");

        var final = ReadStatsFile(harness.StatsPath);
        Assert.AreEqual(42, final.KeyPresses);
        Assert.AreEqual(7, final.LeftClicks);
        Assert.IsTrue(File.Exists(harness.SettingsPath));
        Assert.AreEqual(1, ReadHistory(harness.HistoryPath).Count, "Shutdown must flush today's mirror into history.json.");

        // Late input and late saves after the shutdown flush must not change the persisted state.
        var onKeyPressed = typeof(StatsManager).GetMethod("OnKeyPressed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(onKeyPressed);
        onKeyPressed!.Invoke(manager, new object[] { "A", "editor", "Editor" });

        // Even a save that slips through after Dispose must not overwrite the exit snapshot.
        harness.CurrentStats.KeyPresses = 999;
        harness.CurrentStats.KeyPressCounts["A"] = 5;
        InvokePrivate(manager, "SaveStats");
        InvokePrivate(manager, "SaveHistory");
        InvokePrivate(manager, "FlushSettings");
        InvokePrivate(manager, "FlushPendingSave");
        Thread.Sleep(300);

        Assert.AreEqual(42, ReadStatsFile(harness.StatsPath).KeyPresses, "Post-shutdown work must not rewrite the exit snapshot.");
        Assert.AreEqual(42, ReadHistory(harness.HistoryPath)[Key(Today)].KeyPresses);
    }

    [TestMethod]
    public void SettingsSaveUsesADetachedSnapshotWithoutChangingTheJsonShape()
    {
        var harness = SaveHarness.Create();
        try
        {
            var settings = new AppSettings
            {
                MainWindowWidth = 1_234.5,
                KeyHistoryWindowWidth = 456,
                MouseDistanceUnit = "px",
                LanguagePreference = "zh-Hans",
                FloatingStatsFontSize = 18,
                AnalyticsFirstOpenUtc = new DateTime(2026, 5, 1, 2, 3, 4, DateTimeKind.Utc)
            };
            SetField(harness.Manager, "<Settings>k__BackingField", settings);

            InvokePrivate(harness.Manager, "FlushSettings");
            var written = File.ReadAllText(harness.SettingsPath);
            StringAssert.Contains(written, "\"mainWindowWidth\": 1234.5");
            StringAssert.Contains(written, "\"keyHistoryWindowWidth\": 456");
            StringAssert.Contains(written, "\"mouseDistanceUnit\": \"px\"");
            StringAssert.Contains(written, "\"languagePreference\": \"zh-Hans\"");

            var deserialized = JsonSerializer.Deserialize<AppSettings>(written)!;
            Assert.AreEqual(1_234.5, deserialized.MainWindowWidth);
            Assert.AreEqual(456d, deserialized.KeyHistoryWindowWidth);
            Assert.AreEqual("px", deserialized.MouseDistanceUnit);
            Assert.AreEqual("zh-Hans", deserialized.LanguagePreference);
            Assert.AreEqual(18, deserialized.FloatingStatsFontSize);
            Assert.AreEqual(new DateTime(2026, 5, 1, 2, 3, 4, DateTimeKind.Utc), deserialized.AnalyticsFirstOpenUtc);

            // The snapshot is taken after the gate, so a queued settings save writes the newest
            // values and cannot be overtaken by an older snapshot.
            InvokePrivate(harness.Manager, "SaveStats");
            var gate = GetField<object>(harness.Manager, "_saveGate")!;
            string? captured = null;
            var writer = new Thread(() =>
            {
                InvokePrivate(harness.Manager, "FlushSettings");
                captured = File.ReadAllText(harness.SettingsPath);
            });

            lock (gate)
            {
                writer.Start();
                Assert.IsTrue(Monitor.TryEnter(harness.StateLock, TimeSpan.FromSeconds(5)),
                    "A pending settings save must not hold the statistics lock.");
                Monitor.Exit(harness.StateLock);
                settings.MainWindowWidth = 111;
            }

            writer.Join(TimeSpan.FromSeconds(20));
            settings.MainWindowWidth = 222;

            Assert.IsNotNull(captured);
            var reloaded = JsonSerializer.Deserialize<AppSettings>(captured!)!;
            Assert.AreEqual(111d, reloaded.MainWindowWidth, "The queued settings save must write the value read after the gate.");
            Assert.AreEqual(456d, reloaded.KeyHistoryWindowWidth);
            Assert.AreEqual(18, reloaded.FloatingStatsFontSize);
        }
        finally
        {
            harness.Cleanup();
        }
    }

    #region Harness

    private sealed class SaveHarness
    {
        private SaveHarness(string directory, StatsManager manager, DailyStats currentStats, Dictionary<string, DailyStats> history)
        {
            Directory = directory;
            Manager = manager;
            CurrentStats = currentStats;
            History = history;
        }

        public string Directory { get; }
        public StatsManager Manager { get; }
        public DailyStats CurrentStats { get; }
        public Dictionary<string, DailyStats> History { get; }
        public object StateLock => GetField<object>(Manager, "_lock")!;
        public string StatsPath => Path.Combine(Directory, "daily_stats.json");
        public string HistoryPath => Path.Combine(Directory, "history.json");
        public string SettingsPath => Path.Combine(Directory, "settings.json");

        public static SaveHarness Create(int historyDays = 0, int keysPerDay = 0, int statisticsDayOffset = 0)
        {
            var directory = new TestDirectory();
            var manager = (StatsManager)FormatterServices.GetUninitializedObject(typeof(StatsManager));
            var currentStats = new DailyStats(Today.AddDays(statisticsDayOffset));
            var history = new Dictionary<string, DailyStats>(StringComparer.Ordinal);

            for (var offset = 1; offset <= historyDays; offset++)
            {
                var date = Today.AddDays(-offset);
                var day = new DailyStats(date) { KeyPresses = offset % 97 + 1, PeakKPS = offset % 5 };
                for (var key = 0; key < keysPerDay; key++)
                {
                    day.KeyPressCounts["Key-" + key] = key + 1;
                }

                history[Key(date)] = day;
            }

            // Reflection-created instances skip field initializers, so every lock and mutable
            // collection the persistence path touches has to be provided explicitly.
            SetField(manager, "_lock", new object());
            SetField(manager, "_midnightTimerLock", new object());
            SetField(manager, "_recentKeyTimestamps", new Queue<DateTime>());
            SetField(manager, "_recentClickTimestamps", new Queue<DateTime>());
            SetField(manager, "<Settings>k__BackingField", new AppSettings());
            SetField(manager, "<CurrentStats>k__BackingField", currentStats);
            SetField(manager, "<History>k__BackingField", history);
            SetField(manager, "_statsFilePath", Path.Combine(directory.Path, "daily_stats.json"));
            SetField(manager, "_historyFilePath", Path.Combine(directory.Path, "history.json"));
            SetField(manager, "_settingsFilePath", Path.Combine(directory.Path, "settings.json"));
            SetField(manager, "_saveInterval", 200.0);

            return new SaveHarness(directory.Path, manager, currentStats, history);
        }

        public static bool TimersAlive(StatsManager manager) =>
            TimerField(manager, "_saveTimer") != null ||
            TimerField(manager, "_statsUpdateTimer") != null ||
            TimerField(manager, "_mouseMoveUpdateTimer") != null ||
            TimerField(manager, "_settingsSaveTimer") != null ||
            TimerField(manager, "_midnightTimer") != null;

        public void Cleanup()
        {
            SetField(Manager, "_isDisposed", true);
            foreach (var name in new[] { "_saveTimer", "_statsUpdateTimer", "_mouseMoveUpdateTimer", "_settingsSaveTimer", "_midnightTimer" })
            {
                TimerField(Manager, name)?.Dispose();
            }

            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private static System.Timers.Timer? TimerField(StatsManager manager, string name) =>
            GetField<System.Timers.Timer?>(manager, name);
    }

    #endregion

    #region Helpers

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return condition();
    }

    private static string ReadAllTextRobust(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(10);
            }
        }
    }

    private static DailyStats ReadStatsFile(string path) =>
        JsonSerializer.Deserialize<DailyStats>(ReadAllTextRobust(path))!;

    private static Dictionary<string, DailyStats> ReadHistory(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, DailyStats>>(ReadAllTextRobust(path))!;

    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static byte[] CreateExportPayload(DailyStats current, Dictionary<string, DailyStats> history) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Version = 1,
                Scope = "currentDevice",
                ExportedAt = DateTime.UtcNow,
                CurrentStats = current,
                History = history
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "Missing test setup field: " + name);
        field!.SetValue(target, value);
    }

    private static T? GetField<T>(object target, string name) =>
        (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

    private static object? InvokePrivate(object target, string name)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.IsNotNull(method, "Missing test setup method: " + name);
        return method!.Invoke(target, Array.Empty<object>());
    }

    #endregion
}
