using System.Text.Json.Nodes;

namespace NetPulse.Core.Evidence;

/// <summary>
/// Wi‑Fi 신호 읽기 상태 (M12 설계 C). 정상(Ok)이 아닌 상태는 값이 없고 선을 끊는다 [1-4]:
/// 연결 안 됨, 권한 없음, 지원 안 함, 읽기 실패. 링크 근거와 같은 원칙으로 "값 없음"을 정상으로 보지 않는다.
/// </summary>
public enum SignalState { Ok, NotConnected, Denied, Unsupported, Failed }

/// <summary>
/// 어댑터 하나를 한 번 읽은 결과 (M12 설계 C [1-4] 표본). 품질 0은 값이다(없음 null과 다르다).
/// SSID·BSSID는 담지 않는다 (개인 정보, 설계 C 읽기).
/// </summary>
/// <param name="Quality">링크 품질 0~100 (없으면 null)</param>
/// <param name="RssiDbm">RSSI dBm (없으면 null)</param>
/// <param name="Code">정상이 아닐 때 사유 코드 (예: "access-denied", "wlan_1234", 예외 형식 이름)</param>
/// <param name="Method">읽은 방법: "realtime"(실시간 연결 품질) 또는 "rssi"(RSSI만). 읽지 못했으면 null</param>
public sealed record SignalReading(SignalState State, int? Quality, int? RssiDbm, string? Code, string? Method)
{
    public const int MinRssi = -127, MaxRssi = 0;

    /// <summary>
    /// 기록 계약에 맞춘다: 정상이 아니면 값을 버리고(끊긴 구간은 평균에 넣지 않음 [1-4]), 범위 밖 값은 없음으로 본다.
    /// 공급자가 null을 돌려주면 읽기 실패다.
    /// </summary>
    public static SignalReading Normalize(SignalReading? r)
    {
        if (r is null) return new SignalReading(SignalState.Failed, null, null, "no-reading", null);
        if (r.State != SignalState.Ok) return r with { Quality = null, RssiDbm = null };
        int? q = r.Quality is int qv && qv is >= 0 and <= 100 ? qv : null;
        int? s = r.RssiDbm is int sv && sv is >= MinRssi and <= MaxRssi ? sv : null;
        return r with { Quality = q, RssiDbm = s };
    }
}

/// <summary>
/// Wi‑Fi 신호 읽기 (M12 설계 C 읽기). 실제 구현은 Platform(wlanapi, 위치 권한이 필요 없는 호출만).
/// 엔진 회차에서 무선 어댑터마다 부른다: 빨라야 하고, 예외를 던져도 세션이 읽기 실패로 기록하고 측정은 계속한다.
/// </summary>
public interface ISignalSource : IDisposable
{
    SignalReading Read(AdapterInfo adapter);
}

/// <summary>
/// 같은 (상태, 품질, RSSI)가 이어진 표본 묶음 (M12 설계 C [1-4]·[2-3] 계산 단위).
/// 표본 하나는 그 시각부터 측정 간격 하나를 대표한다.
/// </summary>
/// <param name="FirstMs">첫 표본 경과 ms</param>
/// <param name="LastMs">마지막 표본 경과 ms (반복 표본은 줄로 쓰지 않으므로 실시간 사본·체크포인트·다음 변경 이벤트로만 안다)</param>
/// <param name="Samples">표본 수</param>
/// <param name="Continues">바로 앞 묶음에서 끊김 없이 이어짐 (공백·넘침·번호 바뀜·관측 종료가 사이에 없음). 그리기가 선을 이을지 정한다</param>
/// <param name="EndMs">이 묶음을 끝낸 관측 종료 시각 (어댑터가 경로에서 빠짐·측정 끝). 마지막 표본의 간격을 여기서 자른다.
/// 같은 어댑터를 나중에 다시 관측해도 앞 묶음의 경계가 남는다 (G4 M12 1라운드 2). 모르면 null</param>
public sealed record SignalRun(SignalState State, int? Quality, int? RssiDbm, long FirstMs, long LastMs, long Samples, bool Continues = false, long? EndMs = null);

