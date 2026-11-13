using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NetPulse.Core.Evidence;
using NetPulse.Core.Records;

namespace NetPulse.Core.Measurement;

/// <summary>
/// 세션의 링크 근거 수집 (M4 설계 A2·A3·A5). 직접 관측 표본은 회차와 별개의 제한 큐로 처리 작업에 들어가
/// 회차와 같은 작업이 순서대로 쓴다. 끝날 때 어댑터마다 요약 줄, 그 다음 로그를 읽어 쓴다.
/// </summary>
public sealed partial class MeasurementSession
{
    public const int LinkQueueCapacity = 1024;
    private const int MaxUnobservedSpans = 50;

    private sealed class AdapterTrack
    {
        public required string Alias;
        public required AdapterInfo Info;
        public LinkState? State;
        public long First, Last;
        public long Watched, Observed;
        public readonly List<(long From, long To)> Unobserved = [];
        public int UnobservedOverflow;
        public long? UnknownFrom, UnknownLast;
    }

    private readonly record struct LinkItem(LinkSample Sample, long Ms, DateTimeOffset Wall);

    private readonly Channel<LinkItem> linkQueue = Channel.CreateBounded<LinkItem>(new BoundedChannelOptions(LinkQueueCapacity)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Dictionary<string, AdapterTrack> adapterTracks = [];   // Key → 추적 (처리 작업 또는 끝에서만, linkGate 안)
    private readonly Dictionary<string, long> overflowByKey = [];
    private readonly object linkGate = new();
    private long linkOverflow;
    private readonly List<(long From, long To)> overflowSpans = [];   // 넘친 시각 범위 (linkGate 안)
    private volatile bool linkClosed;
    private LinkCapability directCapability = LinkCapability.Unsupported;
    private string? directCode = "not-configured";

    /// <summary>엔진 회차에서: 경로를 보고, 경로가 쓰는 인터페이스를 감시에 넣고, 감시 어댑터를 읽는다.</summary>
    private sealed class RoutesWithLinks(IRouteObserver? routes, ILinkObserver links, Func<bool> enabled) : IRouteObserver
    {
        public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round)
        {
            var samples = routes?.Observe(targets, round) ?? [];
            if (!enabled()) return samples;   // 알림 등록이 실패하면 직접 관측 전체를 끈다 (수집 능력과 기록이 어긋나지 않게)
            try
            {
                var used = samples.Where(s => s.Ok && s.InterfaceIndex is not null).Select(s => s.InterfaceIndex!.Value).Distinct().ToArray();
                if (used.Length > 0) links.Watch(used);
                links.Poll();
            }
            catch (Exception) { /* 관측기 오류가 측정을 멈추지 않게 한다 (그 회차는 관측 범위에서 빠짐) */ }
            return samples;
        }
    }

    private void StartLinks()
    {
        if (options.Links is not { } links) return;
        try { (directCapability, directCode) = links.Start(OnLinkSample); }
        catch (Exception ex) { (directCapability, directCode) = (LinkCapability.Unsupported, "start-exception:" + ex.GetType().Name); }
        if (directCapability != LinkCapability.Supported)
        {
            var fields = new JsonObject { ["source"] = "direct", ["collection"] = LinkEvidenceBuilder.Text(directCapability), ["code"] = directCode };
            WriteEvent(RecordSchema.EventLine(Id, "link", null, (long)clock.Elapsed.TotalMilliseconds, clock.Now, fields));
        }
    }

    /// <summary>관측기 표본 (아무 스레드). 시각을 붙여 큐에 넣는다. 닫힌 뒤 온 것은 버린다.</summary>
    private void OnLinkSample(LinkSample sample)
    {
        if (linkClosed || directCapability != LinkCapability.Supported) return;
        var item = new LinkItem(sample, (long)clock.Elapsed.TotalMilliseconds, clock.Now);
        if (linkQueue.Writer.TryWrite(item)) return;
        lock (linkGate)
        {
            linkOverflow++;
            if (overflowSpans.Count > 0 && item.Ms - overflowSpans[^1].To <= 1000) overflowSpans[^1] = (overflowSpans[^1].From, item.Ms);
            else if (overflowSpans.Count < MaxUnobservedSpans) overflowSpans.Add((item.Ms, item.Ms));
            if (sample.FromPoll) overflowByKey[sample.Adapter.Key] = overflowByKey.GetValueOrDefault(sample.Adapter.Key) + 1;
        }
    }

    /// <summary>큐에 쌓인 링크 표본을 처리한다 (처리 작업, 또는 처리 작업이 멈춘 뒤 끝에서).</summary>
    private void DrainLinks()
    {
        while (linkQueue.Reader.TryRead(out var item)) ProcessLink(item);
    }

