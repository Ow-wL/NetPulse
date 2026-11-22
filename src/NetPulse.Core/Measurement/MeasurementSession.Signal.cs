using System.Text.Json.Nodes;
using System.Threading.Channels;
using NetPulse.Core.Evidence;
using NetPulse.Core.Records;

namespace NetPulse.Core.Measurement;

/// <summary>
/// 세션의 Wi‑Fi 신호 기록 (M12 설계 C [1-4]·[2-2]·[2-3]).
/// <list type="bullet">
/// <item>읽기: 엔진 회차에서 링크 감시(Poll) 뒤, 경로가 쓰는 무선 어댑터를 읽는다 (측정 엔진 쓰기 경로 밖, 링크 수집과 같은 쪽).
/// 한 회차의 읽기를 한 항목으로 제한 큐에 넣고, 처리 작업이 링크 표본 다음에 꺼내 쓴다.</item>
/// <item>기록: 값·상태가 바뀔 때 변경 이벤트, 반복 표본은 줄로 쓰지 않고 60초마다 체크포인트, 어댑터 관측 종료·측정 끝에 마지막 체크포인트,
/// 측정 끝에 요약. 요약·재계산은 <see cref="SignalStatistics.Compute"/> 하나를 쓴다.</item>
/// <item>선을 끊는 경우: 정상이 아닌 상태(값 없음), 측정 공백(절전·멈춤), 큐 넘침, 어댑터 바뀜(별칭이 다름)·번호 바뀜.</item>
/// <item>별칭은 링크 근거와 같은 할당기(<see cref="AliasFor"/>)를 쓴다: 같은 어댑터 Key면 링크 줄과 신호 줄의 별칭이 같다. Key(GUID)는 기록하지 않는다.</item>
/// </list>
/// 트랙은 linkGate 안에서만 바꾼다 (실시간 사본이 링크와 같은 잠금으로 찍는다). 잠금 순서 linkGate → sync.
/// </summary>
public sealed partial class MeasurementSession
{
    public const int SignalQueueCapacity = 4096;
    /// <summary>반복 표본만 이어질 때 체크포인트 간격 (M12 설계 C [2-2]): 비정상 종료 때 잃는 것은 최대 이만큼이다.</summary>
    public const long SignalCheckpointMs = 60_000;

    /// <summary>한 회차의 신호 읽기. 넘침 순번·전원 변화 순번은 넣을 때의 값 (처리 작업이 사이의 끊김을 안다).</summary>
    private sealed record SignalItem(long Round, long Ms, DateTimeOffset Wall, (AdapterInfo Adapter, SignalReading Reading)[] Readings, long LostSeq, long PowerSeq);

    private sealed class SignalTrack
    {
        public required string Alias;
        public required AdapterInfo Info;
        public SignalRun? Run;                       // 지금 묶음 (관측 종료면 null)
        public readonly List<SignalRun> Closed = [];
        public SignalRun[]? ClosedCache;             // 실시간 사본용 (닫힌 묶음이 바뀔 때만 다시 만든다)
        public string? Method, Code;
        public long LastRound = -1, LostSeq, PowerSeq, LastWrittenMs;
        public long? EndedMs;
        public long Samples, QualitySamples, RssiSamples;
        public SignalStateCounts StateSamples;
    }