/// <summary>상태별 수 (표본 수 또는 ms). 값 비교가 되도록 필드로 둔다 (요약과 재계산을 같다고 비교, [2-3]).</summary>
public readonly record struct SignalStateCounts(long Ok, long NotConnected, long Denied, long Unsupported, long Failed)
{
    public long this[SignalState s] => s switch
    {
        SignalState.Ok => Ok, SignalState.NotConnected => NotConnected, SignalState.Denied => Denied, SignalState.Unsupported => Unsupported, _ => Failed,
    };

    public long Total => Ok + NotConnected + Denied + Unsupported + Failed;

    public SignalStateCounts Add(SignalState s, long n) => s switch
    {
        SignalState.Ok => this with { Ok = Ok + n },
        SignalState.NotConnected => this with { NotConnected = NotConnected + n },
        SignalState.Denied => this with { Denied = Denied + n },
        SignalState.Unsupported => this with { Unsupported = Unsupported + n },
        _ => this with { Failed = Failed + n },
    };

    /// <summary>기록 필드: 0이 아닌 상태만 { "ok": n, ... }.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var s in Enum.GetValues<SignalState>())
            if (this[s] != 0) o[SignalText.Of(s)] = this[s];
        return o;
    }

    public static SignalStateCounts FromJson(JsonNode? node)
    {
        var c = new SignalStateCounts();
        if (node is not JsonObject o) return c;
        foreach (var (k, v) in o)
            if (SignalText.Parse(k) is { } s && Json.Long(v) is long n) c = c.Add(s, n);
        return c;
    }
}

/// <summary>
/// 어댑터 하나의 신호 요약 (M12 설계 C [2-3] 끝 요약). 평균은 시간 가중, 품질·RSSI는 각각 그 값이 있는 표본의 시간만 분모에 넣는다.
/// </summary>
/// <param name="EndMs">계산한 관측 종료 시각 (마지막 표본의 시간을 여기서 자른다)</param>
/// <param name="StateMs">상태별 유효 시간 ms</param>
/// <param name="QualityAvg">품질 시간 가중 평균 (소수 둘째 자리, 유효 시간 0이면 null)</param>
public sealed record SignalStats(
    long EndMs, long Samples, SignalStateCounts StateSamples, SignalStateCounts StateMs, long QualitySamples, long RssiSamples,
    double? QualityAvg, int? QualityMin, double? RssiAvg, int? RssiMin);

/// <summary>
/// 신호 통계 (M12 설계 C [2-3]). 세션의 끝 요약, 이벤트 재계산, (나중에) 리포트가 모두 이 함수 하나를 쓴다.
/// <list type="bullet">
/// <item>표본 하나는 그 표본 시각부터 측정 간격 하나를 대표한다. 마지막 표본도 간격 하나를 갖되 관측 종료 시각을 넘지 않게 자른다.</item>
/// <item>그래서 간격이 고르면 표본 평균과 같다 (1초 간격 t=0~98 80%, t=99 20% → 79.4%).</item>
/// <item>표본이 하나면 그 값이 평균이다. 유효 시간이 0이면(끝에서 잘려 0) 평균은 없음이다. 최저는 시간과 상관없이 관측한 값에서 고른다.</item>
/// <item>관측 종료 뒤에 시작한 묶음은 넣지 않는다.</item>
/// </list>
/// </summary>
public static class SignalStatistics
{
    /// <summary>
    /// 묶음 하나의 유효 시간 ms: (표본 수 − 1) × 간격 + min(간격, 끝 − 마지막 표본), 단 묶음 시작부터 끝까지를 넘지 않는다.
    /// 끝은 전체 관측 종료와 그 묶음의 관측 종료(<see cref="SignalRun.EndMs"/>) 중 이른 것이다.
    /// </summary>
    public static long Weight(SignalRun run, long intervalMs, long endMs)
    {
        if (run.Samples <= 0 || run.FirstMs > endMs) return 0;
        if (run.EndMs is long own) endMs = Math.Min(endMs, Math.Max(own, run.LastMs));
        long w = (run.Samples - 1) * intervalMs + Math.Clamp(endMs - run.LastMs, 0, intervalMs);
        return Math.Min(w, Math.Max(0, endMs - run.FirstMs));
    }