    private void ProcessLink(LinkItem item)
    {
        AdapterTrack track;
        lock (linkGate)
        {
            if (!adapterTracks.TryGetValue(item.Sample.Adapter.Key, out track!))
            {
                track = new AdapterTrack { Alias = "a" + (adapterTracks.Count + 1), Info = item.Sample.Adapter };
                adapterTracks[item.Sample.Adapter.Key] = track;
            }
            bool reindexed = track.Info.InterfaceIndex != item.Sample.Adapter.InterfaceIndex;
            track.Info = item.Sample.Adapter;   // 번호·유형이 바뀌었을 수 있다
            var state = item.Sample.State;
            bool determinate = state is LinkState.Up or LinkState.Down or LinkState.Removed;
            if (item.Sample.FromPoll)
            {
                track.Watched++;
                if (determinate) track.Observed++;
                // 미관측 구간 (판별 못 한 회차 읽기)
                if (!determinate) { track.UnknownFrom ??= item.Ms; track.UnknownLast = item.Ms; }
                else CloseUnknown(track);
            }
            // 같은 상태가 이어지면 줄을 쓰지 않는다. 단 번호가 바뀌면 이력용으로 쓴다 (G4 1라운드 5)
            if (track.State == state && !reindexed) { track.Last = item.Ms; return; }
        }
        var fields = AdapterFields(track);
        fields["collection"] = LinkEvidenceBuilder.Text(item.Sample.State);
        fields["eventMs"] = item.Ms;
        fields["eventWall"] = RecordSchema.Iso(item.Wall);
        if (item.Sample.Code is not null) fields["code"] = item.Sample.Code;
        lock (linkGate)
        {
            if (track.State is { } prev)
            {
                fields["prevState"] = LinkEvidenceBuilder.Text(prev);
                fields["prevFirstMs"] = track.First;
                fields["prevLastMs"] = track.Last;
            }
            track.State = item.Sample.State;
            track.First = track.Last = item.Ms;
        }
        WriteEvent(RecordSchema.EventLine(Id, "link", null, item.Ms, item.Wall, fields));
    }

    private static void CloseUnknown(AdapterTrack t)
    {
        if (t.UnknownFrom is not long from) return;
        if (t.Unobserved.Count < MaxUnobservedSpans) t.Unobserved.Add((from, t.UnknownLast ?? from));
        else t.UnobservedOverflow++;
        t.UnknownFrom = t.UnknownLast = null;
    }

    private static JsonObject AdapterFields(AdapterTrack t) => new()
    {
        ["source"] = "direct", ["adapter"] = t.Alias, ["interfaceIndex"] = t.Info.InterfaceIndex, ["kind"] = t.Info.Kind, ["virtual"] = t.Info.Virtual,
    };

    /// <summary>끝: 직접 관측 요약 줄 (어댑터마다), 그 다음 로그 (M4 설계 A3·A5). 처리 작업이 멈춘 뒤 끝 쓰기 작업에서 부른다.</summary>
    private void FinishLinks(TimeSpan endElapsed, DateTimeOffset endWall)
    {
        if (options.Links is null && options.LinkLog is null) return;
        DrainLinks();
        long at = (long)endElapsed.TotalMilliseconds;
        AdapterTrack[] tracks;
        lock (linkGate) tracks = adapterTracks.Values.ToArray();

        if (options.Links is not null && directCapability == LinkCapability.Supported)
        {
            if (tracks.Length == 0)
                WriteEvent(RecordSchema.EventLine(Id, "link", null, at, endWall,
                    new JsonObject { ["source"] = "direct", ["collection"] = "unsupported", ["code"] = "no-interface" }));
            foreach (var t in tracks)
            {
                JsonObject fields;
                lock (linkGate)
                {
                    CloseUnknown(t);
                    long missed = overflowByKey.GetValueOrDefault(t.Info.Key);
                    long watched = t.Watched + missed;
                    fields = AdapterFields(t);
                    fields["collection"] = "summary";
                    fields["capability"] = "supported";
                    fields["watchedRounds"] = watched;
                    fields["observedRounds"] = t.Observed;
                    fields["coverage"] = watched == 0 ? 0.0 : Math.Round((double)t.Observed / watched, 4);
                    fields["unobserved"] = new JsonArray(t.Unobserved.Select(u => (JsonNode)new JsonArray(u.From, u.To)).ToArray());
                    if (t.UnobservedOverflow > 0) fields["unobservedMore"] = t.UnobservedOverflow;
                    if (missed > 0) fields["overflow"] = missed;
                    if (t.State is { } last)
                    {
                        fields["lastState"] = LinkEvidenceBuilder.Text(last);
                        fields["lastFirstMs"] = t.First;
                        fields["lastLastMs"] = t.Last;
                    }
                }
                WriteEvent(RecordSchema.EventLine(Id, "link", null, at, endWall, fields));
            }
            long totalOverflow;
            (long From, long To)[] spans;
            lock (linkGate) { totalOverflow = linkOverflow; spans = overflowSpans.ToArray(); }
            if (totalOverflow > 0)
                WriteEvent(RecordSchema.EventLine(Id, "link", null, at, endWall, new JsonObject
                {
                    ["source"] = "direct", ["collection"] = "overflow", ["count"] = totalOverflow,
                    ["spans"] = new JsonArray(spans.Select(x => (JsonNode)new JsonArray(x.From, x.To)).ToArray()),
                }));
        }

        if (options.LinkLog is { } logSource) ReadLinkLog(logSource, tracks, at, endWall);
    }

