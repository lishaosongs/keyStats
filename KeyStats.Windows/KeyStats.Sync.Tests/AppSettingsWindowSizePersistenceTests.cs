using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using KeyStats.Models;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

[TestClass]
public sealed class AppSettingsWindowSizePersistenceTests
{
    [TestMethod]
    public void LegacySettingsWithoutWindowSizeFields_LoadWithNullSizes()
    {
        const string legacyJson = @"{
            ""notificationsEnabled"": true,
            ""keyHistorySelectedRangeIndex"": 0,
            ""keyboardHeatmapWindowWidth"": 900,
            ""mainWindowWidth"": 800
        }";

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.IsNotNull(settings);
        Assert.IsTrue(settings!.NotificationsEnabled);
        Assert.AreEqual(0, settings.KeyHistorySelectedRangeIndex);
        Assert.AreEqual(900d, settings.KeyboardHeatmapWindowWidth);
        Assert.AreEqual(800d, settings.MainWindowWidth);
        Assert.IsNull(settings.KeyHistoryWindowWidth);
        Assert.IsNull(settings.KeyHistoryWindowHeight);
        Assert.IsNull(settings.AppStatsWindowWidth);
        Assert.IsNull(settings.AppStatsWindowHeight);
    }

    [TestMethod]
    public void WindowSizeFields_PersistThroughSettingsFileRoundTrip()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var manager = CreateSettingsManager(path);
        SetField(manager, "<Settings>k__BackingField", new AppSettings
        {
            KeyHistoryWindowWidth = 860.5,
            KeyHistoryWindowHeight = 620.25,
            AppStatsWindowWidth = 720,
            AppStatsWindowHeight = 640
        });

        InvokePrivate(manager, "FlushSettings");

        var json = File.ReadAllText(path);
        StringAssert.Contains(json, "\"keyHistoryWindowWidth\": 860.5");
        StringAssert.Contains(json, "\"keyHistoryWindowHeight\": 620.25");
        StringAssert.Contains(json, "\"appStatsWindowWidth\": 720");
        StringAssert.Contains(json, "\"appStatsWindowHeight\": 640");

        var reloaded = (AppSettings)InvokePrivate(manager, "LoadSettings")!;
        Assert.AreEqual(860.5, reloaded.KeyHistoryWindowWidth);
        Assert.AreEqual(620.25, reloaded.KeyHistoryWindowHeight);
        Assert.AreEqual(720d, reloaded.AppStatsWindowWidth);
        Assert.AreEqual(640d, reloaded.AppStatsWindowHeight);
    }

    private static StatsManager CreateSettingsManager(string settingsPath)
    {
        var manager = (StatsManager)FormatterServices.GetUninitializedObject(typeof(StatsManager));
        SetField(manager, "_lock", new object());
        SetField(manager, "_settingsFilePath", settingsPath);
        return manager;
    }

    private static object? InvokePrivate(object target, string name)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Missing test setup method: " + name);
        return method!.Invoke(target, null);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "Missing test setup field: " + name);
        field!.SetValue(target, value);
    }
}