    public static SignalStats Compute(IEnumerable<SignalRun> runs, long intervalMs, long endMs)
    {
        long samples = 0, qSamples = 0, rSamples = 0, qTime = 0, rTime = 0;
        double qSum = 0, rSum = 0;
        int? qMin = null, rMin = null;
        var stateSamples = new SignalStateCounts();
        var stateMs = new SignalStateCounts();
        foreach (var run in runs)
        {
            if (run.Samples <= 0 || run.FirstMs > endMs) continue;
            long w = Weight(run, intervalMs, endMs);
            samples += run.Samples;
            stateSamples = stateSamples.Add(run.State, run.Samples);
            stateMs = stateMs.Add(run.State, w);
            if (run.Quality is int q)
            {
                qSamples += run.Samples;
                qTime += w;
                qSum += (double)q * w;
                qMin = qMin is int m ? Math.Min(m, q) : q;
            }
            if (run.RssiDbm is int r)
            {
                rSamples += run.Samples;
                rTime += w;
                rSum += (double)r * w;
                rMin = rMin is int m ? Math.Min(m, r) : r;
            }
        }
        return new SignalStats(endMs, samples, stateSamples, stateMs, qSamples, rSamples,
            qTime > 0 ? Round(qSum / qTime) : null, qMin, rTime > 0 ? Round(rSum / rTime) : null, rMin);
    }

    private static double Round(double x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);
}

/// <summary>기록 글자 (M12 설계 C 기록 형식).</summary>
public static class SignalText
{
    public static string Of(SignalState s) => s switch
    {
        SignalState.Ok => "ok", SignalState.NotConnected => "notConnected", SignalState.Denied => "denied", SignalState.Unsupported => "unsupported", _ => "failed",
    };

    public static SignalState? Parse(string? s) => s switch
    {
        "ok" => SignalState.Ok, "notConnected" => SignalState.NotConnected, "denied" => SignalState.Denied, "unsupported" => SignalState.Unsupported,
        "failed" => SignalState.Failed, _ => null,
    };
}

/// <summary>
/// 기록에서 복원한 어댑터 하나의 신호 근거 (M12 설계 C [1-4]·[2-3]).
/// </summary>
/// <param name="Runs">그리기용 묶음 (시간 순)</param>
/// <param name="Stats">요약이 있으면 요약, 없으면 재계산 (읽을 때 쓰는 값)</param>
/// <param name="Recomputed">이벤트만으로 다시 계산한 값. 요약이 있으면 요약과 같은 관측 종료 시각에서 계산한다 (시험이 둘을 비교)</param>
/// <param name="Summary">끝 요약 (없으면 null)</param>
/// <param name="LastObserved">끝 요약이 없다(비정상 종료): 마지막으로 저장된 체크포인트·변경 이벤트까지만 복원했다. 화면·리포트는 "마지막 관측"으로 표시한다</param>
public sealed record SignalAdapterEvidence(string Alias, int InterfaceIndex, string? Kind, IReadOnlyList<SignalRun> Runs,
    SignalStats Stats, SignalStats Recomputed, SignalStats? Summary, bool LastObserved);

/// <summary>기록 하나의 신호 근거. 신호 이벤트가 없으면 <see cref="None"/> ("신호 자료 없음").</summary>
public sealed record SignalEvidence(IReadOnlyList<SignalAdapterEvidence> Adapters)
{
    public static readonly SignalEvidence None = new([]);

    public bool HasData => Adapters.Count > 0;

    public SignalAdapterEvidence? For(string alias) => Adapters.FirstOrDefault(a => a.Alias == alias);
}