    private void ReadLinkLog(ILinkLogSource logSource, AdapterTrack[] tracks, long at, DateTimeOffset endWall)
    {
        // 측정 끝 3초 뒤까지 로그가 쌓이기를 기다린다 (벽시계, 최대 LinkLogSettle)
        var settle = options.LinkLogSettle;
        if (settle > TimeSpan.Zero)
        {
            var wait = endWall + settle - clock.Now;
            if (wait > settle) wait = settle;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
        LinkLogResult result;
        var watch = Stopwatch.StartNew();
        // 조회 범위는 측정 중 본 벽시계 전체 (시각이 뒤로 갔으면 시작보다 이른 벽시계도 측정 중이다, 2라운드 2)
        RoundData[] seenRounds;
        lock (sync) seenRounds = observed.ToArray();
        var walls = seenRounds.Select(r => r.Wall).Append(header.StartedAt).Append(endWall).ToArray();
        var queryFrom = walls.Min() - TimeSpan.FromSeconds(3);
        var queryTo = walls.Max() + settle;
        try { result = logSource.Read(queryFrom, queryTo, tracks.Select(t => t.Info).ToArray(), options.LinkLogTimeout); }
        catch (Exception ex)
        {
            result = new LinkLogResult(new Dictionary<string, (LinkCapability, string?)>(), [], 0, LinkCapability.Unsupported, "read-exception:" + ex.GetType().Name);
        }
        if (watch.Elapsed > options.LinkLogTimeout && result.Overall == LinkCapability.Supported)
            result = result with { Overall = LinkCapability.Unsupported, OverallCode = "timeout" };

        if (result.Overall != LinkCapability.Supported || tracks.Length == 0)
        {
            WriteEvent(RecordSchema.EventLine(Id, "link", null, at, endWall, new JsonObject
            {
                ["source"] = "log", ["collection"] = LinkEvidenceBuilder.Text(result.Overall == LinkCapability.Supported ? LinkCapability.Unsupported : result.Overall),
                ["code"] = result.Overall == LinkCapability.Supported ? "no-interface" : result.OverallCode,
            }));
            if (result.Overall != LinkCapability.Supported) return;
        }

        RoundData[] rounds;
        lock (sync) rounds = observed.ToArray();
        var byKey = tracks.ToDictionary(t => t.Info.Key);
        foreach (var e in result.Events.OrderBy(e => e.Wall))
        {
            var fields = new JsonObject { ["source"] = "log", ["collection"] = LinkEvidenceBuilder.Text(e.State), ["eventWall"] = RecordSchema.Iso(e.Wall) };
            if (e.Match == LinkMatch.Linked && e.AdapterKey is { } key && byKey.TryGetValue(key, out var t))
            {
                fields["adapter"] = t.Alias;
                fields["interfaceIndex"] = t.Info.InterfaceIndex;
                fields["kind"] = t.Info.Kind;
                fields["virtual"] = t.Info.Virtual;
                fields["link"] = "linked";
            }
            else fields["link"] = e.Match == LinkMatch.Linked ? "unlinked" : e.Match.ToString().ToLowerInvariant();
            // 측정 중으로 환산되는 것만 근거. 측정 밖이거나 불확실(시각 변경 사이)이면 참고(historical)로만 (G4 1·2라운드 3·2)
            if (TimeMapping.ToElapsedMs(rounds, e.Wall, at) is long ms) fields["eventMs"] = ms;
            else if (fields["link"] is not null && (string?)fields["link"] == "linked") fields["link"] = "historical";
            WriteEvent(RecordSchema.EventLine(Id, "link", null, at, endWall, fields));
        }
        foreach (var t in tracks)
        {
            var (cap, code) = result.PerAdapter.TryGetValue(t.Info.Key, out var c) ? c : (LinkCapability.Unsupported, "no-known-driver");
            var fields = new JsonObject
            {
                ["source"] = "log", ["adapter"] = t.Alias, ["interfaceIndex"] = t.Info.InterfaceIndex, ["kind"] = t.Info.Kind, ["virtual"] = t.Info.Virtual,
                ["collection"] = "summary", ["capability"] = LinkEvidenceBuilder.Text(cap), ["code"] = code,
            };
            if (result.Unparsed > 0) fields["unparsed"] = result.Unparsed;
            WriteEvent(RecordSchema.EventLine(Id, "link", null, at, endWall, fields));
        }
    }
}
