using System.Text.Json.Nodes;
using NetPulse.Core.Records;

namespace NetPulse.Core.Evidence;

/// <summary>한 표본에서 본 링크 상태 (M4 설계 A2). Removed는 다운으로 다룬다.</summary>
public enum LinkState { Up, Down, Unknown, Removed }

public enum LinkSource { Direct, Log }

/// <summary>수집 능력 (M4 설계 A4 첫째 축).</summary>
public enum LinkCapability { Supported, Unsupported, Denied }

/// <summary>R2 네 표시 상태. 값 순서가 우선순위다: 위에서부터 처음 맞는 것 (M4 설계 A4, 2-1).</summary>
public enum LinkDisplay { Down, Denied, Unsupported, ObservedNone }

/// <summary>로그 이벤트와 어댑터의 대응 (M4 설계 A3).</summary>
public enum LinkMatch { Linked, Unlinked, Ambiguous, Historical }

/// <summary>관측기가 아는 어댑터. Key(GUID 등)는 기록하지 않는다 (세션이 별칭을 붙인다).</summary>
public sealed record AdapterInfo(string Key, int InterfaceIndex, string Kind, bool Virtual)
{
    public bool Physical => !Virtual && Kind is "wired" or "wireless";
}

/// <summary>직접 관측 표본 하나. 시각은 세션이 받는 순간 붙인다 (상태 표본이지 전이 시각이 아님).</summary>
/// <param name="FromPoll">회차 읽기에서 나온 표본이면 true (관측 범위 계산에 씀). 알림에서 나온 표본은 false</param>
public sealed record LinkSample(AdapterInfo Adapter, LinkState State, bool FromPoll, string? Code = null);

/// <summary>
/// 인터페이스 상태 직접 관측 (M4 설계 A2). 실제 구현은 Platform(GetIfEntry2 + NotifyIpInterfaceChange).
/// 표본은 아무 스레드에서나 sink로 온다. Watch·Poll은 엔진 회차에서 부른다 (빨라야 하고 예외를 던지지 않아야 한다).
/// </summary>
public interface ILinkObserver : IDisposable
{
    /// <summary>알림을 등록한다. 실패하면 Unsupported/Denied와 사유 코드.</summary>
    (LinkCapability Capability, string? Code) Start(Action<LinkSample> sink);

    /// <summary>감시할 인터페이스 번호를 더한다 (경로가 쓰는 것). 이미 감시 중이면 무시.</summary>
    void Watch(IReadOnlyCollection<int> interfaceIndexes);

    /// <summary>감시 중인 어댑터를 모두 읽어 sink로 보낸다 (FromPoll = true). 읽기 실패는 Unknown 표본.</summary>
    void Poll();

    /// <summary>지금까지 감시한 어댑터.</summary>
    IReadOnlyList<AdapterInfo> Adapters { get; }
}

/// <summary>로그에서 읽은 링크 이벤트 하나. AdapterKey는 대응이 Linked일 때만.</summary>
public sealed record LogLinkEvent(string? AdapterKey, LinkMatch Match, LinkState State, DateTimeOffset Wall);

/// <summary>로그 읽기 결과. 수집 능력은 어댑터마다 (M4 설계 2-1).</summary>
public sealed record LinkLogResult(
    IReadOnlyDictionary<string, (LinkCapability Capability, string? Code)> PerAdapter,
    IReadOnlyList<LogLinkEvent> Events,
    int Unparsed,
    LinkCapability Overall,
    string? OverallCode);

/// <summary>시스템·무선 로그에서 링크 이벤트를 읽는다 (M4 설계 A3). 실제 구현은 Platform.</summary>
public interface ILinkLogSource
{
    LinkLogResult Read(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<AdapterInfo> adapters, TimeSpan timeout);
}

/// <summary>확정 다운 구간 (M4 설계 A4). ToMs = 다음 up 처음 관측, 없으면 마지막 down 관측 (측정 끝까지 늘리지 않음).</summary>
public sealed record DownSpan(string Adapter, LinkSource Source, long FromMs, long ToMs, bool Physical);

/// <summary>어댑터 하나(또는 "*" = 경로 전체)·경로 하나의 근거 요약.</summary>
public sealed record LinkSummary(
    string Adapter, LinkSource Source, LinkDisplay Display, LinkCapability Capability, string? Code,
    double? Coverage, bool Partial, IReadOnlyList<string> Secondary);

