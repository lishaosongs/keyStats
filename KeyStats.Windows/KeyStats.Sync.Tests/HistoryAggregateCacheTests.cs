using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using KeyStats.Models;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class HistoryAggregateCacheTests
{
    private static readonly DateTime Today = DateTime.Today;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TwoYearHistory_MatchesReferenceAggregates()
    {
        var data = SyntheticHistory.Create(dayCount: 730, statisticsDayOffset: 0);
        var manager = data.CreateManager(out _);

        AssertHistoricalKeyCounts(manager, data, data.KeyHistoryDates(null), StatsManager.KeyHistoryRange.All);
        AssertHistoricalKeyCounts(manager, data, data.LastNDays(data.TrendDates(data.MinHistoryDate), 30), StatsManager.KeyHistoryRange.Month);
        AssertHistoricalKeyCounts(manager, data, data.LastNDays(data.TrendDates(data.MinHistoryDate), 7), StatsManager.KeyHistoryRange.Week);
        AssertHistoricalKeyCounts(manager, data, new[] { Today }, StatsManager.KeyHistoryRange.Today);

        AssertAppSummary(manager, data, data.AppStatsDates(), StatsManager.AppStatsRange.All);
        AssertAppSummary(manager, data, data.LastNDays(data.TrendDates(data.MinHistoryDate), 30), StatsManager.AppStatsRange.Month);
        AssertAppSummary(manager, data, new[] { Today }, StatsManager.AppStatsRange.Today);

        AssertHeatmapSummary(manager, data, data.TrendDates(data.HeatmapStart(null)), StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Sum);
        AssertHeatmapSummary(manager, data, data.TrendDates(data.HeatmapStart(null)), StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Average);
        AssertHeatmapSummary(manager, data, data.LastNDays(data.TrendDates(data.MinHistoryDate), 7), StatsManager.KeyboardHeatmapRange.Week, StatsManager.KeyboardHeatmapMode.Average);

        foreach (var metric in new[]
                 {
                     StatsManager.HistoryMetric.KeyPresses,
                     StatsManager.HistoryMetric.Clicks,
                     StatsManager.HistoryMetric.MouseDistance,
                     StatsManager.HistoryMetric.ScrollDistance
                 })
        {
            AssertTrendSeries(manager, data, data.TrendDates(data.MinHistoryDate), metric);
        }

        AssertInputTotals(manager, data, data.TrendDates(data.MouseTotalsStart(null)));
        Assert.AreEqual(ReferencePeaks(data, data.TrendDates(data.MinHistoryDate)), manager.GetAllTimePeakRates());
    }

    [TestMethod]
    public void DayRolloverPending_LiveDaysStayOutsideCachedPrefix()
    {
        // CurrentStats still holds yesterday while the wall clock has moved on: the day being
        // changed must stay outside the cached prefix and be re-read on every query.
        var statisticsDay = Today.AddDays(-1);
        var data = SyntheticHistory.Create(dayCount: 60, statisticsDayOffset: -1);
        var manager = data.CreateManager(out _);

        Assert.AreEqual(statisticsDay, data.Current.Date.Date);
        Assert.IsFalse(data.History.ContainsKey(Key(statisticsDay)));

        AssertHistoricalKeyCounts(manager, data, data.KeyHistoryDates(null), StatsManager.KeyHistoryRange.All);
        AssertHistoricalKeyCounts(manager, data, data.LastNDays(data.TrendDates(data.MinHistoryDate), 7), StatsManager.KeyHistoryRange.Week);
        AssertAppSummary(manager, data, data.AppStatsDates(), StatsManager.AppStatsRange.All);
        AssertHeatmapSummary(manager, data, data.TrendDates(data.HeatmapStart(null)), StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Sum);
        AssertTrendSeries(manager, data, data.TrendDates(data.MinHistoryDate), StatsManager.HistoryMetric.KeyPresses);
        AssertInputTotals(manager, data, data.TrendDates(data.MouseTotalsStart(null)));
        Assert.AreEqual(ReferencePeaks(data, data.TrendDates(data.MinHistoryDate)), manager.GetAllTimePeakRates());

        // The cached key prefix must equal the reference over the stable days only, i.e. it must
        // not contain the live statistics day (yesterday) nor the new calendar day (today).
        var entry = GetEntry(manager, "_keyHistoryCache", StatsManager.KeyHistoryRange.All);
        Assert.IsNotNull(entry);
        var cached = (Dictionary<string, int>)GetEntryValue(entry);
        var stableDates = data.TrendDates(data.MinHistoryDate)
            .Where(date => date < statisticsDay)
            .Concat(new[] { Today })
            .ToList();
        var stableTotals = ReferenceKeyTotals(data, stableDates);
        Assert.AreEqual(stableTotals.Count, cached.Count);
        foreach (var pair in stableTotals)
        {
            Assert.AreEqual(pair.Value, cached[pair.Key], "cached prefix value for " + pair.Key);
        }

        // Yesterday is the still-changing statistics day, so it only shows up through the
        // week range and never inside the cached prefix.
        var revisionBefore = manager.HistoryRevision;
        var weekDates = data.LastNDays(data.TrendDates(data.MinHistoryDate), 7);
        data.Current.KeyPresses++;
        data.Current.KeyPressCounts["Z"] = 1;

        var changedWeek = manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.Week);
        CollectionAssert.AreEqual(ReferenceKeyCounts(data, weekDates), changedWeek, "live day must be re-read");
        Assert.AreEqual(1, changedWeek.Single(x => x.Key == "Z").Count);
        Assert.AreEqual(0, manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All).Count(x => x.Key == "Z"),
            "the live statistics day must not leak into the cached prefix");
        Assert.AreEqual(revisionBefore, manager.HistoryRevision);
        Assert.AreSame(entry, GetEntry(manager, "_keyHistoryCache", StatsManager.KeyHistoryRange.All),
            "Changing the live day must not rebuild the stable prefix.");
    }

    [TestMethod]
    public void RepeatQuery_ReusesCachedHistoryButKeepsTodayLive()
    {
        var data = SyntheticHistory.Create(dayCount: 120, statisticsDayOffset: 0);
        var manager = data.CreateManager(out _);
        var dates = data.TrendDates(data.MinHistoryDate);

        CollectionAssert.AreEqual(ReferenceKeyCounts(data, data.KeyHistoryDates(null)), manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All));
        var revisionAfterFirst = manager.HistoryRevision;
        var keyEntry = GetEntry(manager, "_keyHistoryCache", StatsManager.KeyHistoryRange.All);
        Assert.IsNotNull(keyEntry);

        Assert.AreEqual(ReferencePeaks(data, dates), manager.GetAllTimePeakRates());
        var peakEntry = GetEntryObject(manager, "_peakCache");
        Assert.IsNotNull(peakEntry);

        data.Current.KeyPresses++;
        data.Current.KeyPressCounts["Z"] = 1;

        var second = manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All);
        Assert.AreEqual(1, second.Single(x => x.Key == "Z").Count);
        Assert.AreEqual(revisionAfterFirst, manager.HistoryRevision);
        Assert.AreSame(keyEntry, GetEntry(manager, "_keyHistoryCache", StatsManager.KeyHistoryRange.All),
            "Today's counters changing must not rebuild the historical prefix.");

        // The 2s mirror of today into History (and the stats file write) must not invalidate history.
        InvokePrivate(manager, "SaveStats");
        Assert.IsTrue(data.History.ContainsKey(Key(Today)));
        Assert.AreEqual(revisionAfterFirst, manager.HistoryRevision);
        Assert.AreSame(keyEntry, GetEntry(manager, "_keyHistoryCache", StatsManager.KeyHistoryRange.All));

        data.Current.PeakKPS = 9_999;
        Assert.AreEqual(9_999, manager.GetAllTimePeakRates().PeakKPS);
        Assert.AreSame(peakEntry, GetEntryObject(manager, "_peakCache"));
    }

    [TestMethod]
    public void InvalidationEvents_ForceHistoryAggregatesToRebuild()
    {
        var data = SyntheticHistory.Create(dayCount: 45, statisticsDayOffset: 0);
        var manager = data.CreateManager(out var directory);
        var dates = data.KeyHistoryDates(null);
        var all = StatsManager.KeyHistoryRange.All;

        CollectionAssert.AreEqual(ReferenceKeyCounts(data, dates), manager.GetHistoricalKeyCounts(all));
        var entryBefore = GetEntry(manager, "_keyHistoryCache", all);
        Assert.IsNotNull(entryBefore);
        var revisionBefore = manager.HistoryRevision;

        // 1. Remote data changed.
        manager.NotifyRemoteStatsChanged();
        AssertInvalidated(manager, entryBefore, revisionBefore, () =>
            CollectionAssert.AreEqual(ReferenceKeyCounts(data, data.KeyHistoryDates(null)), manager.GetHistoricalKeyCounts(all)));
        entryBefore = GetEntry(manager, "_keyHistoryCache", all);
        revisionBefore = manager.HistoryRevision;

        // 2. Sync display configured.
        using var remoteDirectory = new TestDirectory();
        var aggregator = new DisplayStatsAggregator(new RemoteShardCache(remoteDirectory.Path), () => "local-device");
        manager.ConfigureSyncDisplay(aggregator, () => false);
        AssertInvalidated(manager, entryBefore, revisionBefore, () =>
            CollectionAssert.AreEqual(ReferenceKeyCounts(data, data.KeyHistoryDates(null)), manager.GetHistoricalKeyCounts(all)));
        entryBefore = GetEntry(manager, "_keyHistoryCache", all);
        revisionBefore = manager.HistoryRevision;

        // 3. Day rollover archives the previous statistics day.
        var rollover = typeof(StatsManager).GetMethod("SynchronizeCurrentDay", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(rollover);
        rollover!.Invoke(manager, new object[] { Today.AddDays(1), false });
        data.Current = manager.GetCurrentStatsCopy();
        AssertInvalidated(manager, entryBefore, revisionBefore, () =>
            CollectionAssert.AreEqual(
                ReferenceKeyCounts(data, data.KeyHistoryDates(null), statisticsDay: Today.AddDays(1)),
                manager.GetHistoricalKeyCounts(all)));
        entryBefore = GetEntry(manager, "_keyHistoryCache", all);
        revisionBefore = manager.HistoryRevision;

        // 4. Reset archives today and starts a fresh day.
        manager.ResetStats();
        Assert.AreEqual(0, manager.GetCurrentStatsCopy().KeyPresses);
        AssertInvalidated(manager, entryBefore, revisionBefore, () =>
        {
            CollectionAssert.AreEqual(
                ReferenceKeyCounts(data, data.KeyHistoryDates(null)),
                manager.GetHistoricalKeyCounts(all));
            Assert.AreEqual(0, manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.Today).Count);
        });
        entryBefore = GetEntry(manager, "_keyHistoryCache", all);
        revisionBefore = manager.HistoryRevision;

        // 5. Import overwrite replaces the whole history.
        var imported = new Dictionary<string, DailyStats>(StringComparer.Ordinal)
        {
            [Key(Today.AddDays(-3))] = new DailyStats(Today.AddDays(-3))
            {
                KeyPresses = 4,
                KeyPressCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["Q"] = 4 }
            }
        };
        manager.ImportStatsData(CreateExportPayload(
            new DailyStats(Today)
            {
                KeyPresses = 2,
                KeyPressCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["W"] = 2 }
            },
            imported));

        Assert.IsTrue(manager.HistoryRevision > revisionBefore);
        Assert.IsNull(GetEntry(manager, "_keyHistoryCache", all), "import must drop the cached aggregate before it is queried again.");
        CollectionAssert.AreEqual(new[] { ("Q", 4), ("W", 2) }, manager.GetHistoricalKeyCounts(all).ToList());
        Assert.IsNotNull(GetEntry(manager, "_keyHistoryCache", all));

        Assert.IsTrue(File.Exists(Path.Combine(directory.Path, "history.json")));
        Assert.IsTrue(File.Exists(Path.Combine(directory.Path, "daily_stats.json")));
    }

    [TestMethod]
    public void ReturnedAggregates_AreDetachedFromTheCache()
    {
        var data = SyntheticHistory.Create(dayCount: 30, statisticsDayOffset: 0);
        var manager = data.CreateManager(out _);
        var dates = data.TrendDates(data.MinHistoryDate);

        manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All).Clear();
        manager.GetAppStatsSummary(StatsManager.AppStatsRange.All).Clear();
        manager.GetKeyboardHeatmapSummary(StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Sum).KeyCounts.Clear();
        var trend = manager.GetHistoryTrendSeries(StatsManager.HistoryRange.All, StatsManager.HistoryMetric.KeyPresses);
        trend.Display.Clear();
        trend.Local?.Clear();

        CollectionAssert.AreEqual(ReferenceKeyCounts(data, data.KeyHistoryDates(null)), manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All));
        Assert.AreEqual(data.ReferenceAppTotals(data.AppStatsDates(), null).Count, manager.GetAppStatsSummary(StatsManager.AppStatsRange.All).Count);
        AssertHeatmapSummary(manager, data, dates, StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Sum);
        Assert.AreEqual(dates.Count, manager.GetHistoryTrendSeries(StatsManager.HistoryRange.All, StatsManager.HistoryMetric.KeyPresses).Display.Count);

        // Mutating the objects handed out by the per-day heatmap view must not leak into history.
        var day = manager.GetKeyboardHeatmapDay(Today.AddDays(-5));
        day.KeyCounts["A"] = 99;
        day.Apps.Clear();
        Assert.IsFalse(manager.GetKeyboardHeatmapDay(Today.AddDays(-5)).KeyCounts.ContainsKey("A"));
    }

    [TestMethod]
    public void BatchedRemoteSnapshot_MatchesPerDayRemoteAggregation()
    {
        TestPlatform.RequireWindows();
        using var remoteDirectory = new TestDirectory();
        var cache = new RemoteShardCache(remoteDirectory.Path);
        var aggregator = new DisplayStatsAggregator(cache, () => "local-device");

        for (var offset = 1; offset <= 20; offset++)
        {
            Assert.IsTrue(cache.Apply(CreateRemoteRecord(
                "record-" + offset,
                "remote-device",
                Key(Today.AddDays(-offset)),
                offset)));
        }

        // This device's own records stay excluded, exactly like the per-day path.
        Assert.IsTrue(cache.Apply(CreateRemoteRecord("record-local", "local-device", Key(Today.AddDays(-5)), 99)));

        var data = SyntheticHistory.Create(dayCount: 25, statisticsDayOffset: 0);
        var manager = data.CreateManager(out _);
        manager.ConfigureSyncDisplay(aggregator, () => true);

        AssertHistoricalKeyCounts(manager, data, data.KeyHistoryDates(aggregator), StatsManager.KeyHistoryRange.All, aggregator);
        AssertAppSummary(manager, data, data.AppStatsDates(), StatsManager.AppStatsRange.All, aggregator);
        AssertHeatmapSummary(manager, data, data.TrendDates(data.HeatmapStart(aggregator)), StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Sum, aggregator);
        AssertTrendSeries(manager, data, data.TrendDates(data.MinHistoryDate), StatsManager.HistoryMetric.KeyPresses, aggregator);
        AssertInputTotals(manager, data, data.TrendDates(data.MouseTotalsStart(aggregator)), aggregator);

        // Same answers from the cached prefix after an invalidation.
        manager.NotifyRemoteStatsChanged();
        AssertHistoricalKeyCounts(manager, data, data.KeyHistoryDates(aggregator), StatsManager.KeyHistoryRange.All, aggregator);
        AssertHeatmapSummary(manager, data, data.TrendDates(data.HeatmapStart(aggregator)), StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Average, aggregator);

        // Turning the display off must drop remote contributions again.
        manager.ConfigureSyncDisplay(aggregator, () => false);
        AssertHistoricalKeyCounts(manager, data, data.KeyHistoryDates(null), StatsManager.KeyHistoryRange.All);
    }

    [TestMethod]
    public void LegacyHistoryFile_LoadsQueriesAndRoundTripsThroughBackup()
    {
        using var directory = new TestDirectory();
        var historyPath = Path.Combine(directory.Path, "history.json");
        var statsPath = Path.Combine(directory.Path, "daily_stats.json");

        // Written by an older build: per-app key breakdowns present, newer optional fields absent.
        const string legacyHistory = @"{
  ""2026-05-01"": {
    ""date"": ""2026-05-01T00:00:00"",
    ""keyPresses"": 30,
    ""keyPressCounts"": { ""A"": 20, ""Ctrl+C"": 10 },
    ""leftClicks"": 5,
    ""rightClicks"": 2,
    ""middleClicks"": 1,
    ""mouseDistance"": 1234.5,
    ""scrollDistance"": 67.25,
    ""peakKPS"": 3,
    ""peakCPS"": 2,
    ""appStats"": {
      ""editor"": {
        ""AppName"": ""editor"",
        ""DisplayName"": ""Editor"",
        ""KeyPresses"": 30,
        ""KeyPressCounts"": { ""A"": 20, ""Ctrl+C"": 10 },
        ""LeftClicks"": 5,
        ""RightClicks"": 2,
        ""MiddleClicks"": 1,
        ""ScrollDistance"": 67.25
      }
    }
  }
}";
        File.WriteAllText(historyPath, legacyHistory);
        File.WriteAllText(statsPath, @"{ ""date"": ""2026-05-01T00:00:00"", ""keyPresses"": 30, ""keyPressCounts"": { ""A"": 20 } }");

        var manager = (StatsManager)FormatterServices.GetUninitializedObject(typeof(StatsManager));
        SetField(manager, "_lock", new object());
        SetField(manager, "_midnightTimerLock", new object());
        SetField(manager, "_recentKeyTimestamps", new Queue<DateTime>());
        SetField(manager, "_recentClickTimestamps", new Queue<DateTime>());
        SetField(manager, "<Settings>k__BackingField", new AppSettings());
        SetField(manager, "_historyFilePath", historyPath);
        SetField(manager, "_statsFilePath", statsPath);
        SetField(manager, "_settingsFilePath", Path.Combine(directory.Path, "settings.json"));

        var history = (Dictionary<string, DailyStats>)InvokePrivate(manager, "LoadHistory")!;
        var current = (DailyStats?)InvokePrivate(manager, "LoadStats");
        SetField(manager, "<History>k__BackingField", history);
        SetField(manager, "<CurrentStats>k__BackingField", new DailyStats(Today)
        {
            KeyPresses = 4,
            KeyPressCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["B"] = 4 }
        });

        Assert.AreEqual(1, history.Count);
        Assert.AreEqual(30, history["2026-05-01"].KeyPresses);
        Assert.AreEqual(20, history["2026-05-01"].AppStats["editor"].KeyPressCounts["A"]);
        Assert.IsNotNull(current);

        Assert.AreEqual(4, manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.Week).Single(x => x.Key == "B").Count);
        Assert.AreEqual(
            20,
            manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All).Single(x => x.Key == "A").Count);

        InvokePrivate(manager, "SaveHistory");
        InvokePrivate(manager, "SaveStats");
        InvokePrivate(manager, "FlushSettings");

        var reloaded = JsonSerializer.Deserialize<Dictionary<string, DailyStats>>(File.ReadAllText(historyPath))!;
        Assert.AreEqual(30, reloaded["2026-05-01"].KeyPresses);
        Assert.AreEqual(10, reloaded["2026-05-01"].AppStats["editor"].KeyPressCounts["Ctrl+C"]);
        Assert.AreEqual(67.25, reloaded["2026-05-01"].ScrollDistance);
        Assert.AreEqual(5, reloaded["2026-05-01"].AppStats["editor"].LeftClicks);
        Assert.AreEqual(4, JsonSerializer.Deserialize<DailyStats>(File.ReadAllText(statsPath))!.KeyPresses);
        Assert.IsTrue(File.Exists(historyPath + ".bak"));

        // Corrupt the primary file: the .bak written by the atomic replace must still load.
        File.WriteAllText(historyPath, "{ truncated");
        var recovered = (Dictionary<string, DailyStats>)InvokePrivate(manager, "LoadHistory")!;
        Assert.AreEqual(1, recovered.Count);
        Assert.AreEqual(30, recovered["2026-05-01"].KeyPresses);
    }

    [TestMethod]
    public void TwoYearColdAndHotQueryTiming()
    {
        var data = SyntheticHistory.Create(dayCount: 730, statisticsDayOffset: 0);
        var manager = data.CreateManager(out _);

        var cold = Stopwatch.StartNew();
        manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All);
        var coldKeys = cold.ElapsedMilliseconds;

        cold.Restart();
        manager.GetKeyboardHeatmapSummary(StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Average);
        var coldHeatmap = cold.ElapsedMilliseconds;

        cold.Restart();
        manager.GetAppStatsSummary(StatsManager.AppStatsRange.All);
        var coldApps = cold.ElapsedMilliseconds;

        cold.Restart();
        manager.GetHistoryTrendSeries(StatsManager.HistoryRange.All, StatsManager.HistoryMetric.KeyPresses);
        var coldTrend = cold.ElapsedMilliseconds;

        cold.Restart();
        manager.GetAllTimePeakRates();
        var coldPeaks = cold.ElapsedMilliseconds;

        var hot = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All);
        }

        var hotKeys = hot.Elapsed.TotalMilliseconds / 50;

        hot.Restart();
        for (var i = 0; i < 50; i++)
        {
            manager.GetKeyboardHeatmapSummary(StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Average);
        }

        var hotHeatmap = hot.Elapsed.TotalMilliseconds / 50;

        hot.Restart();
        for (var i = 0; i < 50; i++)
        {
            manager.GetAppStatsSummary(StatsManager.AppStatsRange.All);
        }

        var hotApps = hot.Elapsed.TotalMilliseconds / 50;

        hot.Restart();
        for (var i = 0; i < 50; i++)
        {
            data.Current.KeyPresses++;
            data.Current.KeyPressCounts["Z"] = 1;
            manager.GetHistoryTrendSeries(StatsManager.HistoryRange.All, StatsManager.HistoryMetric.KeyPresses);
        }

        var hotTrendWithInput = hot.Elapsed.TotalMilliseconds / 50;

        hot.Restart();
        for (var i = 0; i < 50; i++)
        {
            manager.GetAllTimePeakRates();
        }

        var hotPeaks = hot.Elapsed.TotalMilliseconds / 50;

        // Same queries with the cache forced empty every time, i.e. the pre-optimization cost
        // where every call rescanned the whole history.
        var rescan = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            manager.NotifyRemoteStatsChanged();
            manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All);
        }

        var rescanKeys = rescan.Elapsed.TotalMilliseconds / 20;
        rescan.Restart();
        for (var i = 0; i < 20; i++)
        {
            manager.NotifyRemoteStatsChanged();
            manager.GetKeyboardHeatmapSummary(StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Average);
        }

        var rescanHeatmap = rescan.Elapsed.TotalMilliseconds / 20;
        rescan.Restart();
        for (var i = 0; i < 20; i++)
        {
            manager.NotifyRemoteStatsChanged();
            manager.GetAppStatsSummary(StatsManager.AppStatsRange.All);
        }

        var rescanApps = rescan.Elapsed.TotalMilliseconds / 20;
        rescan.Restart();
        for (var i = 0; i < 20; i++)
        {
            manager.NotifyRemoteStatsChanged();
            manager.GetHistoryTrendSeries(StatsManager.HistoryRange.All, StatsManager.HistoryMetric.KeyPresses);
        }

        var rescanTrend = rescan.Elapsed.TotalMilliseconds / 20;

        TestContext.WriteLine(
            $"730 days — cold: keys {coldKeys}ms, heatmap {coldHeatmap}ms, apps {coldApps}ms, trend {coldTrend}ms, peaks {coldPeaks}ms");
        TestContext.WriteLine(
            $"730 days — hot avg: keys {hotKeys:F3}ms, heatmap {hotHeatmap:F3}ms, apps {hotApps:F3}ms, trend+input {hotTrendWithInput:F3}ms, peaks {hotPeaks:F3}ms");
        TestContext.WriteLine(
            $"730 days — forced rescan avg: keys {rescanKeys:F3}ms, heatmap {rescanHeatmap:F3}ms, apps {rescanApps:F3}ms, trend {rescanTrend:F3}ms");
    }

    [TestMethod]
    public void RemoteDisplayAggregation_CapturesOneSnapshotPerQuery()
    {
        TestPlatform.RequireWindows();
        using var remoteDirectory = new TestDirectory();
        var cache = new RemoteShardCache(remoteDirectory.Path);
        var aggregator = new DisplayStatsAggregator(cache, () => "local-device");
        for (var offset = 1; offset <= 365; offset++)
        {
            Assert.IsTrue(cache.Apply(CreateRemoteRecord("remote-" + offset, "remote-device", Key(Today.AddDays(-offset)), offset)));
        }

        var data = SyntheticHistory.Create(dayCount: 730, statisticsDayOffset: 0);
        var manager = data.CreateManager(out _);
        manager.ConfigureSyncDisplay(aggregator, () => true);
        var dates = data.KeyHistoryDates(aggregator);
        Assert.AreEqual(730, dates.Count);

        // Control measurement only: the two-argument Aggregate captures a full remote snapshot per
        // day, which is heavier than the former per-day filter. It shows the cost of repeating the
        // snapshot per day — it is NOT a pre-optimization baseline and no threshold is asserted.
        var perDaySnapshot = Stopwatch.StartNew();
        for (var round = 0; round < 5; round++)
        {
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var date in dates)
            {
                var daily = aggregator.Aggregate(date, data.ResolveLocal(date));
                foreach (var pair in daily.KeyPressCounts)
                {
                    if (pair.Value <= 0) continue;
                    totals[pair.Key] = (totals.TryGetValue(pair.Key, out var current) ? current : 0) + pair.Value;
                }
            }
        }

        var perDaySnapshotPerQuery = perDaySnapshot.Elapsed.TotalMilliseconds / 5;

        var coldWatch = Stopwatch.StartNew();
        var cached = manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All);
        var coldPerQuery = coldWatch.Elapsed.TotalMilliseconds;

        var remoteSnapshot = GetEntryObject(manager, "_remoteSnapshot");
        var cachedEntry = GetEntry(manager, "_keyHistoryCache", StatsManager.KeyHistoryRange.All);
        Assert.IsNotNull(remoteSnapshot, "The remote contribution must be memoised per revision.");
        Assert.IsNotNull(cachedEntry);

        var hotWatch = Stopwatch.StartNew();
        for (var round = 0; round < 20; round++)
        {
            cached = manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All);
        }

        var hotPerQuery = hotWatch.Elapsed.TotalMilliseconds / 20;

        Assert.AreSame(remoteSnapshot, GetEntryObject(manager, "_remoteSnapshot"),
            "Hot queries must reuse the remote snapshot instead of cloning the whole cache again.");
        Assert.AreSame(cachedEntry, GetEntry(manager, "_keyHistoryCache", StatsManager.KeyHistoryRange.All),
            "Hot queries must reuse the stable prefix instead of re-aggregating the history.");

        // Top-level refresh cost: cached aggregates for every view, today changing in between.
        var refreshWatch = Stopwatch.StartNew();
        for (var round = 0; round < 20; round++)
        {
            data.Current.KeyPresses++;
            manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All);
            manager.GetKeyboardHeatmapSummary(StatsManager.KeyboardHeatmapRange.All, StatsManager.KeyboardHeatmapMode.Average);
            manager.GetAppStatsSummary(StatsManager.AppStatsRange.All);
            manager.GetHistoryTrendSeries(StatsManager.HistoryRange.All, StatsManager.HistoryMetric.KeyPresses);
            manager.GetHistoryInputTotals(StatsManager.MouseTotalsRange.All);
            manager.GetAllTimePeakRates();
        }

        var refreshAll = refreshWatch.Elapsed.TotalMilliseconds / 20;
        Assert.AreSame(remoteSnapshot, GetEntryObject(manager, "_remoteSnapshot"));

        // A remote change advances the revision and therefore re-captures the snapshot.
        manager.NotifyRemoteStatsChanged();
        Assert.IsNull(GetEntryObject(manager, "_remoteSnapshot"));
        manager.GetHistoricalKeyCounts(StatsManager.KeyHistoryRange.All);
        Assert.IsNotNull(GetEntryObject(manager, "_remoteSnapshot"));
        Assert.AreNotSame(remoteSnapshot, GetEntryObject(manager, "_remoteSnapshot"));

        // The batched snapshot must produce the same totals as the per-day deep clones.
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var date in dates)
        {
            var daily = aggregator.Aggregate(date, data.ResolveLocal(date));
            foreach (var pair in daily.KeyPressCounts)
            {
                if (pair.Value <= 0) continue;
                expected[pair.Key] = (expected.TryGetValue(pair.Key, out var current) ? current : 0) + pair.Value;
            }
        }

        foreach (var pair in cached)
        {
            Assert.AreEqual(expected[pair.Key], pair.Count, "remote total for " + pair.Key);
        }

        TestContext.WriteLine(
            $"730 days with 365 remote records — per-query key history: repeated per-day snapshot control {perDaySnapshotPerQuery:F1}ms, " +
            $"batched cold {coldPerQuery:F1}ms, batched hot {hotPerQuery:F3}ms; full UI refresh (6 queries) {refreshAll:F2}ms");
    }

    #region Assertions

    private static void AssertHistoricalKeyCounts(
        StatsManager manager,
        SyntheticHistory data,
        IReadOnlyList<DateTime> dates,
        StatsManager.KeyHistoryRange range,
        DisplayStatsAggregator? aggregator = null)
    {
        var expected = ReferenceKeyCounts(data, dates, aggregator);
        CollectionAssert.AreEqual(expected, manager.GetHistoricalKeyCounts(range), "Key history for " + range);
        CollectionAssert.AreEqual(expected.Take(3).ToList(), manager.GetHistoricalKeyCounts(range, 3), "Limited key history for " + range);
    }

    private static void AssertAppSummary(
        StatsManager manager,
        SyntheticHistory data,
        IReadOnlyList<DateTime> dates,
        StatsManager.AppStatsRange range,
        DisplayStatsAggregator? aggregator = null)
    {
        var expected = data.ReferenceAppTotals(dates, aggregator).Values.OrderBy(app => app.AppName, StringComparer.Ordinal).ToList();
        var actual = manager.GetAppStatsSummary(range).OrderBy(app => app.AppName, StringComparer.Ordinal).ToList();

        Assert.AreEqual(expected.Count, actual.Count, "App summary size for " + range);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index].AppName, actual[index].AppName);
            Assert.AreEqual(expected[index].DisplayName, actual[index].DisplayName, actual[index].AppName + " display name");
            Assert.AreEqual(expected[index].KeyPresses, actual[index].KeyPresses, actual[index].AppName + " keys");
            Assert.AreEqual(expected[index].LeftClicks, actual[index].LeftClicks, actual[index].AppName + " left");
            Assert.AreEqual(expected[index].RightClicks, actual[index].RightClicks, actual[index].AppName + " right");
            Assert.AreEqual(expected[index].MiddleClicks, actual[index].MiddleClicks, actual[index].AppName + " middle");
            Assert.AreEqual(expected[index].SideBackClicks, actual[index].SideBackClicks, actual[index].AppName + " back");
            Assert.AreEqual(expected[index].SideForwardClicks, actual[index].SideForwardClicks, actual[index].AppName + " forward");
            Assert.AreEqual(expected[index].ScrollDistance, actual[index].ScrollDistance, actual[index].AppName + " scroll");
        }
    }

    private static void AssertHeatmapSummary(
        StatsManager manager,
        SyntheticHistory data,
        IReadOnlyList<DateTime> dates,
        StatsManager.KeyboardHeatmapRange range,
        StatsManager.KeyboardHeatmapMode mode,
        DisplayStatsAggregator? aggregator = null)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        var totalKeyPresses = 0.0;
        foreach (var date in dates)
        {
            var daily = data.Resolve(date, aggregator);
            totalKeyPresses += Math.Max(0, daily.KeyPresses);
            foreach (var pair in AggregateKeyboardHeatmapCounts(daily.KeyPressCounts))
            {
                totals[pair.Key] = (totals.TryGetValue(pair.Key, out var current) ? current : 0) + pair.Value;
            }
        }

        if (mode == StatsManager.KeyboardHeatmapMode.Average && dates.Count > 0)
        {
            totalKeyPresses /= dates.Count;
            foreach (var key in totals.Keys.ToList())
            {
                totals[key] /= dates.Count;
            }
        }

        var actual = manager.GetKeyboardHeatmapSummary(range, mode);
        Assert.AreEqual(totalKeyPresses, actual.TotalKeyPresses, 1e-9, "Heatmap total " + range + "/" + mode);
        Assert.AreEqual(totals.Count, actual.KeyCounts.Count, "Heatmap key count " + range + "/" + mode);
        foreach (var pair in totals)
        {
            Assert.AreEqual(pair.Value, actual.KeyCounts[pair.Key], 1e-9, "Heatmap key " + pair.Key);
        }
    }

    private static void AssertTrendSeries(
        StatsManager manager,
        SyntheticHistory data,
        IReadOnlyList<DateTime> dates,
        StatsManager.HistoryMetric metric,
        DisplayStatsAggregator? aggregator = null)
    {
        var expectedLocal = dates.Select(date => (date, data.MetricValue(metric, data.ResolveLocal(date)))).ToList();
        var expectedDisplay = dates.Select(date => (date, data.MetricValue(metric, data.Resolve(date, aggregator)))).ToList();

        var actual = manager.GetHistoryTrendSeries(StatsManager.HistoryRange.All, metric);
        CollectionAssert.AreEqual(expectedLocal, actual.Local, "Local trend " + metric);
        CollectionAssert.AreEqual(expectedDisplay, actual.Display, "Display trend " + metric);

        var expectedSeries = aggregator == null ||
                             metric is StatsManager.HistoryMetric.MouseDistance or StatsManager.HistoryMetric.ScrollDistance
            ? expectedLocal
            : expectedDisplay;
        CollectionAssert.AreEqual(expectedSeries, manager.GetHistorySeries(StatsManager.HistoryRange.All, metric));
    }

    private static void AssertInputTotals(
        StatsManager manager,
        SyntheticHistory data,
        IReadOnlyList<DateTime> dates,
        DisplayStatsAggregator? aggregator = null)
    {
        int left = 0, middle = 0, right = 0, back = 0, forward = 0;
        double mouse = 0, scroll = 0;
        foreach (var date in dates)
        {
            var stats = data.Resolve(date, aggregator);
            left = SaturatingAdd(left, stats.LeftClicks);
            middle = SaturatingAdd(middle, stats.MiddleClicks);
            right = SaturatingAdd(right, stats.RightClicks);
            back = SaturatingAdd(back, stats.SideBackClicks);
            forward = SaturatingAdd(forward, stats.SideForwardClicks);
            mouse += Math.Max(0, stats.MouseDistance);
            scroll += Math.Max(0, stats.ScrollDistance);
        }

        var actual = manager.GetHistoryInputTotals(StatsManager.MouseTotalsRange.All);
        Assert.AreEqual(left, actual.LeftClicks);
        Assert.AreEqual(middle, actual.MiddleClicks);
        Assert.AreEqual(right, actual.RightClicks);
        Assert.AreEqual(back, actual.SideBackClicks);
        Assert.AreEqual(forward, actual.SideForwardClicks);
        Assert.AreEqual(mouse, actual.MouseDistance, 1e-9);
        Assert.AreEqual(scroll, actual.ScrollDistance, 1e-9);
    }

    private static (double PeakKPS, double PeakCPS) ReferencePeaks(SyntheticHistory data, IReadOnlyList<DateTime> dates)
    {
        // Unchanged original semantics: today's live peaks plus every archived day up to today.
        var peakKPS = data.Current.PeakKPS;
        var peakCPS = data.Current.PeakCPS;
        foreach (var stats in data.History.Values)
        {
            if (stats.Date.Date > Today) continue;
            peakKPS = Math.Max(peakKPS, stats.PeakKPS);
            peakCPS = Math.Max(peakCPS, stats.PeakCPS);
        }

        return (peakKPS, peakCPS);
    }

    private static List<(string Key, int Count)> ReferenceKeyCounts(
        SyntheticHistory data,
        IReadOnlyList<DateTime> dates,
        DisplayStatsAggregator? aggregator = null,
        DateTime? statisticsDay = null)
    {
        var totals = ReferenceKeyTotals(data, dates, aggregator, statisticsDay);
        return totals
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Key, x.Value))
            .ToList();
    }

    private static Dictionary<string, int> ReferenceKeyTotals(
        SyntheticHistory data,
        IReadOnlyList<DateTime> dates,
        DisplayStatsAggregator? aggregator = null,
        DateTime? statisticsDay = null)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var date in dates)
        {
            var stats = aggregator != null ? data.Resolve(date, aggregator) : data.ResolveLocal(date, statisticsDay);
            foreach (var pair in stats.KeyPressCounts)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key) || pair.Value <= 0) continue;
                totals[key] = SaturatingAdd(totals.TryGetValue(key, out var existing) ? existing : 0, pair.Value);
            }
        }

        return totals;
    }

    private static Dictionary<string, int> AggregateKeyboardHeatmapCounts(Dictionary<string, int> keyPressCounts)
    {
        var method = typeof(StatsManager).GetMethod("AggregateKeyboardHeatmapCounts", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (Dictionary<string, int>)method!.Invoke(null, new object[] { keyPressCounts })!;
    }

    private static int SaturatingAdd(int left, int right)
    {
        if (right <= 0) return left;
        return left > int.MaxValue - right ? int.MaxValue : left + right;
    }

    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    #endregion

    #region Cache inspection helpers

    private static object? GetEntry(StatsManager manager, string fieldName, object key)
    {
        var cache = GetEntryObject(manager, fieldName);
        if (cache == null)
        {
            return null;
        }

        var dictionary = (IDictionary)cache;
        return dictionary.Contains(key) ? dictionary[key] : null;
    }

    private static object? GetEntryObject(StatsManager manager, string fieldName) =>
        typeof(StatsManager).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager);

    private static object GetEntryValue(object entry) =>
        entry.GetType().GetProperty("Value")!.GetValue(entry)!;

    private static void AssertInvalidated(
        StatsManager manager,
        object? entryBefore,
        long revisionBefore,
        Action requery)
    {
        Assert.IsTrue(
            manager.HistoryRevision > revisionBefore,
            "revision must advance when the immutable history view changes");
        Assert.IsNull(GetEntryObject(manager, "_keyHistoryCache"), "cached aggregates must be dropped");

        requery();

        var entryAfter = GetEntry(manager, "_keyHistoryCache", StatsManager.KeyHistoryRange.All);
        Assert.IsNotNull(entryAfter, "the aggregate must be rebuilt on the next query");
        if (entryBefore != null)
        {
            Assert.AreNotSame(entryBefore, entryAfter, "the rebuilt entry must be a new instance");
        }
    }

    #endregion

    #region Fixture plumbing

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "Missing test setup field: " + name);
        field!.SetValue(target, value);
    }

    private static object? InvokePrivate(object target, string name)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Missing test setup method: " + name);
        return method!.Invoke(target, null);
    }

    private static byte[] CreateExportPayload(DailyStats current, Dictionary<string, DailyStats> history)
    {
        var payload = new
        {
            Version = 1,
            Scope = "currentDevice",
            ExportedAt = DateTime.UtcNow,
            CurrentStats = current,
            History = history
        };

        return JsonSerializer.SerializeToUtf8Bytes(
            payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static CachedRemoteRecord CreateRemoteRecord(string recordId, string deviceId, string localDay, long value) =>
        new()
        {
            RecordId = recordId,
            DeviceId = deviceId,
            Revision = 1,
            CiphertextHash = "hash-" + recordId,
            IsCurrent = true,
            Plaintext = new CoreDaySnapshotV1
            {
                SchemaVersion = 1,
                DeviceId = deviceId,
                LocalDay = localDay,
                Revision = 1,
                KeyPresses = value,
                KeyPressCounts = new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["A"] = value,
                    ["Ctrl+S"] = value + 1
                },
                Clicks = new CoreClickSnapshotV1
                {
                    Left = value,
                    Right = value + 1,
                    Middle = value + 2,
                    SideBack = value + 3,
                    SideForward = value + 4
                }
            }
        };

    /// <summary>
    /// Deterministic multi-year fixture plus the unchanged per-day reference math (including the
    /// original range semantics) the aggregate cache is expected to reproduce exactly.
    /// </summary>
    private sealed class SyntheticHistory
    {
        public DailyStats Current { get; set; } = new();
        public Dictionary<string, DailyStats> History { get; set; } = new(StringComparer.Ordinal);
        public int DayCount { get; set; }
        public DateTime StatisticsDay { get; set; }

        public static SyntheticHistory Create(int dayCount, int statisticsDayOffset)
        {
            var statisticsDay = Today.AddDays(statisticsDayOffset);
            var history = new Dictionary<string, DailyStats>(StringComparer.Ordinal);
            for (var offset = dayCount - 1; offset >= 0; offset--)
            {
                var date = Today.AddDays(-offset);
                if (date >= statisticsDay)
                {
                    continue;
                }

                history[Key(date)] = CreateDay(date, offset);
            }

            return new SyntheticHistory
            {
                DayCount = dayCount,
                StatisticsDay = statisticsDay,
                History = history,
                Current = CreateDay(statisticsDay, 0)
            };
        }

        private static DailyStats CreateDay(DateTime date, int offset)
        {
            var key = "K" + (date.DayOfYear % 6);
            var secondKey = "Ctrl+" + (char)('A' + date.DayOfWeek);
            var stats = new DailyStats(date)
            {
                KeyPresses = 10 + offset % 17,
                LeftClicks = 3 + offset % 7,
                RightClicks = 1 + offset % 5,
                MiddleClicks = offset % 3,
                SideBackClicks = offset % 2,
                SideForwardClicks = (offset + 1) % 2,
                MouseDistance = 100.5 + offset,
                ScrollDistance = 12.25 + offset,
                PeakKPS = 1 + offset % 11,
                PeakCPS = 0.5 + offset % 7
            };
            stats.KeyPressCounts[key] = 4 + offset % 9;
            stats.KeyPressCounts[secondKey] = 2 + offset % 3;
            stats.KeyPressCounts["  "] = -5;
            stats.KeyPressCounts["Zero"] = 0;

            for (var app = 0; app < 3; app++)
            {
                var name = "app" + ((date.DayOfYear + app) % 3);
                var appStats = new AppStats(name, "Display " + name)
                {
                    KeyPresses = 5 + offset % 13 + app,
                    LeftClicks = 2 + app,
                    RightClicks = 1 + app,
                    MiddleClicks = app,
                    SideBackClicks = app % 2,
                    SideForwardClicks = (app + 1) % 2,
                    ScrollDistance = 5.5 + app
                };
                appStats.KeyPressCounts[key] = 3 + app;
                stats.AppStats[name] = appStats;
            }

            return stats;
        }

        public StatsManager CreateManager(out TestDirectory directory)
        {
            var temp = new TestDirectory();
            directory = temp;
            var manager = (StatsManager)FormatterServices.GetUninitializedObject(typeof(StatsManager));
            SetField(manager, "_lock", new object());
            SetField(manager, "_midnightTimerLock", new object());
            SetField(manager, "_recentKeyTimestamps", new Queue<DateTime>());
            SetField(manager, "_recentClickTimestamps", new Queue<DateTime>());
            SetField(manager, "<Settings>k__BackingField", new AppSettings());
            SetField(manager, "<CurrentStats>k__BackingField", Current);
            SetField(manager, "<History>k__BackingField", History);
            SetField(manager, "_statsFilePath", Path.Combine(temp.Path, "daily_stats.json"));
            SetField(manager, "_historyFilePath", Path.Combine(temp.Path, "history.json"));
            SetField(manager, "_settingsFilePath", Path.Combine(temp.Path, "settings.json"));
            SetField(manager, "_saveInterval", 20.0);
            return manager;
        }

        #region Reference range math (unchanged semantics)

        public DateTime MinHistoryDate =>
            History.Values.Select(stats => stats.Date.Date).Where(date => date <= Today).DefaultIfEmpty(Today).Min();

        public IReadOnlyList<DateTime> TrendDates(DateTime start)
        {
            var dates = new List<DateTime>();
            for (var date = start; date <= Today; date = date.AddDays(1))
            {
                dates.Add(date);
            }

            return dates;
        }

        public IReadOnlyList<DateTime> KeyHistoryDates(DisplayStatsAggregator? aggregator)
        {
            var dates = History.Values.Select(stats => stats.Date.Date).Where(date => date <= Today).ToHashSet();
            dates.Add(Today);
            if (aggregator != null)
            {
                dates.UnionWith(aggregator.GetRemoteDays().Where(date => date <= Today));
            }

            return dates.OrderBy(date => date).ToList();
        }

        /// <summary>App stats range "All" only walks the History keys — no remote days.</summary>
        public IReadOnlyList<DateTime> AppStatsDates()
        {
            var dates = History.Keys
                .Where(key => DateTime.TryParse(key, out _))
                .Select(key => DateTime.Parse(key).Date)
                .ToList();
            if (!dates.Contains(Today))
            {
                dates.Add(Today);
            }

            return dates.Distinct().OrderBy(date => date).ToList();
        }

        public DateTime HeatmapStart(DisplayStatsAggregator? aggregator)
        {
            var start = StatisticsDay <= Today ? StatisticsDay : Today;
            foreach (var candidate in History.Values.Select(stats => stats.Date.Date))
            {
                if (candidate <= Today && candidate < start)
                {
                    start = candidate;
                }
            }

            if (aggregator != null)
            {
                foreach (var candidate in aggregator.GetRemoteDays())
                {
                    if (candidate <= Today && candidate < start)
                    {
                        start = candidate;
                    }
                }
            }

            return start;
        }

        public DateTime MouseTotalsStart(DisplayStatsAggregator? aggregator)
        {
            var candidates = History.Values
                .Select(stats => stats.Date.Date)
                .Append(StatisticsDay)
                .Concat(aggregator?.GetRemoteDays() ?? Array.Empty<DateTime>())
                .Where(date => date <= Today)
                .DefaultIfEmpty(Today);
            return candidates.Min();
        }

        public IReadOnlyList<DateTime> LastNDays(IReadOnlyList<DateTime> dates, int count) =>
            dates.OrderByDescending(date => date).Take(count).OrderBy(date => date).ToList();

        #endregion

        public DailyStats ResolveLocal(DateTime date, DateTime? statisticsDayOverride = null)
        {
            var statisticsDay = statisticsDayOverride ?? StatisticsDay;
            if (date.Date == statisticsDay)
            {
                return Current;
            }

            return History.TryGetValue(Key(date), out var stats) ? stats : new DailyStats(date);
        }

        public DailyStats Resolve(DateTime date, DisplayStatsAggregator? aggregator)
        {
            var local = ResolveLocal(date);
            return aggregator != null ? aggregator.Aggregate(date.Date, local) : local;
        }

        public double MetricValue(StatsManager.HistoryMetric metric, DailyStats stats) => metric switch
        {
            StatsManager.HistoryMetric.KeyPresses => stats.KeyPresses,
            StatsManager.HistoryMetric.Clicks => stats.TotalClicks,
            StatsManager.HistoryMetric.MouseDistance => stats.MouseDistance,
            StatsManager.HistoryMetric.ScrollDistance => stats.ScrollDistance,
            _ => 0
        };

        public Dictionary<string, AppStats> ReferenceAppTotals(IReadOnlyList<DateTime> dates, DisplayStatsAggregator? aggregator)
        {
            var totals = new Dictionary<string, AppStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var date in dates)
            {
                var daily = Resolve(date, aggregator);
                foreach (var pair in daily.AppStats)
                {
                    if (!totals.TryGetValue(pair.Key, out var total))
                    {
                        total = new AppStats(pair.Key, pair.Value.DisplayName);
                        totals[pair.Key] = total;
                    }

                    if (!string.IsNullOrEmpty(pair.Value.DisplayName))
                    {
                        total.DisplayName = pair.Value.DisplayName;
                    }

                    total.KeyPresses += pair.Value.KeyPresses;
                    total.LeftClicks += pair.Value.LeftClicks;
                    total.RightClicks += pair.Value.RightClicks;
                    total.MiddleClicks += pair.Value.MiddleClicks;
                    total.SideBackClicks += pair.Value.SideBackClicks;
                    total.SideForwardClicks += pair.Value.SideForwardClicks;
                    total.ScrollDistance += pair.Value.ScrollDistance;
                }
            }

            return totals;
        }
    }

    #endregion
}
