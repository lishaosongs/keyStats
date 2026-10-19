using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KeyStats.Models;

public class AppStats
{
    private Dictionary<string, int> _keyPressCounts = new(StringComparer.Ordinal);

    public string AppName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int KeyPresses { get; set; }
    public Dictionary<string, int> KeyPressCounts
    {
        get => _keyPressCounts;
        set => _keyPressCounts = value ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }
    public int LeftClicks { get; set; }
    public int RightClicks { get; set; }
    public int MiddleClicks { get; set; }
    public int SideBackClicks { get; set; }
    public int SideForwardClicks { get; set; }
    public double ScrollDistance { get; set; }

    [JsonIgnore]
    public int TotalClicks => LeftClicks + RightClicks + MiddleClicks + SideBackClicks + SideForwardClicks;

    [JsonIgnore]
    public bool HasActivity =>
        KeyPresses > 0 ||
        LeftClicks > 0 ||
        RightClicks > 0 ||
        MiddleClicks > 0 ||
        SideBackClicks > 0 ||
        SideForwardClicks > 0 ||
        ScrollDistance > 0;

    public AppStats() { }

    public AppStats(string appName, string displayName = "")
    {
        AppName = appName;
        DisplayName = string.IsNullOrEmpty(displayName) ? appName : displayName;
    }

    public AppStats(AppStats source)
    {
        AppName = source.AppName;
        DisplayName = source.DisplayName;
        KeyPresses = source.KeyPresses;
        KeyPressCounts = new Dictionary<string, int>(source.KeyPressCounts, StringComparer.Ordinal);
        LeftClicks = source.LeftClicks;
        RightClicks = source.RightClicks;
        MiddleClicks = source.MiddleClicks;
        SideBackClicks = source.SideBackClicks;
        SideForwardClicks = source.SideForwardClicks;
        ScrollDistance = source.ScrollDistance;
    }

    public void RecordKeyPress(string? keyName = null)
    {
        KeyPresses++;
        if (keyName == null || string.IsNullOrWhiteSpace(keyName)) return;

        var key = keyName.Trim();
        KeyPressCounts[key] = KeyPressCounts.TryGetValue(key, out var count) ? count + 1 : 1;
    }
    public void RecordLeftClick() => LeftClicks++;
    public void RecordRightClick() => RightClicks++;
    public void RecordMiddleClick() => MiddleClicks++;
    public void RecordSideBackClick() => SideBackClicks++;
    public void RecordSideForwardClick() => SideForwardClicks++;
    public void AddScrollDistance(double distance) => ScrollDistance += distance;
}