/// <summary>기록에서 복원한 어댑터 (별칭).</summary>
public sealed record AdapterAt(string Alias, int InterfaceIndex, string? Kind, bool Virtual)
{
    public bool Physical => !Virtual && Kind is "wired" or "wireless";
}

/// <summary>기록 하나의 링크 근거 전체 (분석 입력).</summary>
public sealed record LinkEvidence(
    IReadOnlyList<LinkSummary> Summaries,
    IReadOnlyList<DownSpan> Downs,
    IReadOnlyDictionary<string, AdapterAt> Adapters,
    int Unmapped,
    int Unlinked)
{
    public static readonly LinkEvidence None = new([], [], new Dictionary<string, AdapterAt>(), 0, 0);

    /// <summary>그 인터페이스 번호를 쓰던 어댑터 별칭.</summary>
    public string? AdapterFor(int interfaceIndex) =>
        Adapters.Values.Where(a => a.InterfaceIndex == interfaceIndex).Select(a => a.Alias).FirstOrDefault();

    /// <summary>
    /// "링크 근거가 있다"고 말할 수 있는지: 직접·로그 중 하나라도 어떤 어댑터에서 down 또는 (부분 아닌) observed-none.
    /// 근거가 하나도 없으면 결론에 "링크 근거를 얻지 못함"을 붙인다 (M4 설계 B5).
    /// </summary>
    public bool HasEvidence => Summaries.Any(s => s.Display == LinkDisplay.Down || s.Display == LinkDisplay.ObservedNone);
}

/// <summary>R2 표시 계산 (M4 설계 A4, 2-1). 순수 함수.</summary>
public static class LinkDisplayRule
{
    public const double MinCoverage = 0.9;

    /// <param name="coverage">직접 관측의 관측 범위 (판별된 표본 비율). 로그 경로는 null</param>
    public static (LinkDisplay Display, bool Partial) Decide(LinkCapability capability, bool anyDown, double? coverage)
    {
        bool partial = coverage is double c && c < MinCoverage;
        if (anyDown) return (LinkDisplay.Down, partial);
        if (capability == LinkCapability.Denied) return (LinkDisplay.Denied, partial);
        if (capability == LinkCapability.Unsupported || partial) return (LinkDisplay.Unsupported, partial);
        return (LinkDisplay.ObservedNone, false);
    }
}

/// <summary>
/// UTC → 측정 경과 시간 (M4 설계 A6, 2-3). 회차 표본(경과 ms, 벽시계)으로 대응 구간을 만든다:
/// 이웃한 두 회차의 "벽시계 − 경과"가 1초 안에서 같으면 그 사이는 한 구간. 다르면(절전·시각 변경) 그 사이는 불확실이다
/// (변경이 정확히 언제인지 모르므로 앞뒤 어느 대응도 쓰지 않음).
/// 로그 시각이 정확히 한 구간에만 들면 환산, 아니면(불확실·여러 구간 겹침·측정 밖) null.
/// </summary>
public static class TimeMapping
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Edge = TimeSpan.FromSeconds(3);

    public static long? ToElapsedMs(IReadOnlyList<RoundData> rounds, DateTimeOffset wall)
    {
        if (rounds.Count == 0) return null;
        var hits = new HashSet<long>();
        bool uncertain = false;
        for (int i = 0; i + 1 < rounds.Count; i++)
        {
            var a = rounds[i];
            var b = rounds[i + 1];
            var offA = a.Wall - TimeSpan.FromMilliseconds(a.AtMs);
            var offB = b.Wall - TimeSpan.FromMilliseconds(b.AtMs);
            bool same = (offA - offB).Duration() <= Tolerance;
            // 시각 변경·절전 사이: 그 두 회차의 벽시계 사이 어딘가 (또는 역행이면 뒤집힌 범위)는 불확실
            var lo = a.Wall < b.Wall ? a.Wall : b.Wall;
            var hi = a.Wall < b.Wall ? b.Wall : a.Wall;
            if (!same)
            {
                if (wall >= lo && wall <= hi) uncertain = true;
                continue;
            }
            if (wall >= a.Wall && wall <= b.Wall) hits.Add(a.AtMs + (long)(wall - a.Wall).TotalMilliseconds);
        }
        var first = rounds[0];
        if (wall < first.Wall && first.Wall - wall <= Edge) hits.Add(Math.Max(0, first.AtMs - (long)(first.Wall - wall).TotalMilliseconds));
        var last = rounds[^1];
        if (wall > last.Wall && wall - last.Wall <= Edge) hits.Add(last.AtMs + (long)(wall - last.Wall).TotalMilliseconds);
        if (rounds.Count == 1 && wall == first.Wall) hits.Add(first.AtMs);
        if (uncertain || hits.Count != 1) return null;
        return hits.First();
    }
}