    private readonly Channel<SignalItem> signalQueue = Channel.CreateBounded<SignalItem>(new BoundedChannelOptions(SignalQueueCapacity)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
    });
    private long signalLost;                                                       // 큐에 넣지 못한 회차 수 (Interlocked)
    private readonly Dictionary<string, SignalTrack> signalTracks = [];             // Key → 트랙 (linkGate 안)
    private readonly Dictionary<string, string> adapterAliases = [];               // Key → 별칭, 링크·신호 공용 (linkGate 안)
    private string[] signalWatched = [];                                           // 지난 회차에 읽은 어댑터 Key (엔진 회차에서만)
    private volatile bool signalClosed;
    private volatile bool signalSummaryWritten;

    private static long IntervalMs => (long)NetPulse.Core.Targets.Profile.Interval.TotalMilliseconds;

    /// <summary>linkGate 안: 어댑터 Key의 별칭. 링크 트랙과 신호 트랙이 같은 별칭을 쓰게 한 곳에서 붙인다.</summary>
    private string AliasFor(AdapterInfo adapter)
    {
        if (!adapterAliases.TryGetValue(adapter.Key, out var alias)) adapterAliases[adapter.Key] = alias = "a" + (adapterAliases.Count + 1);
        return alias;
    }

    /// <summary>시험 전용: 신호 큐에 있는 회차 수.</summary>
    internal int SignalQueueCount => signalQueue.Reader.Count;

    /// <summary>
    /// 엔진 회차에서 (링크 감시 뒤): 이번 회차 경로가 쓰는 인터페이스의 무선 어댑터를 읽는다.
    /// 경로가 하나도 없으면(끊김) 지난 회차에 읽은 어댑터를 계속 읽는다: 끊긴 동안의 "연결 안 됨"도 근거다.
    /// 경로가 유선으로 바뀌어 무선 어댑터가 빠지면 그 어댑터의 관측이 끝난다 (처리 작업이 마지막 체크포인트를 쓴다).
    /// 공급자 예외는 그 어댑터의 읽기 실패로 기록하고 측정은 계속한다.
    /// </summary>
    private void ObserveSignal(IReadOnlyCollection<int> used, IReadOnlyList<AdapterInfo> adapters, long round)
    {
        if (options.Signal is not { } source || signalClosed) return;
        var wireless = adapters.Where(a => a.Kind == "wireless");
        var chosen = (used.Count > 0 ? wireless.Where(a => used.Contains(a.InterfaceIndex)) : wireless.Where(a => signalWatched.Contains(a.Key)))
            .GroupBy(a => a.Key).Select(g => g.First()).ToArray();
        if (chosen.Length == 0 && signalWatched.Length == 0) return;
        signalWatched = chosen.Select(a => a.Key).ToArray();
        long ms = (long)clock.Elapsed.TotalMilliseconds;
        var wall = clock.Now;
        if (ms > Volatile.Read(ref linkCutoffMs)) return;   // 측정 끝 경계 뒤의 표본은 받지 않는다 (링크 표본과 같은 경계)
        var readings = chosen.Select(a => (a, SafeRead(source, a))).ToArray();
        var item = new SignalItem(round, ms, wall, readings, Interlocked.Read(ref signalLost), gaps.PowerChanges);
        if (!signalQueue.Writer.TryWrite(item)) Interlocked.Increment(ref signalLost);
    }

    /// <summary>공급자 예외를 읽기 실패(사유 = 예외 형식 이름)로 바꾸고 기록 계약에 맞춘다. 예외 메시지는 쓰지 않는다 (개인 정보가 섞일 수 있음).</summary>
    internal static SignalReading SafeRead(ISignalSource source, AdapterInfo adapter)
    {
        try { return SignalReading.Normalize(source.Read(adapter)); }
        catch (Exception ex) { return new SignalReading(SignalState.Failed, null, null, ex.GetType().Name, null); }
    }

    /// <summary>큐에 쌓인 신호 읽기를 처리한다 (처리 작업, 또는 처리 작업이 멈춘 뒤 끝에서).</summary>
    private void DrainSignal()
    {
        while (signalQueue.Reader.TryRead(out var item)) ProcessSignal(item);
    }

    private void ProcessSignal(SignalItem item)
    {
        if (item.Ms > Volatile.Read(ref linkCutoffMs)) return;
        var ids = new List<string>();
        lock (linkGate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (adapter, reading) in item.Readings)
            {
                if (!seen.Add(adapter.Key)) continue;
                if (!signalTracks.TryGetValue(adapter.Key, out var t))
                    signalTracks[adapter.Key] = t = new SignalTrack { Alias = AliasFor(adapter), Info = adapter };
                if (ProcessReading(t, adapter, reading, item) is { } id) ids.Add(id);
            }
            // 이번 회차에 읽지 않은 어댑터 = 관측 종료 (경로가 다른 어댑터로 바뀜·어댑터가 사라짐)
            foreach (var t in signalTracks.Values.Where(t => t.Run is not null && !seen.Contains(t.Info.Key)).ToArray())
                ids.Add(EndTrack(t, item.Ms, item.Wall));
        }
        if (ids.Count == 0) return;
        RetryEvents();
        foreach (var id in ids) PublishEvent(id);
    }

    /// <summary>linkGate 안: 표본 하나. 바뀌었거나 끊겼으면 변경 이벤트, 같은 값이 60초 넘게 이어지면 체크포인트를 등록한다.</summary>
    private string? ProcessReading(SignalTrack t, AdapterInfo adapter, SignalReading r, SignalItem item)
    {
        bool reindexed = t.Info.InterfaceIndex != adapter.InterfaceIndex;
        t.Info = adapter;
        string? breakReason = null;
        if (t.Run is not null)
        {
            if (item.LostSeq != t.LostSeq) breakReason = "overflow";
            else if (item.PowerSeq != t.PowerSeq || item.Round - t.LastRound - 1 >= GapTracker.StallThreshold) breakReason = "gap";   // 기존 공백 규칙 (절전, 멈춤)
            else if (reindexed) breakReason = "reindexed";
        }
        t.LostSeq = item.LostSeq;
        t.PowerSeq = item.PowerSeq;
        t.LastRound = item.Round;
        t.Method = r.Method;
        t.Code = r.Code;
        t.Samples++;
        t.StateSamples = t.StateSamples.Add(r.State, 1);
        if (r.Quality is not null) t.QualitySamples++;
        if (r.RssiDbm is not null) t.RssiSamples++;

        bool same = t.Run is { } cur && cur.State == r.State && cur.Quality == r.Quality && cur.RssiDbm == r.RssiDbm;
        if (same && breakReason is null)
        {
            t.Run = t.Run! with { LastMs = item.Ms, Samples = t.Run.Samples + 1 };
            if (item.Ms - t.LastWrittenMs < SignalCheckpointMs) return null;
            t.LastWrittenMs = item.Ms;
            return RegisterEvent(RecordSchema.EventLine(Id, SignalEvidenceBuilder.EventType, item.Round, item.Ms, item.Wall, CheckpointFields(t, "periodic", null)), null);
        }

        var fields = SignalAdapterFields(t);
        fields["collection"] = "change";
        AddValue(fields, r.State, r.Quality, r.RssiDbm, r.Method, r.Code);
        fields["sampleMs"] = item.Ms;
        if (t.Run is { } prev)
        {
            fields["prevState"] = SignalText.Of(prev.State);
            if (prev.Quality is int pq) fields["prevQuality"] = pq;
            if (prev.RssiDbm is int pr) fields["prevRssi"] = pr;
            fields["prevFirstMs"] = prev.FirstMs;
            fields["prevLastMs"] = prev.LastMs;
            fields["prevSamples"] = prev.Samples;
            if (breakReason is not null) fields["break"] = breakReason;
            t.Closed.Add(prev);
            t.ClosedCache = null;
        }
        AddCounts(fields, t);
        t.Run = new SignalRun(r.State, r.Quality, r.RssiDbm, item.Ms, item.Ms, 1, t.Run is not null && breakReason is null);
        t.EndedMs = null;
        t.LastWrittenMs = item.Ms;
        return RegisterEvent(RecordSchema.EventLine(Id, SignalEvidenceBuilder.EventType, item.Round, item.Ms, item.Wall, fields), null);
    }

    /// <summary>linkGate 안: 어댑터 관측 종료. 마지막 체크포인트(reason "end")를 등록하고 묶음을 닫는다.</summary>
    private string EndTrack(SignalTrack t, long endMs, DateTimeOffset wall)
    {
        var fields = CheckpointFields(t, "end", endMs);
        t.Closed.Add(t.Run! with { EndMs = endMs });   // 다시 관측해도 이 묶음은 여기서 끝난다 (G4 M12 1라운드 2)
        t.ClosedCache = null;
        t.Run = null;
        t.EndedMs = endMs;
        t.LastWrittenMs = endMs;
        return RegisterEvent(RecordSchema.EventLine(Id, SignalEvidenceBuilder.EventType, null, endMs, wall, fields), null);
    }

    /// <summary>
    /// 끝: 관측 중인 어댑터마다 마지막 체크포인트, 그 다음 어댑터마다 요약 (M12 설계 C [1-4]). 처리 작업이 멈춘 뒤 끝 쓰기 작업에서 부른다.
    /// 요약의 관측 종료는 그 어댑터가 먼저 사라졌으면 그 시각, 아니면 측정 끝이다.
    /// 등록·게시·"요약을 썼음" 표시를 링크 요약처럼 한 경계(linkGate → sync) 안에서 한다.
    /// </summary>
    private void FinishSignal(long endMs, DateTimeOffset endWall)
    {
        if (options.Signal is null) return;
        DrainSignal();
        lock (linkGate)
        {
            var ids = new List<string>();
            var tracks = signalTracks.Values.OrderBy(t => t.Alias.Length).ThenBy(t => t.Alias, StringComparer.Ordinal).ToArray();
            foreach (var t in tracks.Where(t => t.Run is not null)) ids.Add(EndTrack(t, endMs, endWall));
            foreach (var t in tracks)
            {
                var stats = SignalStatistics.Compute(t.Closed, IntervalMs, t.EndedMs ?? endMs);
                var fields = SignalAdapterFields(t);
                fields["collection"] = "summary";
                SignalEvidenceBuilder.WriteStats(fields, stats);
                ids.Add(RegisterEvent(RecordSchema.EventLine(Id, SignalEvidenceBuilder.EventType, null, endMs, endWall, fields), null));
            }
            foreach (var id in ids) PublishEvent(id);
            signalSummaryWritten = true;
        }
        RetryEvents();
    }

    /// <summary>알림 해제와 같은 때: 이후 읽기는 하지 않고 공급자를 닫는다.</summary>
    private void CloseSignal()
    {
        signalClosed = true;
        try { options.Signal?.Dispose(); } catch (Exception) { }
    }

    private static JsonObject SignalAdapterFields(SignalTrack t) => new()
    {
        ["adapter"] = t.Alias, ["interfaceIndex"] = t.Info.InterfaceIndex, ["kind"] = t.Info.Kind,
    };

    private static void AddValue(JsonObject fields, SignalState state, int? quality, int? rssi, string? method, string? code)
    {
        fields["state"] = SignalText.Of(state);
        if (quality is int q) fields["quality"] = q;
        if (rssi is int r) fields["rssi"] = r;
        if (method is not null) fields["method"] = method;
        if (code is not null) fields["code"] = code;
    }

    /// <summary>누적 표본 수 (상태별·값별): 이벤트를 잃었는지 읽는 쪽이 대조할 수 있게.</summary>
    private static void AddCounts(JsonObject fields, SignalTrack t)
    {
        fields["samples"] = t.Samples;
        fields["stateSamples"] = t.StateSamples.ToJson();
        fields["qualitySamples"] = t.QualitySamples;
        fields["rssiSamples"] = t.RssiSamples;
    }

    private static JsonObject CheckpointFields(SignalTrack t, string reason, long? endMs)
    {
        var run = t.Run!;
        var fields = SignalAdapterFields(t);
        fields["collection"] = "checkpoint";
        fields["reason"] = reason;
        AddValue(fields, run.State, run.Quality, run.RssiDbm, t.Method, t.Code);
        fields["firstMs"] = run.FirstMs;
        fields["lastMs"] = run.LastMs;
        fields["runSamples"] = run.Samples;
        if (endMs is long e) fields["endMs"] = e;
        AddCounts(fields, t);
        return fields;
    }

    /// <summary>linkGate 안: 신호 관측의 지금 상태 (트랙을 바꾸지 않는다. 닫힌 묶음 사본은 바뀔 때만 다시 만든다).</summary>
    private SignalLive SnapshotSignal()
    {
        if (options.Signal is null) return SignalLive.None;
        var adapters = signalTracks.Values.OrderBy(t => t.Alias.Length).ThenBy(t => t.Alias, StringComparer.Ordinal)
            .Select(t => new SignalAdapterLive(t.Alias, t.Info.InterfaceIndex, t.Info.Kind, t.ClosedCache ??= t.Closed.ToArray(), t.Run, t.Method, t.Code, t.EndedMs))
            .ToArray();
        return new SignalLive(true, IntervalMs, adapters, signalSummaryWritten);
    }
}
