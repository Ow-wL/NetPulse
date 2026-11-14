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
    public const int LinkQueueCapacityDefault = 1024;
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
        public long OverflowSeq;   // 마지막으로 처리한 표본이 실은 넘침 순번
    }

    /// <param name="OverflowSeq">이 표본을 큐에 넣을 때까지 등록된 넘침 수 (넣기와 넘침 등록은 같은 잠금 안에서 일어난다)</param>
    private readonly record struct LinkItem(LinkSample Sample, long Ms, DateTimeOffset Wall, long OverflowSeq);

    private Channel<LinkItem> linkQueue = null!;

    private void CreateLinkQueue(int capacity) => linkQueue = Channel.CreateBounded<LinkItem>(new BoundedChannelOptions(Math.Max(1, capacity))
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
    });

    /// <summary>시험 전용: 큐 넣기에 실패한 직후(넘침 등록 전, 잠금 안)에 부른다.</summary>
    internal Action? TestHookAfterLinkEnqueueFailed { get; set; }
    /// <summary>시험 전용: 표본이 들어와 잠금을 잡기 직전 (재관측 생산자의 진입 신호, G4 M5 1라운드 C1).</summary>
    internal Action? TestHookLinkSampleArrived { get; set; }
    /// <summary>시험 전용: 처리 작업이 큐에서 표본을 꺼낸 직후, 잠금 전 (소비자의 배출 신호).</summary>
    internal Action? TestHookLinkDequeued { get; set; }
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
    /// <summary>
    /// 관측기 표본 (아무 스레드). 시각 붙이기 → 큐 넣기 → (넘치면) 넘침 등록을 한 잠금 안에서 한다 (M4 G4 5라운드 1):
    /// 큐 순서가 시각 순서와 같고, 넘친 표본보다 뒤 표본은 그 넘침이 등록된 뒤에야 큐에 들어간다.
    /// 그래서 처리 작업은 표본에 실린 넘침 순번만 보고 "직전 표본과 이 표본 사이에 넘침이 있었는지"를 정확히 안다.
    /// </summary>
    private void OnLinkSample(LinkSample sample)
    {
        if (linkClosed || directCapability != LinkCapability.Supported) return;
        TestHookLinkSampleArrived?.Invoke();
        lock (linkGate)
        {
            var item = new LinkItem(sample, (long)clock.Elapsed.TotalMilliseconds, clock.Now, linkOverflow);
            if (item.Ms > Volatile.Read(ref linkCutoffMs)) return;   // 고정한 측정 끝 뒤의 표본은 받지 않는다 (G4 3라운드 2)
            if (linkQueue.Writer.TryWrite(item)) return;
            TestHookAfterLinkEnqueueFailed?.Invoke();
            linkOverflow++;
            AddOverflowSpan(overflowSpans, item.Ms);
            if (sample.FromPoll) overflowByKey[sample.Adapter.Key] = overflowByKey.GetValueOrDefault(sample.Adapter.Key) + 1;
        }
    }

    /// <summary>
    /// 넘침 시각을 범위로 더한다. 1초 안이면 앞 범위를 늘린다. 범위가 상한(50)에 닿으면 이후 넘침은 마지막 범위를 늘려
    /// 보수적으로 덮는다: 시각을 잃지 않고, 그 사이 상태 연속성을 끊게 한다 (M4 G4 4라운드 1).
    /// </summary>
    internal static void AddOverflowSpan(List<(long From, long To)> spans, long ms)
    {
        if (spans.Count > 0 && (ms - spans[^1].To <= 1000 || spans.Count >= MaxUnobservedSpans))
            spans[^1] = (spans[^1].From, Math.Max(spans[^1].To, ms));
        else spans.Add((ms, ms));
    }

    /// <summary>시험 전용: 링크 수집이 닫혔는지.</summary>
    internal bool LinkClosed => linkClosed;

    /// <summary>시험 전용: 큐에 있는 링크 표본 수.</summary>
    internal int LinkQueueCount => linkQueue.Reader.Count;

    /// <summary>시험 전용: 링크 표본 마감 시각을 바꾼다 (받을 때·꺼낼 때 검사를 따로 시험, M4 이월 1).</summary>
    internal void TestSetLinkCutoff(long ms) => Volatile.Write(ref linkCutoffMs, ms);

    /// <summary>큐에 쌓인 링크 표본을 처리한다 (처리 작업, 또는 처리 작업이 멈춘 뒤 끝에서).</summary>
    private void DrainLinks()
    {
        while (linkQueue.Reader.TryRead(out var item))
        {
            TestHookLinkDequeued?.Invoke();
            ProcessLink(item);
        }
    }

    /// <summary>측정 끝 경계 (경과 ms). 끝낼 때 정리 전에 고정한다. 그 뒤 표본은 큐에 있어도 버린다.</summary>
    private long linkCutoffMs = long.MaxValue;   // 생성자에서 계획 끝으로, 끝낼 때 min(중지, 계획 끝)으로 (4라운드 2)

    private void ProcessLink(LinkItem item)
    {
        if (item.Ms > Volatile.Read(ref linkCutoffMs)) return;
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
            // 직전 표본과 이 표본 사이에 넘침이 있었으면 상태가 같아도 연속으로 보지 않는다:
            // 넘침 전 마지막 관측과 넘침 뒤 재관측을 따로 남긴다 (G4 3라운드 1)
            bool afterOverflow = track.State is not null && item.OverflowSeq > track.OverflowSeq;
            track.OverflowSeq = item.OverflowSeq;
            // 같은 상태가 이어지면 줄을 쓰지 않는다. 단 번호가 바뀌거나 넘침을 건넜으면 쓴다 (G4 1라운드 5)
            if (track.State == state && !reindexed && !afterOverflow) { track.Last = item.Ms; return; }
        }
        var fields = AdapterFields(track);
        fields["collection"] = LinkEvidenceBuilder.Text(item.Sample.State);
        fields["eventMs"] = item.Ms;
        fields["eventWall"] = RecordSchema.Iso(item.Wall);
        if (item.Sample.Code is not null) fields["code"] = item.Sample.Code;
        string eventId;
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
            // 트랙 상태와 이벤트 등록을 같은 잠금 안에서: 실시간 사본(M5 설계 3.2)이 둘 중 하나만 보지 않게
            eventId = RegisterEvent(RecordSchema.EventLine(Id, "link", null, item.Ms, item.Wall, fields), null);
        }
        RetryEvents();
        PublishEvent(eventId);
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
            // 요약 줄은 잠정 분석이 쓰는 것과 같은 함수로 만든다 (DirectSummary, M5 설계 3.2).
            // 등록·게시·"요약을 썼음" 표시를 한 경계(linkGate → sync) 안에서: 사본(게시한 이벤트만 담음)과 전달이
            // 실제 요약과 잠정 요약을 함께 보거나, 둘 다 못 보는 순간이 없게 (G4 M5 1라운드 A1·2라운드 1).
            // 게시는 쓰기 대기 상태로 하고, 디스크 쓰기 결과는 상태 변경 항목으로 뒤따른다
            lock (linkGate)
            {
                foreach (var t in tracks) CloseUnknown(t);
                var lines = DirectSummary.Lines(tracks.Select(SnapshotTrack).ToArray(), linkOverflow, overflowSpans.ToArray());
                var ids = lines.Select(fields => RegisterEvent(RecordSchema.EventLine(Id, "link", null, at, endWall, fields), null)).ToArray();
                foreach (var id in ids) PublishEvent(id);
                linkSummaryWritten = true;
            }
            RetryEvents();
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
