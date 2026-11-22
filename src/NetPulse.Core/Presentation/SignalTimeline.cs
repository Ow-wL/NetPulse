using System.Collections;
using System.Globalization;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;

namespace NetPulse.Core.Presentation;

/// <summary>신호 표시 방식 (M12 설계 C 표시). 신호가 관측되지 않은 측정은 <see cref="None"/>: 여백도 범례도 없이 지금과 같다.</summary>
public enum SignalDisplay { None, Line, State }

/// <summary>그리기 입력: 무선 어댑터 하나의 신호 묶음 (기록의 근거 또는 실시간 사본).</summary>
/// <param name="EndMs">그 어댑터의 관측 종료 (마지막 표본의 간격을 여기서 자른다). 관측 중이면 null</param>
public sealed record SignalTrack(string Alias, int InterfaceIndex, string? Kind, IReadOnlyList<SignalRun> Runs, long? EndMs);

/// <summary>
/// 그릴 묶음 하나 [<see cref="FromMs"/>, <see cref="ToMs"/>): 첫 표본부터 마지막 표본 + 간격 (다음 묶음 시작·관측 종료에서 자름).
/// <see cref="Joined"/>면 바로 앞 묶음(같은 어댑터)에서 끊김 없이 이어진다.
/// </summary>
public readonly record struct SignalTraceRun(string Alias, SignalState State, int? Quality, long FromMs, long ToMs, bool Joined);

