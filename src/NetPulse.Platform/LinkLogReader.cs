using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security;
using System.Xml.Linq;
using NetPulse.Core.Evidence;

namespace NetPulse.Platform;

/// <summary>
/// 시스템·무선 로그에서 링크 이벤트를 읽는다 (M4 설계 A3). 해석표는 이 PC에서 실측으로 확인한 것만 (LinkCatalog).
/// 어댑터 대응: 지금 있는 모든 어댑터(내려간 것 포함)에서 설명이 유일하게 맞거나 GUID가 맞을 때만.
/// </summary>
public sealed class LinkLogReader : ILinkLogSource
{
    public LinkLogResult Read(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<AdapterInfo> adapters, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        var all = AllAdapters();
        var events = new List<LogLinkEvent>();
        int unparsed = 0;
        var perAdapter = new Dictionary<string, (LinkCapability, string?)>();
        try
        {
            // 유선 드라이버
            foreach (var record in Query("System", LinkCatalog.SystemQuery(fromUtc, toUtc), watch, timeout))
            {
                var parsed = LinkCatalog.ParseDriver(record.ProviderName, record.Id, record.Properties.Select(p => p.Value).ToArray());
                if (parsed is null) { unparsed++; continue; }
                var (key, match) = MatchByDescription(parsed.Value.Description, all, adapters);
                events.Add(new LogLinkEvent(key, match, parsed.Value.State, Utc(record)));
            }
            // 무선
            foreach (var record in Query("Microsoft-Windows-WLAN-AutoConfig/Operational", LinkCatalog.WlanQuery(fromUtc, toUtc), watch, timeout, optional: true))
            {
                var guid = NamedData(record, "InterfaceGuid");
                var state = LinkCatalog.WlanState(record.Id);
                if (guid is null || state is null) { unparsed++; continue; }
                var key = adapters.FirstOrDefault(a => SameGuid(a.Key, guid))?.Key;
                events.Add(new LogLinkEvent(key, key is null ? LinkMatch.Unlinked : LinkMatch.Linked, state.Value, Utc(record)));
            }
            // 어댑터별 지원 여부 (2-1): 유선은 지난 30일 해석표 공급자 이벤트가 그 어댑터에 유일하게 대응된 적이 있어야
            var lookback = DateTimeOffset.UtcNow - LinkCatalog.SupportLookback;
            var supportedWired = new HashSet<string>();
            foreach (var record in Query("System", LinkCatalog.SystemQuery(lookback, DateTimeOffset.UtcNow), watch, timeout, maxEvents: 2000))
            {
                var parsed = LinkCatalog.ParseDriver(record.ProviderName, record.Id, record.Properties.Select(p => p.Value).ToArray());
                if (parsed is null) continue;
                var (key, match) = MatchByDescription(parsed.Value.Description, all, adapters);
                if (match == LinkMatch.Linked && key is not null) supportedWired.Add(key);
            }
            bool wlanReadable = LogReadable("Microsoft-Windows-WLAN-AutoConfig/Operational");
            foreach (var a in adapters)
            {
                if (a.Kind == "wired") perAdapter[a.Key] = supportedWired.Contains(a.Key) ? (LinkCapability.Supported, null) : (LinkCapability.Unsupported, "no-known-driver");
                else if (a.Kind == "wireless") perAdapter[a.Key] = wlanReadable ? (LinkCapability.Supported, null) : (LinkCapability.Unsupported, "wlan-log-unavailable");
                else perAdapter[a.Key] = (LinkCapability.Unsupported, "no-known-driver");
            }
            if (watch.Elapsed > timeout) return new LinkLogResult(perAdapter, events, unparsed, LinkCapability.Unsupported, "timeout");
            return new LinkLogResult(perAdapter, events, unparsed, LinkCapability.Supported, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new LinkLogResult(perAdapter, [], 0, LinkCapability.Denied, "access-denied");
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new LinkLogResult(perAdapter, [], 0, LinkCapability.Unsupported, ex is EventLogNotFoundException ? "log-not-found" : "log-error:" + ex.GetType().Name);
        }
        catch (TimeoutException)
        {
            return new LinkLogResult(perAdapter, events, unparsed, LinkCapability.Unsupported, "timeout");
        }
    }

    private static IEnumerable<EventRecord> Query(string log, string xpath, Stopwatch watch, TimeSpan timeout, bool optional = false, int maxEvents = 5000)
    {
        EventLogReader reader;
        try { reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath)); }
        catch (EventLogNotFoundException) when (optional) { yield break; }
        using (reader)
        {
            int n = 0;
            while (n++ < maxEvents)
            {
                if (watch.Elapsed > timeout) throw new TimeoutException();
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch (EventLogNotFoundException) when (optional) { yield break; }
                if (record is null) yield break;
                using (record) yield return record;
            }
        }
    }

    private static bool LogReadable(string log)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, "*[System[EventID=0]]"));
            reader.ReadEvent()?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or SecurityException) { return false; }
    }

    private static DateTimeOffset Utc(EventRecord r) => new(r.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow, TimeSpan.Zero);

    private static string? NamedData(EventRecord r, string name)
    {
        try
        {
            var xml = XDocument.Parse(r.ToXml());
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            return xml.Descendants(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or EventLogException) { return null; }
    }

    private static bool SameGuid(string key, string text) =>
        Guid.TryParse(key, out var a) && Guid.TryParse(text, out var b) && a == b;

    private sealed record AnyAdapter(string Key, string Description);

    private static IReadOnlyList<AnyAdapter> AllAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => new AnyAdapter(Guid.TryParse(n.Id, out var g) ? g.ToString("B").ToUpperInvariant() : n.Id, n.Description)).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    /// <summary>설명이 모든 어댑터 중 정확히 하나와 같고, 그것이 측정 어댑터면 Linked.</summary>
    private static (string? Key, LinkMatch Match) MatchByDescription(string description, IReadOnlyList<AnyAdapter> all, IReadOnlyList<AdapterInfo> measured)
    {
        var same = all.Where(a => string.Equals(a.Description, description, StringComparison.Ordinal)).ToArray();
        if (same.Length > 1) return (null, LinkMatch.Ambiguous);
        if (same.Length == 0) return (null, LinkMatch.Unlinked);
        var key = measured.FirstOrDefault(m => string.Equals(m.Key, same[0].Key, StringComparison.OrdinalIgnoreCase))?.Key;
        return key is null ? (null, LinkMatch.Unlinked) : (key, LinkMatch.Linked);
    }
}