/// <summary>
/// 기록의 signal 이벤트 → 신호 근거 (M12 설계 C [1-4]·[2-2]·[2-3]). 쓰는 형식 (세션이 지킴, 모든 줄에 adapter·interfaceIndex·kind):
/// <list type="bullet">
/// <item>collection "change": 값이나 상태가 바뀜(또는 공백·넘침·번호 바뀜으로 끊김, break). state, quality?, rssi?, method?, code?, sampleMs(새 묶음 첫 표본),
/// 앞 묶음이 있으면 prevState·prevQuality?·prevRssi?·prevFirstMs·prevLastMs·prevSamples. 누적 samples·stateSamples·qualitySamples·rssiSamples.</item>
/// <item>collection "checkpoint": reason "periodic"(60초마다) 또는 "end"(어댑터 관측 종료, endMs). 지금 묶음의 값과 firstMs·lastMs·runSamples, 누적 수.</item>
/// <item>collection "summary": 측정 끝. endMs(그 어댑터의 관측 종료), samples, stateSamples, stateMs, qualitySamples, rssiSamples, qualityAvg?·qualityMin?·rssiAvg?·rssiMin?.</item>
/// </list>
/// 변경 이벤트의 prev* 필드로 앞 묶음을 스스로 닫으므로 앞 이벤트를 잃어도 묶음이 섞이지 않는다.
/// 모르는 collection·필드는 무시한다 (나중 판 호환).
/// </summary>
public static class SignalEvidenceBuilder
{
    public const string EventType = "signal";

    private sealed class Track
    {
        public required string Alias;
        public int Index;
        public string? Kind;
        public readonly List<SignalRun> Closed = [];
        public SignalRun? Open;
        public long? EndedMs;
        public SignalStats? Summary;
    }

    public static SignalEvidence FromEvents(IReadOnlyList<JsonObject> events, long intervalMs)
    {
        var tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        var order = new List<Track>();
        foreach (var e in events)
        {
            if (Str(e, "type") != EventType || Str(e, "adapter") is not { } alias) continue;
            if (!tracks.TryGetValue(alias, out var t))
            {
                tracks[alias] = t = new Track { Alias = alias };
                order.Add(t);
            }
            if (Int(e, "interfaceIndex") is int idx) t.Index = idx;
            if (Str(e, "kind") is { } kind) t.Kind = kind;
            switch (Str(e, "collection"))
            {
                case "change":
                    if (SignalText.Parse(Str(e, "state")) is not { } state || Long(e, "sampleMs") is not long first) break;
                    bool hasPrev = Long(e, "prevLastMs") is not null;
                    if (hasPrev) ClosePrevious(t, e);
                    else if (t.Open is { } dangling) { t.Closed.Add(dangling); t.Open = null; }
                    t.Open = new SignalRun(state, Int(e, "quality"), Int(e, "rssi"), first, first, 1, hasPrev && Str(e, "break") is null);
                    t.EndedMs = null;
                    break;
                case "checkpoint":
                    if (SignalText.Parse(Str(e, "state")) is not { } cs || Long(e, "firstMs") is not long f || Long(e, "lastMs") is not long l
                        || Long(e, "runSamples") is not long n) break;
                    var q = Int(e, "quality");
                    var r = Int(e, "rssi");
                    // 지금 묶음을 갱신한다. 그 묶음의 변경 이벤트를 잃었으면 체크포인트로 새로 연다 (이어짐은 모름 → 끊김).
                    // 열려 있던 다른 묶음은 버리지 않고 아는 데까지 닫는다 (G4 M12 3라운드 1: 0~60초 80% 뒤 70초 변경을 잃고 130초 체크포인트만 남아도 80% 묶음이 남게)
                    if (t.Open is { } open && open.FirstMs == f && open.State == cs && open.Quality == q && open.RssiDbm == r)
                        t.Open = open with { LastMs = l, Samples = n };
                    else
                    {
                        if (t.Open is { } other) t.Closed.Add(other);
                        t.Open = new SignalRun(cs, q, r, f, l, n);
                    }
                    if (Str(e, "reason") == "end")
                    {
                        t.EndedMs = Long(e, "endMs") ?? Long(e, "at");
                        t.Closed.Add(t.Open with { EndMs = t.EndedMs });
                        t.Open = null;
                    }
                    break;
                case "summary":
                    t.Summary = ParseSummary(e);
                    break;
            }
        }
        if (order.Count == 0) return SignalEvidence.None;

        var adapters = new List<SignalAdapterEvidence>();
        foreach (var t in order)
        {
            var runs = t.Closed.ToList();
            if (t.Open is { } open) runs.Add(open);
            runs.Sort((a, b) => a.FirstMs.CompareTo(b.FirstMs));
            long end;
            if (t.Summary is { } s) end = s.EndMs;
            else if (t.Open is null && t.EndedMs is long ended) end = ended;
            else end = runs.Count == 0 ? 0 : runs.Max(x => x.LastMs) + intervalMs;   // 비정상 종료: 마지막 저장 표본도 간격 하나를 갖는다
            var recomputed = SignalStatistics.Compute(runs, intervalMs, end);
            adapters.Add(new SignalAdapterEvidence(t.Alias, t.Index, t.Kind, runs, t.Summary ?? recomputed, recomputed, t.Summary, t.Summary is null));
        }
        return new SignalEvidence(adapters);
    }