/// <summary>
/// 기록의 link 이벤트(M4 설계 A5) → 링크 근거. 실시간 세션과 다시 읽기가 같은 함수를 쓴다.
/// 쓰는 형식 (세션이 지킴):
/// - 직접, 상태가 바뀔 때: collection = up/down/unknown/removed, adapter, interfaceIndex, kind, virtual, eventMs(새 상태 처음 관측),
///   prevState·prevFirstMs·prevLastMs(앞 상태의 처음·마지막 관측).
/// - 직접, 끝: collection = "summary", capability, coverage, lastState·lastFirstMs·lastLastMs.
/// - 로그, 이벤트: collection = up/down, link = linked/unlinked/ambiguous, eventMs(환산 안 되면 없음), eventWall.
/// - 로그, 끝: 어댑터마다 collection = "summary", capability, code.
/// - 경로 전체 실패(어댑터 없음): collection = unsupported/denied, code (adapter 없음).
/// </summary>
public static class LinkEvidenceBuilder
{
    private sealed class Track
    {
        public readonly List<(LinkState State, long First, bool Physical)> Steps = [];
        public readonly Dictionary<int, long> LastSeen = [];   // Steps 인덱스 → 그 상태의 마지막 관측
    }

    public static LinkEvidence FromEvents(IReadOnlyList<JsonObject> events)
    {
        var links = events.Where(e => Str(e, "type") == "link").ToArray();
        if (links.Length == 0) return LinkEvidence.None;
        var adapters = new Dictionary<string, AdapterAt>();
        var direct = new Dictionary<string, Track>();
        var log = new Dictionary<string, List<(LinkState State, long Ms, bool Physical)>>();
        var raw = new Dictionary<(string, LinkSource), (LinkCapability Capability, string? Code, double? Coverage)>();
        int unmapped = 0, unlinked = 0;

        foreach (var e in links)
        {
            var source = Str(e, "source") == "log" ? LinkSource.Log : LinkSource.Direct;
            string? alias = Str(e, "adapter");
            if (alias is not null && Int(e, "interfaceIndex") is int idx)
                adapters[alias] = new AdapterAt(alias, idx, Str(e, "kind"), Bool(e, "virtual") ?? false);
            string collection = Str(e, "collection") ?? "";
            string? match = Str(e, "link");
            switch (collection)
            {
                case "up" or "down" or "unknown" or "removed":
                    if (match is "unlinked" or "ambiguous" || alias is null) { unlinked++; break; }
                    if (match is "historical") break;
                    if (Long(e, "eventMs") is not long ms) { unmapped++; break; }
                    var state = Parse(collection);
                    // 물리 어댑터인지는 그 이벤트 자체의 유형·가상 여부로 (나중 줄이 덮어쓰지 않게)
                    bool physical = !(Bool(e, "virtual") ?? false) && Str(e, "kind") is "wired" or "wireless";
                    if (source == LinkSource.Direct)
                    {
                        if (!direct.TryGetValue(alias, out var t)) direct[alias] = t = new Track();
                        if (t.Steps.Count > 0 && Long(e, "prevLastMs") is long pl) t.LastSeen[t.Steps.Count - 1] = pl;
                        t.Steps.Add((state, ms, physical));
                    }
                    else
                    {
                        if (!log.TryGetValue(alias, out var l)) log[alias] = l = [];
                        l.Add((state, ms, physical));
                    }
                    break;
                case "summary":
                    if (alias is null) break;
                    var cap = Str(e, "capability") switch { "denied" => LinkCapability.Denied, "supported" => LinkCapability.Supported, _ => LinkCapability.Unsupported };
                    raw[(alias, source)] = (cap, Str(e, "code"), Double(e, "coverage"));
                    if (source == LinkSource.Direct && direct.TryGetValue(alias, out var tr) && tr.Steps.Count > 0 && Long(e, "lastLastMs") is long ll)
                        tr.LastSeen[tr.Steps.Count - 1] = ll;
                    break;
                case "unsupported" or "denied":
                    raw[(alias ?? "*", source)] = (collection == "denied" ? LinkCapability.Denied : LinkCapability.Unsupported, Str(e, "code"), null);
                    break;
            }
        }

        var downs = new List<DownSpan>();
        foreach (var (alias, t) in direct)
        {
            for (int i = 0; i < t.Steps.Count; i++)
            {
                var cur = t.Steps[i];
                if (cur.State is not (LinkState.Down or LinkState.Removed)) continue;
                long to = i + 1 < t.Steps.Count && t.Steps[i + 1].State == LinkState.Up ? t.Steps[i + 1].First
                    : t.LastSeen.TryGetValue(i, out var seen) ? seen : cur.First;
                downs.Add(new DownSpan(alias, LinkSource.Direct, cur.First, Math.Max(cur.First, to), cur.Physical));
            }
        }
        foreach (var (alias, list) in log)
        {
            var ordered = list.OrderBy(x => x.Ms).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                if (ordered[i].State != LinkState.Down) continue;
                long to = ordered.Skip(i + 1).Where(x => x.State == LinkState.Up).Select(x => x.Ms).DefaultIfEmpty(ordered[i].Ms).First();
                downs.Add(new DownSpan(alias, LinkSource.Log, ordered[i].Ms, to, ordered[i].Physical));
            }
        }