/// <summary>
/// 측정 전체의 신호 표시 (M12 설계 C 표시 [1-7]). 보기 창과 무관해 측정·기록마다 한 번 만든다.
/// <list type="bullet">
/// <item>측정의 기본 경로 어댑터 선만 그린다: 표본이 많은 어댑터부터, 이미 고른 어댑터와 관측 시간이 겹치지 않으면 고른다.
/// 그래서 시간마다 선은 많아야 하나다. 경로가 다른 무선 어댑터로 옮기면(겹치지 않음) 선을 끊고 새 어댑터로 잇고 그 시각을 <see cref="AdapterChanges"/>에 둔다.</item>
/// <item>겹치는 다른 무선 어댑터는 그리지 않고 수(<see cref="OtherAdapters"/>)만 센다 (이름 없이).</item>
/// <item>품질 값이 하나라도 있으면 <see cref="SignalDisplay.Line"/>(오른쪽 축 여백), 없으면 <see cref="SignalDisplay.State"/>(범례 자리에 상태 글).</item>
/// </list>
/// </summary>
public sealed record SignalTrace(
    SignalDisplay Display, IReadOnlyList<SignalTraceRun> Runs, IReadOnlyList<long> AdapterChanges, IReadOnlyList<string> RouteAdapters,
    int OtherAdapters, SignalState? State, bool RssiOnly, long IntervalMs)
{
    public static readonly SignalTrace None = new(SignalDisplay.None, [], [], [], 0, null, false, 0);

    /// <summary>오른쪽 축 여백을 뺀다 (선을 그림).</summary>
    public bool Axis => Display == SignalDisplay.Line;

    /// <summary>범례 글 (선이 있을 때). 다른 무선 어댑터는 수만, 어댑터 바뀜은 위쪽 줄 표시를 알린다.</summary>
    public string? LegendText => Display != SignalDisplay.Line ? null
        : "Wi‑Fi 신호 품질 (오른쪽 축 0~100%)"
          + (AdapterChanges.Count > 0 ? " · 위쪽 줄 세로 막대는 무선 어댑터 바뀜" : "")
          + (OtherAdapters > 0 ? $" · 그 밖의 무선 어댑터 {OtherAdapters}개는 그리지 않음" : "");

    /// <summary>선 대신 범례 자리에 적는 상태 (품질 값이 없을 때).</summary>
    public string? StateText => Display != SignalDisplay.State ? null : SignalTimeline.StateText(State, RssiOnly);

    public static SignalTrace FromEvidence(SignalEvidence evidence, long intervalMs) =>
        SignalTimeline.Trace(evidence.Adapters.Select(a => new SignalTrack(a.Alias, a.InterfaceIndex, a.Kind, a.Runs, a.Stats.EndMs)), intervalMs);

    public static SignalTrace FromLive(SignalLive live) => !live.Configured ? None
        : SignalTimeline.Trace(live.Adapters.Select(a => new SignalTrack(a.Alias, a.InterfaceIndex, a.Kind,
            a.Current is { } c ? new RunsWithCurrent(a.Closed, c) : a.Closed, a.EndedMs)), live.IntervalMs);

    /// <summary>닫힌 묶음 + 지금 묶음을 복사하지 않고 잇는 보기 (실시간 게시마다 큰 배열을 만들지 않게).</summary>
    private sealed class RunsWithCurrent(IReadOnlyList<SignalRun> closed, SignalRun current) : IReadOnlyList<SignalRun>
    {
        public SignalRun this[int index] => index == closed.Count ? current : closed[index];
        public int Count => closed.Count + 1;
        public IEnumerator<SignalRun> GetEnumerator() { foreach (var r in closed) yield return r; yield return current; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>보기 창 하나의 신호 장면. 점의 값(<see cref="TimelinePoint.MinMs"/>·<see cref="TimelinePoint.MaxMs"/>)은 품질 %다.</summary>
/// <param name="Axis">오른쪽 축 여백을 뺀다 (측정 전체에 품질 값이 있음, 창마다 바뀌지 않음)</param>
/// <param name="MinQuality">창 안에서 관측한 가장 낮은 품질 (없으면 null)</param>
public sealed record SignalFrame(
    SignalDisplay Display, bool Axis, IReadOnlyList<IReadOnlyList<TimelinePoint>> Segments, IReadOnlyList<long> AdapterChanges,
    string? Legend, string? StateText, int OtherAdapters, int? MinQuality)
{
    public static readonly SignalFrame None = new(SignalDisplay.None, false, [], [], null, null, 0, null);
}

/// <summary>
/// 타임라인 그래프 영역 (M12 설계 C 표시): 신호 선이 있으면 오른쪽 축 글("100%"·"0%") 여백을 빼고, 시간축·선·띠·끝 시각을 모두 남은 같은 영역에 둔다.
/// 화면(<c>TimelinePanel</c>)과 리포트 그림이 같은 투영을 쓴다.
/// </summary>
public static class TimelineGeometry
{
    public const double QualityMax = 100;

    /// <summary>그래프 폭: 신호 축이 있으면 오른쪽 여백을 뺀다. 화면은 이 폭을 열 수로 쓴다.</summary>
    public static double PlotWidth(double width, bool signalAxis, double axisMargin) => Math.Max(0, signalAxis ? width - axisMargin : width);

    /// <summary>시각 → 그래프 영역 안의 x (0 ~ <paramref name="plotWidth"/>).</summary>
    public static double X(double ms, long fromMs, long toMs, double plotWidth) => (ms - fromMs) / Math.Max(1, toMs - fromMs) * plotWidth;

    /// <summary>품질 % → y (오른쪽 축: 100%가 위, 0%가 아래). 범위 밖은 끝에 붙인다.</summary>
    public static double SignalY(double quality, double top, double height) => top + height - Math.Clamp(quality, 0, QualityMax) / QualityMax * height;
}

/// <summary>신호 표시 계산 (순수 함수, 시험 대상).</summary>
public static class SignalTimeline
{
    public static SignalTrace Trace(IEnumerable<SignalTrack> tracks, long intervalMs)
    {
        var list = tracks.Where(t => t.Runs.Count > 0).ToList();
        if (list.Count == 0) return SignalTrace.None;

        // 기본 경로 어댑터: 표본이 많은 것부터, 고른 어댑터와 관측 시간이 겹치지 않으면 고른다
        var chosen = new List<SignalTrack>();
        foreach (var t in list.OrderByDescending(t => t.Runs.Sum(r => r.Samples)).ThenBy(t => t.Runs[0].FirstMs))
            if (!chosen.Any(c => Overlap(c.Runs, t.Runs))) chosen.Add(t);
        int others = list.Count - chosen.Count;

        var merged = chosen.SelectMany(t => t.Runs.Select(r => (Track: t, Run: r))).OrderBy(x => x.Run.FirstMs).ToArray();
        var runs = new SignalTraceRun[merged.Length];
        var changes = new List<long>();
        var route = new List<string>();
        long okQuality = 0, okRssi = 0;
        var notOk = new Dictionary<SignalState, long>();
        for (int i = 0; i < merged.Length; i++)
        {
            var (t, r) = merged[i];
            long to = r.LastMs + intervalMs;
            if (t.EndMs is long end) to = Math.Min(to, Math.Max(r.LastMs, end));
            if (r.EndMs is long own) to = Math.Min(to, Math.Max(r.LastMs, own));   // 그 묶음의 관측 종료 (같은 어댑터를 다시 관측해도, G4 M12 1라운드 2)
            if (i + 1 < merged.Length) to = Math.Min(to, merged[i + 1].Run.FirstMs);
            to = Math.Max(to, r.FirstMs);
            bool sameAsPrev = i > 0 && ReferenceEquals(merged[i - 1].Track, t);
            if (i > 0 && !sameAsPrev) changes.Add(r.FirstMs);
            if (!route.Contains(t.Alias)) route.Add(t.Alias);
            runs[i] = new SignalTraceRun(t.Alias, r.State, r.State == SignalState.Ok ? r.Quality : null, r.FirstMs, to, sameAsPrev && r.Continues);
            if (r.State == SignalState.Ok && r.Quality is not null) okQuality += r.Samples;
            else if (r.State == SignalState.Ok) okRssi += r.Samples;
            else notOk[r.State] = notOk.GetValueOrDefault(r.State) + r.Samples;
        }

        if (okQuality > 0)
            return new SignalTrace(SignalDisplay.Line, runs, changes, route, others, null, false, intervalMs);
        // 품질 값이 없다: 가장 많이 관측한 상태 하나를 범례 자리에 (RSSI만 읽힌 것도 상태로 본다)
        var top = notOk.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => (SignalState?)x.Key).FirstOrDefault();
        bool rssiOnly = okRssi > 0 && (top is null || okRssi >= notOk[top.Value]);
        return new SignalTrace(SignalDisplay.State, runs, changes, route, others, rssiOnly ? SignalState.Ok : top, rssiOnly, intervalMs);
    }

    /// <summary>두 어댑터의 관측 시간(묶음의 첫·마지막 표본)이 겹치는지. 묶음은 시간 순이다.</summary>
    internal static bool Overlap(IReadOnlyList<SignalRun> a, IReadOnlyList<SignalRun> b)
    {
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i].FirstMs <= b[j].LastMs && b[j].FirstMs <= a[i].LastMs) return true;
            if (a[i].LastMs < b[j].LastMs) i++; else j++;
        }
        return false;
    }

    /// <summary>범례 자리 상태 글 (품질 값이 없을 때). 원인을 단정하지 않고 읽은 결과만.</summary>
    public static string StateText(SignalState? state, bool rssiOnly) => rssiOnly
        ? "Wi‑Fi 신호: 품질 값 없이 RSSI만 읽혀 선을 그리지 않았습니다"
        : state switch
        {
            SignalState.Denied => "Wi‑Fi 신호: 읽을 권한이 없어 기록하지 못했습니다 (측정은 그대로 했습니다)",
            SignalState.Unsupported => "Wi‑Fi 신호: 이 어댑터나 드라이버에서는 읽을 수 없습니다",
            SignalState.NotConnected => "Wi‑Fi 신호: 연결되지 않은 상태만 관측되어 값이 없습니다",
            _ => "Wi‑Fi 신호: 읽지 못했습니다",
        };

    /// <summary>보기 창 하나의 신호 장면. <paramref name="columns"/>는 그래프 폭(여백을 뺀 픽셀). 점은 열 수의 두 배를 넘지 않는다.</summary>
    public static SignalFrame Frame(SignalTrace trace, long fromMs, long toMs, int columns)
    {
        if (trace.Display == SignalDisplay.None) return SignalFrame.None;
        if (toMs <= fromMs) toMs = fromMs + 1;
        columns = Math.Max(1, columns);
        var segments = trace.Display == SignalDisplay.Line ? Segments(trace.Runs, fromMs, toMs, columns) : [];
        int? min = null;
        foreach (var r in trace.Runs)
            if (r.Quality is int q && r.ToMs > fromMs && r.FromMs < toMs) min = min is int m ? Math.Min(m, q) : q;
        var changes = trace.AdapterChanges.Where(c => c >= fromMs && c <= toMs).ToArray();
        return new SignalFrame(trace.Display, trace.Axis, segments, changes, trace.LegendText, trace.StateText, trace.OtherAdapters, min);
    }

    /// <summary>화면 읽기 요약에 붙이는 말 (신호가 관측된 측정만). 관측만 적는다.</summary>
    public static string Describe(SignalFrame f)
    {
        if (f.Display == SignalDisplay.None) return "";
        if (f.Display == SignalDisplay.State) return ", " + f.StateText;
        string text = f.MinQuality is int q ? $", Wi‑Fi 신호 품질 최저 {q.ToString(CultureInfo.InvariantCulture)}%" : ", 이 범위에 Wi‑Fi 신호 값 없음";
        if (f.AdapterChanges.Count > 0) text += $", 무선 어댑터 바뀜 {f.AdapterChanges.Count}번";
        return text;
    }

    /// <summary>
    /// 품질 선 구간 (지연 선과 같은 간추리기 규칙). 묶음이 열 수 이하면 묶음마다 가로선(계단), 넘으면 열마다 최솟값·최댓값.
    /// 정상이 아닌 상태·품질 없음·끊긴 묶음·어댑터 바뀜을 건너 잇지 않는다. x는 창 안으로 자른다.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<TimelinePoint>> Segments(IReadOnlyList<SignalTraceRun> runs, long fromMs, long toMs, int columns)
    {
        var segments = new List<IReadOnlyList<TimelinePoint>>();
        var current = new List<TimelinePoint>();
        void Break()
        {
            if (current.Count > 0) segments.Add(current.ToArray());
            current.Clear();
        }
        int lo = Lower(runs, r => r.ToMs > fromMs), hi = Lower(runs, r => r.FromMs >= toMs);
        if (hi <= lo) return segments;
        if (hi - lo <= columns)
        {
            for (int i = lo; i < hi; i++)
            {
                var r = runs[i];
                if (r.Quality is not int q) { Break(); continue; }
                bool joined = i > lo && r.Joined && runs[i - 1].Quality is not null;
                if (!joined) Break();
                double a = Math.Max(r.FromMs, fromMs), b = Math.Max(a, Math.Min(r.ToMs, toMs));
                current.Add(new TimelinePoint(a, q, q));
                current.Add(new TimelinePoint(b, q, q));
            }
            Break();
            return segments;
        }

        double width = (double)(toMs - fromMs) / columns;
        int k = lo, prevLast = -1;
        bool prevClean = false;
        for (int c = 0; c < columns && k < hi; c++)
        {
            double colStart = fromMs + c * width, colEnd = colStart + width;
            while (k < hi && runs[k].ToMs <= colStart) k++;
            if (k >= hi) break;
            if (runs[k].FromMs >= colEnd) { Break(); prevClean = false; prevLast = -1; continue; }
            int j = k, valid = 0;
            double min = double.MaxValue, max = double.MinValue;
            bool clean = true;
            while (j < hi && runs[j].FromMs < colEnd)
            {
                if (runs[j].Quality is int q) { valid++; min = Math.Min(min, q); max = Math.Max(max, q); }
                else clean = false;
                if (j > k && !runs[j].Joined) clean = false;
                j++;
            }
            bool connected = prevLast >= 0 && prevClean && clean && (k == prevLast || (k == prevLast + 1 && runs[k].Joined));
            if (!connected) Break();
            if (valid > 0) current.Add(new TimelinePoint(colStart + width / 2, min, max));
            if (!clean) Break();
            prevLast = j - 1;
            prevClean = clean;
        }
        Break();
        return segments;

        // 조건이 처음 참이 되는 위치 (묶음은 시간 순이고 겹치지 않아 시작·끝이 모두 줄지 않는다)
        static int Lower(IReadOnlyList<SignalTraceRun> list, Func<SignalTraceRun, bool> pass)
        {
            int a = 0, b = list.Count;
            while (a < b)
            {
                int m = (a + b) / 2;
                if (pass(list[m])) b = m; else a = m + 1;
            }
            return a;
        }
    }
}
