using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KeyStats.Models;

namespace KeyStats.Services;

/// <summary>
/// One pass over the remote cache for a display query: per-day contributions from other
/// devices plus the set of days the remote cache knows about. A multi-day query captures it
/// once and reuses it, instead of deep-cloning every record for every day.
/// </summary>
public sealed class DisplayRemoteSnapshot
{
    internal static readonly DisplayRemoteSnapshot Empty =
        new(new Dictionary<string, DailyStats>(StringComparer.Ordinal), new List<DateTime>());

    private readonly Dictionary<string, DailyStats> _contributionsByDay;
    private readonly List<DateTime> _days;

    internal DisplayRemoteSnapshot(Dictionary<string, DailyStats> contributionsByDay, List<DateTime> days)
    {
        _contributionsByDay = contributionsByDay;
        _days = days;
    }

    public IReadOnlyList<DateTime> Days => _days;

    public DailyStats? GetContribution(DateTime date)
    {
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return _contributionsByDay.TryGetValue(day, out var contribution) ? contribution : null;
    }
}

public sealed class DisplayStatsAggregator
{
    private const string DayFormat = "yyyy-MM-dd";

    private readonly RemoteShardCache _remoteCache;
    private readonly Func<string> _localDeviceId;

    public DisplayStatsAggregator(RemoteShardCache remoteCache, Func<string> localDeviceId)
    {
        _remoteCache = remoteCache;
        _localDeviceId = localDeviceId;
    }

    public DailyStats Aggregate(DateTime date, DailyStats localStats)
    {
        return Aggregate(date, localStats, CaptureSnapshot());
    }

    public DailyStats Aggregate(DateTime date, DailyStats localStats, DisplayRemoteSnapshot snapshot)
    {
        var result = Clone(localStats, date.Date);
        var remote = snapshot.GetContribution(date);
        if (remote == null)
        {
            return result;
        }

        result.KeyPresses = SafeAdd(result.KeyPresses, remote.KeyPresses);
        result.LeftClicks = SafeAdd(result.LeftClicks, remote.LeftClicks);
        result.RightClicks = SafeAdd(result.RightClicks, remote.RightClicks);
        result.MiddleClicks = SafeAdd(result.MiddleClicks, remote.MiddleClicks);
        result.SideBackClicks = SafeAdd(result.SideBackClicks, remote.SideBackClicks);
        result.SideForwardClicks = SafeAdd(result.SideForwardClicks, remote.SideForwardClicks);

        foreach (var keyCount in remote.KeyPressCounts)
        {
            var key = (keyCount.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key) || keyCount.Value <= 0) continue;
            result.KeyPressCounts[key] = SafeAdd(
                result.KeyPressCounts.TryGetValue(key, out var existing) ? existing : 0,
                keyCount.Value);
        }

        return result;
    }

    /// <summary>
    /// Reads the remote cache once and folds every non-local record into per-day contributions,
    /// keeping the same device filter and clamping the previous per-day <see cref="Aggregate"/> used.
    /// </summary>
    public DisplayRemoteSnapshot CaptureSnapshot()
    {
        var localDeviceId = _localDeviceId();
        var contributionsByDay = new Dictionary<string, DailyStats>(StringComparer.Ordinal);
        var days = new HashSet<DateTime>();

        foreach (var record in _remoteCache.GetAll())
        {
            var remote = record.Plaintext;
            var day = remote.LocalDay ?? string.Empty;
            if (!DateTime.TryParseExact(
                    day,
                    DayFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                continue;
            }

            days.Add(parsed.Date);

            if (string.Equals(record.DeviceId, localDeviceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!contributionsByDay.TryGetValue(day, out var contribution))
            {
                contribution = new DailyStats(parsed.Date);
                contributionsByDay[day] = contribution;
            }

            contribution.KeyPresses = SafeAdd(contribution.KeyPresses, remote.KeyPresses);
            contribution.LeftClicks = SafeAdd(contribution.LeftClicks, remote.Clicks.Left);
            contribution.RightClicks = SafeAdd(contribution.RightClicks, remote.Clicks.Right);
            contribution.MiddleClicks = SafeAdd(contribution.MiddleClicks, remote.Clicks.Middle);
            contribution.SideBackClicks = SafeAdd(contribution.SideBackClicks, remote.Clicks.SideBack);
            contribution.SideForwardClicks = SafeAdd(contribution.SideForwardClicks, remote.Clicks.SideForward);

            foreach (var keyCount in remote.KeyPressCounts)
            {
                var key = (keyCount.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key) || keyCount.Value <= 0) continue;
                contribution.KeyPressCounts[key] = SafeAdd(
                    contribution.KeyPressCounts.TryGetValue(key, out var existing) ? existing : 0,
                    keyCount.Value);
            }
        }

        return new DisplayRemoteSnapshot(contributionsByDay, days.OrderBy(day => day).ToList());
    }

    public IReadOnlyCollection<DateTime> GetRemoteDays()
    {
        return _remoteCache.GetAvailableDays()
            .Select(day => DateTime.TryParseExact(
                day,
                DayFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? (DateTime?)parsed.Date
                : null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
    }

    private static DailyStats Clone(DailyStats source, DateTime date)
    {
        return new DailyStats(date)
        {
            KeyPresses = Math.Max(0, source.KeyPresses),
            KeyPressCounts = new Dictionary<string, int>(source.KeyPressCounts, StringComparer.Ordinal),
            LeftClicks = Math.Max(0, source.LeftClicks),
            RightClicks = Math.Max(0, source.RightClicks),
            MiddleClicks = Math.Max(0, source.MiddleClicks),
            SideBackClicks = Math.Max(0, source.SideBackClicks),
            SideForwardClicks = Math.Max(0, source.SideForwardClicks),
            MouseDistance = Math.Max(0, source.MouseDistance),
            ScrollDistance = Math.Max(0, source.ScrollDistance),
            PeakKPS = Math.Max(0, source.PeakKPS),
            PeakCPS = Math.Max(0, source.PeakCPS),
            AppStats = source.AppStats.ToDictionary(pair => pair.Key, pair => new AppStats(pair.Value))
        };
    }

    private static int SafeAdd(int left, long right)
    {
        if (right <= 0) return Math.Max(0, left);
        var total = (long)Math.Max(0, left) + right;
        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }
}