/// <summary>
/// 로그 해석표 (M4 설계 A3). 이 PC에서 실측으로 확인한 공급자·Id·필드 위치만 넣는다. 다른 공급자는 추측해 넣지 않는다.
/// </summary>
public static class LinkCatalog
{
    /// <summary>System 로그의 Intel 유선 드라이버 (이 PC 실측): 27 = 링크 다운, 32 = 링크 업. 이벤트 데이터 두 번째(문자열) = 어댑터 설명.</summary>
    public static readonly IReadOnlyList<string> DriverProviders = ["e2fexpress", "e2fnexpress"];
    public const int DriverDown = 27, DriverUp = 32;
    public const int WlanDisconnected = 8003, WlanConnected = 8001;
    public static readonly TimeSpan SupportLookback = TimeSpan.FromDays(30);

    public static string SystemQuery(DateTimeOffset from, DateTimeOffset to) =>
        "*[System[(" + string.Join(" or ", DriverProviders.Select(p => $"Provider[@Name='{p}']")) + $") and (EventID={DriverDown} or EventID={DriverUp})"
        + $" and TimeCreated[@SystemTime>='{Iso(from)}' and @SystemTime<='{Iso(to)}']]]";

    public static string WlanQuery(DateTimeOffset from, DateTimeOffset to) =>
        $"*[System[(EventID={WlanDisconnected} or EventID={WlanConnected}) and TimeCreated[@SystemTime>='{Iso(from)}' and @SystemTime<='{Iso(to)}']]]";

    /// <summary>드라이버 이벤트 해석. 공급자·Id가 표에 없거나 필드가 없거나 자료형이 다르면 null (해석 안 됨).</summary>
    public static (LinkState State, string Description)? ParseDriver(string? provider, int id, IReadOnlyList<object?> properties)
    {
        if (provider is null || !DriverProviders.Contains(provider, StringComparer.OrdinalIgnoreCase)) return null;
        LinkState? state = id switch { DriverDown => LinkState.Down, DriverUp => LinkState.Up, _ => null };
        if (state is null || properties.Count < 2 || properties[1] is not string description || description.Length == 0) return null;
        return (state.Value, description);
    }

    public static LinkState? WlanState(int id) => id switch { WlanDisconnected => LinkState.Down, WlanConnected => LinkState.Up, _ => null };

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