        // 데이터는 있는데 끝 요약이 없는 경로·어댑터 = 중단된 기록 (M4 설계 A5)
        foreach (var alias in direct.Keys)
            if (!raw.ContainsKey((alias, LinkSource.Direct))) raw[(alias, LinkSource.Direct)] = (LinkCapability.Unsupported, "interrupted", null);
        foreach (var alias in log.Keys)
            if (!raw.ContainsKey((alias, LinkSource.Log))) raw[(alias, LinkSource.Log)] = (LinkCapability.Unsupported, "interrupted", null);

        var summaries = raw.Select(kv =>
        {
            var (alias, source) = kv.Key;
            bool anyDown = downs.Any(d => d.Adapter == alias && d.Source == source);
            var (display, partial) = LinkDisplayRule.Decide(kv.Value.Capability, anyDown, kv.Value.Coverage);
            var secondary = new List<string>();
            if (partial) secondary.Add("partial");
            if (display == LinkDisplay.Down && kv.Value.Capability != LinkCapability.Supported) secondary.Add(kv.Value.Capability.ToString().ToLowerInvariant());
            if (kv.Value.Code is { } code) secondary.Add(code);
            return new LinkSummary(alias, source, display, kv.Value.Capability, kv.Value.Code, kv.Value.Coverage, partial, secondary);
        }).OrderBy(s => s.Adapter, StringComparer.Ordinal).ThenBy(s => s.Source).ToArray();
        return new LinkEvidence(summaries, downs, adapters, unmapped, unlinked);
    }

    public static LinkState Parse(string s) => s switch
    {
        "up" => LinkState.Up, "down" => LinkState.Down, "removed" => LinkState.Removed, _ => LinkState.Unknown,
    };

    public static string Text(LinkState s) => s switch { LinkState.Up => "up", LinkState.Down => "down", LinkState.Removed => "removed", _ => "unknown" };

    public static string Text(LinkCapability c) => c switch { LinkCapability.Supported => "supported", LinkCapability.Denied => "denied", _ => "unsupported" };

    public static string Text(LinkDisplay d) => d switch
    {
        LinkDisplay.Down => "down", LinkDisplay.Denied => "denied", LinkDisplay.ObservedNone => "observed-none", _ => "unsupported",
    };

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static long? Long(JsonObject o, string name) => Json.Long(o[name]);
    private static int? Int(JsonObject o, string name) => Json.Long(o[name]) is long x && x is >= int.MinValue and <= int.MaxValue ? (int)x : null;
    private static bool? Bool(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : null;
    private static double? Double(JsonObject o, string name) => Json.Double(o[name]);
}

/// <summary>
/// JSON 숫자 읽기. 파일에서 읽은 값(JsonElement)과 메모리에서 만든 값(int·long·double)을 같게 다룬다
/// (메모리 값은 TryGetValue&lt;long&gt;가 int를 받아 주지 않는다).
/// </summary>
public static class Json
{
    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && !double.IsInfinity(d)) return (long)d;
        return null;
    }

    public static double? Double(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }
}