    /// <summary>
    /// 변경 이벤트의 prev* 필드로 앞 묶음을 닫는다. 열린 묶음이 바로 그 앞 묶음(시작·상태·값이 같음)일 때만 이어 붙인다.
    /// 다르면 사이의 변경 이벤트를 잃은 것이다: 열린 묶음은 아는 데까지만 닫고 앞 묶음은 prev 값으로 따로 만든다
    /// (0초 80% → 1초 20% → 2초 90%에서 1초 이벤트를 잃어도 80% 묶음을 20% 구간까지 늘리지 않는다, G4 M12 1라운드 1).
    /// </summary>
    private static void ClosePrevious(Track t, JsonObject e)
    {
        long last = Long(e, "prevLastMs")!.Value;
        var ps = SignalText.Parse(Str(e, "prevState"));
        long? pf = Long(e, "prevFirstMs"), pn = Long(e, "prevSamples");
        int? pq = Int(e, "prevQuality"), pr = Int(e, "prevRssi");
        if (t.Open is { } open)
        {
            t.Open = null;
            bool same = pf is null || (open.FirstMs == pf && (ps is null || open.State == ps) && open.Quality == pq && open.RssiDbm == pr);
            if (same)
            {
                t.Closed.Add(open with { LastMs = last, Samples = pn ?? open.Samples });
                return;
            }
            t.Closed.Add(open);
        }
        if (ps is { } state && pf is long first && pn is long n)
            t.Closed.Add(new SignalRun(state, pq, pr, first, last, n));
    }

    private static SignalStats? ParseSummary(JsonObject e)
    {
        if (Long(e, "endMs") is not long end || Long(e, "samples") is not long samples) return null;
        return new SignalStats(end, samples, SignalStateCounts.FromJson(e["stateSamples"]), SignalStateCounts.FromJson(e["stateMs"]),
            Long(e, "qualitySamples") ?? 0, Long(e, "rssiSamples") ?? 0, Json.Double(e["qualityAvg"]), Int(e, "qualityMin"), Json.Double(e["rssiAvg"]), Int(e, "rssiMin"));
    }

    /// <summary>끝 요약 줄의 통계 필드 (세션이 쓰고 이 빌더가 읽는다).</summary>
    public static void WriteStats(JsonObject fields, SignalStats s)
    {
        fields["endMs"] = s.EndMs;
        fields["samples"] = s.Samples;
        fields["stateSamples"] = s.StateSamples.ToJson();
        fields["stateMs"] = s.StateMs.ToJson();
        fields["qualitySamples"] = s.QualitySamples;
        fields["rssiSamples"] = s.RssiSamples;
        if (s.QualityAvg is double qa) fields["qualityAvg"] = qa;
        if (s.QualityMin is int qm) fields["qualityMin"] = qm;
        if (s.RssiAvg is double ra) fields["rssiAvg"] = ra;
        if (s.RssiMin is int rm) fields["rssiMin"] = rm;
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static long? Long(JsonObject o, string name) => Json.Long(o[name]);
    private static int? Int(JsonObject o, string name) => Json.Long(o[name]) is long x && x is >= int.MinValue and <= int.MaxValue ? (int)x : null;
}
