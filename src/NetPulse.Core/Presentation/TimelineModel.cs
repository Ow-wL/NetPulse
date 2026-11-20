using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Presentation;

/// <summary>타임라인 띠 종류 (M5 설계 3.5). 띠는 판정이 아니라 관측이다.</summary>
public enum BandKind
{
    /// <summary>사용 중인 외부 대상이 모두 응답 없음 (끊김 색)</summary>
    ExternalNone,
    /// <summary>일부 대상만 응답 없음 (주의 색, 위쪽 얇은 줄)</summary>
    PartialNone,
    /// <summary>관측 공백 (절전·멈춤)</summary>
    Gap,
}

/// <summary>
/// 띠 하나. <see cref="FromMs"/>~<see cref="ToMs"/>는 응답 없음이 관측된 <b>회차 칸</b>(예정 시각 ~ 다음 예정 시각)이지 실제 끊김의 시작·끝이 아니다.
/// 길이는 <see cref="MinMs"/>(첫·마지막 손실 표본 사이)~<see cref="MaxMs"/>(앞뒤 성공 표본 사이) 범위로만 말한다.
/// </summary>
public sealed record TimelineBand(BandKind Kind, long FromMs, long ToMs, long FirstRound, long LastRound, int Rounds, long MinMs, long? MaxMs, bool OpenEnd, bool OpenStart);

/// <summary>간추린 점: 그 칸의 최솟값·최댓값 (급증이 사라지지 않게).</summary>
public readonly record struct TimelinePoint(double XMs, double MinMs, double MaxMs);

/// <summary>링크 다운 표시 (직접 관측 또는 기록). 진행 중이면 마지막 표본까지만.</summary>
public sealed record LinkDownView(string Adapter, LinkSource Source, long FromMs, long ToMs, bool Ongoing);

/// <summary>한 장면 (보기 창 하나).</summary>
public sealed record TimelineFrame(
    long FromMs, long ToMs, double AxisMaxMs,
    IReadOnlyList<IReadOnlyList<TimelinePoint>> Router, IReadOnlyList<IReadOnlyList<TimelinePoint>> Internet,
    IReadOnlyList<TimelineBand> Bands, IReadOnlyList<SymptomMark> Marks, IReadOnlyList<LinkDownView> LinkDowns,
    string Summary);

/// <summary>
/// 겹침 타임라인의 값 (M5 설계 3.5, N1). 순수 계산: 화면은 이것을 그리기만 한다.
/// <list type="bullet">
/// <item>공유기 지연 = 순서상 첫 로컬 대상, 인터넷 지연 = 그 회차에 응답한 외부 대상들의 중앙값.</item>
/// <item>값 없는 회차·누락 회차·관측 공백을 건너 선을 잇지 않는다 (기획서 "이어받을 판단 규칙").</item>
/// <item>검사 오류만 있는 회차는 띠를 그리지 않고 값 없음 (손실과 구별).</item>
/// </list>
/// </summary>
public static class TimelineModel
{
    public const double MinAxisMs = 40;
    public const double AxisStepMs = 20;

    /// <summary>회차 하나의 대표 값 (화면 카드·칩도 같은 함수를 쓴다).</summary>
    public static (double? Router, double? Internet) Representative(RoundData r, IReadOnlyList<Target> targets)
    {
        var (local, externals) = Roles(targets);
        return Representative(r, local, externals, []);
    }

    internal static (string? Local, HashSet<string> Externals) Roles(IReadOnlyList<Target> targets) =>
        (targets.FirstOrDefault(t => t.Role == TargetRole.Local)?.Id, targets.Where(t => t.Role == TargetRole.External).Select(t => t.Id).ToHashSet());

    /// <summary>역할 조회를 미리 한 대표값 (보기 창의 회차마다 부르므로 할당을 줄인다). <paramref name="buffer"/>는 비워서 다시 쓴다.</summary>
    internal static (double? Router, double? Internet) Representative(RoundData r, string? local, HashSet<string> externals, List<double> buffer)
    {
        double? router = null;
        if (local is not null)
            foreach (var x in r.Results)
                if (x.TargetId == local) { router = x is { Outcome: ProbeOutcome.Success, RttMs: double ms } ? ms : null; break; }
        buffer.Clear();
        foreach (var x in r.Results)
            if (externals.Contains(x.TargetId) && x.Outcome == ProbeOutcome.Success && x.RttMs is double v) buffer.Add(v);
        buffer.Sort();
        return (router, Median(buffer));
    }

    public static double? Median(IReadOnlyList<double> sorted) => sorted.Count switch
    {
        0 => null,
        _ when sorted.Count % 2 == 1 => sorted[sorted.Count / 2],
        _ => (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2,
    };

    /// <summary>
    /// 응답 없음 띠와 공백 띠 (측정 전체). 띠는 연속 회차 번호로만 묶는다 (누락 회차를 건너 잇지 않음).
    /// </summary>
    /// <remarks>마지막 관측 회차까지 이어진 띠는 <see cref="TimelineBand.OpenEnd"/>: 측정 중이면 "이어지는 중", 끝났으면 "끝까지 이어짐".</remarks>
    public static IReadOnlyList<TimelineBand> Bands(IReadOnlyList<RoundData> rounds, IReadOnlyList<Target> targets, IReadOnlyList<GapSpan> gaps, long intervalMs)
    {
        var externals = targets.Where(t => t.Role == TargetRole.External).Select(t => t.Id).ToArray();
        var all = targets.Select(t => t.Id).ToArray();
        var ordered = InRoundOrder(rounds) ? rounds : rounds.OrderBy(r => r.Round).ToArray();
        var bands = new List<TimelineBand>();

        // 회차마다 대상 결과를 훑는다 (대상은 몇 개뿐이고, 기록 읽기가 같은 대상의 결과 둘을 거부한다).
        // 회차마다 사전을 만들던 것이 12시간 기록에서 리포트 문서 할당의 대부분이었다 (M8 게이트 bench, 2세대 수집)
        BandKind? Classify(RoundData r)
        {
            if (r.Gap) return null;
            if (externals.Length > 0 && Every(r, externals, ProbeOutcome.Lost)) return BandKind.ExternalNone;
            if (AnyOf(r, all, ProbeOutcome.Lost)) return BandKind.PartialNone;
            return null;
        }
        bool Success(RoundData r, BandKind kind) =>
            // 띠를 끝내는 "성공": 외부 띠면 외부 하나라도 응답, 일부 띠면 모든 대상 응답
            kind == BandKind.ExternalNone ? AnyOf(r, externals, ProbeOutcome.Success) : Every(r, all, ProbeOutcome.Success);

        int i = 0;
        while (i < ordered.Count)
        {
            var kind = Classify(ordered[i]);
            if (kind is null) { i++; continue; }
            int j = i;
            while (j + 1 < ordered.Count && ordered[j + 1].Round == ordered[j].Round + 1 && Classify(ordered[j + 1]) == kind) j++;
            var first = ordered[i];
            var last = ordered[j];
            long minMs = last.AtMs - first.AtMs;
            // 앞뒤 성공 표본 (바로 이웃한 회차가 성공일 때만. 누락·공백 너머의 표본은 쓰지 않는다)
            RoundData? before = i > 0 && ordered[i - 1].Round == first.Round - 1 && !ordered[i - 1].Gap && Success(ordered[i - 1], kind.Value) ? ordered[i - 1] : null;
            RoundData? after = j + 1 < ordered.Count && ordered[j + 1].Round == last.Round + 1 && !ordered[j + 1].Gap && Success(ordered[j + 1], kind.Value) ? ordered[j + 1] : null;
            long? maxMs = before is not null && after is not null ? after.AtMs - before.AtMs : null;
            bands.Add(new TimelineBand(kind.Value, first.DueMs, last.DueMs + intervalMs, first.Round, last.Round, j - i + 1, minMs, maxMs,
                OpenEnd: j == ordered.Count - 1, OpenStart: i == 0 && first.Round == 0 && before is null));
            i = j + 1;
        }
        foreach (var g in gaps)
            bands.Add(new TimelineBand(BandKind.Gap, g.FromMs, g.ToMs, g.LastRoundBefore ?? -1, g.FirstRoundAfter ?? -1, 0, g.ToMs - g.FromMs, g.ToMs - g.FromMs, false, false));
        return bands.OrderBy(b => b.FromMs).ToArray();
    }

    /// <summary>회차 번호가 줄지 않는 순서인지. 그렇다면 안정 정렬해도 같은 순서라 복사하지 않는다.</summary>
    internal static bool InRoundOrder(IReadOnlyList<RoundData> rounds)
    {
        for (int k = 1; k < rounds.Count; k++) if (rounds[k].Round < rounds[k - 1].Round) return false;
        return true;
    }

    private static TargetResult? ResultOf(RoundData r, string targetId)
    {
        var results = r.Results;
        for (int k = 0; k < results.Count; k++) if (results[k].TargetId == targetId) return results[k];
        return null;
    }

    /// <summary>모든 대상의 결과가 있고 그 결과가 <paramref name="outcome"/>인지.</summary>
    private static bool Every(RoundData r, string[] ids, ProbeOutcome outcome)
    {
        foreach (var id in ids) if (ResultOf(r, id) is not { } x || x.Outcome != outcome) return false;
        return true;
    }

    /// <summary>결과가 <paramref name="outcome"/>인 대상이 하나라도 있는지.</summary>
    private static bool AnyOf(RoundData r, string[] ids, ProbeOutcome outcome)
    {
        foreach (var id in ids) if (ResultOf(r, id) is { } x && x.Outcome == outcome) return true;
        return false;
    }

    /// <summary>보기 창 하나의 장면. <paramref name="columns"/>는 그래프 폭(픽셀). 점은 계열마다 열 수의 두 배를 넘지 않는다.</summary>
    public static TimelineFrame Frame(IReadOnlyList<RoundData> rounds, IReadOnlyList<Target> targets, IReadOnlyList<GapSpan> gaps,
        IReadOnlyList<SymptomMark> marks, IReadOnlyList<LinkDownView> linkDowns, IReadOnlyList<TimelineBand> bands,
        long fromMs, long toMs, int columns, long intervalMs, string windowLabel, RepresentativeCache? cache = null)
    {
        if (toMs <= fromMs) toMs = fromMs + 1;
        columns = Math.Max(1, columns);
        var (inWindow, offset) = InWindow(rounds, fromMs, toMs, intervalMs);
        // 대표값은 회차마다 한 번만 (12시간 기록의 전체 보기에서 회차마다 네 번 다시 구하던 것이 프레임 예산을 넘겼다, M5 bench).
        // 캐시가 있으면 지난 그리기에서 구한 값을 쓴다 (회차 목록은 뒤로만 늘어난다)
        var routerValues = new double?[inWindow.Length];
        var internetValues = new double?[inWindow.Length];
        if (cache is not null && offset >= 0)
        {
            cache.Update(rounds, targets);
            for (int i = 0; i < inWindow.Length; i++) (routerValues[i], internetValues[i]) = (cache.Router(offset + i), cache.Internet(offset + i));
        }
        else
        {
            var (local, externals) = Roles(targets);
            var buffer = new List<double>();
            for (int i = 0; i < inWindow.Length; i++)
            {
                if (inWindow[i].Gap) continue;
                (routerValues[i], internetValues[i]) = Representative(inWindow[i], local, externals, buffer);
            }
        }
        var router = Series(inWindow, routerValues, gaps, fromMs, toMs, columns, intervalMs);
        var internet = Series(inWindow, internetValues, gaps, fromMs, toMs, columns, intervalMs);
        double max = router.Concat(internet).SelectMany(s => s).Select(p => p.MaxMs).DefaultIfEmpty(0).Max();
        double axis = Math.Max(MinAxisMs, Math.Ceiling(max / AxisStepMs) * AxisStepMs);
        if (max >= axis) axis += AxisStepMs;
        var visibleBands = bands.Where(b => b.ToMs > fromMs && b.FromMs < toMs).ToArray();
        var visibleMarks = marks.Where(m => m.AtMs >= fromMs && m.AtMs <= toMs).ToArray();
        var visibleDowns = linkDowns.Where(d => d.ToMs >= fromMs && d.FromMs <= toMs).ToArray();
        return new TimelineFrame(fromMs, toMs, axis, router, internet, visibleBands, visibleMarks, visibleDowns,
            Summarize(windowLabel, visibleBands, visibleMarks.Length, visibleDowns));
    }

    /// <summary>
    /// 보기 창의 회차 (회차 순)와 원래 목록에서의 시작 위치. 기록은 보통 회차·예정 시각 순이라 이분 탐색으로 자르고,
    /// 순서가 아니면 걸러서 정렬한다 (위치 -1).
    /// </summary>
    private static (RoundData[] Rounds, int Offset) InWindow(IReadOnlyList<RoundData> rounds, long fromMs, long toMs, long intervalMs)
    {
        bool ordered = true;
        for (int i = 1; i < rounds.Count && ordered; i++)
            ordered = rounds[i].Round > rounds[i - 1].Round && rounds[i].DueMs >= rounds[i - 1].DueMs;
        if (!ordered) return (rounds.Where(r => r.DueMs + intervalMs > fromMs && r.DueMs < toMs).OrderBy(r => r.Round).ToArray(), -1);
        int lo = Lower(rounds, r => r.DueMs + intervalMs > fromMs);
        int hi = Lower(rounds, r => r.DueMs >= toMs);
        var result = new RoundData[Math.Max(0, hi - lo)];
        for (int i = 0; i < result.Length; i++) result[i] = rounds[lo + i];
        return (result, lo);

        // 조건이 처음 참이 되는 위치 (조건은 순서에 대해 단조)
        static int Lower(IReadOnlyList<RoundData> list, Func<RoundData, bool> pass)
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

    private static IReadOnlyList<IReadOnlyList<TimelinePoint>> Series(RoundData[] rounds, double?[] vals, IReadOnlyList<GapSpan> gaps,
        long fromMs, long toMs, int columns, long intervalMs)
    {
        var segments = new List<IReadOnlyList<TimelinePoint>>();
        var current = new List<TimelinePoint>();
        void Break()
        {
            if (current.Count > 0) segments.Add(current.ToArray());
            current.Clear();
        }
        double width = (double)(toMs - fromMs) / columns;
        if (rounds.Length <= columns)
        {
            // 회차마다 한 점 (칸 가운데)
            for (int i = 0; i < rounds.Length; i++)
            {
                var r = rounds[i];
                var v = vals[i];
                bool connected = i > 0 && r.Round == rounds[i - 1].Round + 1 && vals[i - 1] is not null && !GapBetween(gaps, rounds[i - 1], r);
                if (v is null) { Break(); continue; }
                if (!connected) Break();
                current.Add(new TimelinePoint(r.DueMs + intervalMs / 2.0, v.Value, v.Value));
            }
            Break();
            return segments;
        }

        // 칸마다 최솟값·최댓값. 칸 안에 값 없는 회차가 있거나 칸 사이 회차가 이어지지 않으면 선을 끊는다
        int index = 0;
        int lastOfPrev = -1;
        bool prevClean = false;
        for (int c = 0; c < columns && index < rounds.Length; c++)
        {
            double colEnd = fromMs + (c + 1) * width;
            int start = index;
            while (index < rounds.Length && rounds[index].DueMs < colEnd) index++;
            if (index == start) continue;
            double min = double.MaxValue, max = double.MinValue;
            int valid = 0;
            bool consecutive = true;
            for (int i = start; i < index; i++)
            {
                if (vals[i] is double v) { valid++; min = Math.Min(min, v); max = Math.Max(max, v); }
                if (i > start && consecutive) consecutive = rounds[i].Round == rounds[i - 1].Round + 1 && !GapBetween(gaps, rounds[i - 1], rounds[i]);
            }
            bool clean = valid == index - start && consecutive;
            bool connected = lastOfPrev >= 0 && prevClean && clean && rounds[start].Round == rounds[lastOfPrev].Round + 1 && !GapBetween(gaps, rounds[lastOfPrev], rounds[start]);
            if (!connected) Break();
            if (valid > 0) current.Add(new TimelinePoint(fromMs + (c + 0.5) * width, min, max));
            if (!clean) Break();
            lastOfPrev = index - 1;
            prevClean = clean;
        }
        Break();
        return segments;
    }

    private static bool GapBetween(IReadOnlyList<GapSpan> gaps, RoundData a, RoundData b)
    {
        // 회차 쌍마다 부르므로 할당 없이 (보통 공백은 없거나 몇 개)
        for (int i = 0; i < gaps.Count; i++)
            if (gaps[i].FromMs < b.AtMs && gaps[i].ToMs > a.AtMs) return true;
        return false;
    }

    /// <summary>화면 읽기용 요약 ("최근 2분: 외부 응답 없음 1번(약 7~9초), 증상 표시 1개, 관측 공백 없음").</summary>
    public static string Summarize(string windowLabel, IReadOnlyList<TimelineBand> bands, int marks, IReadOnlyList<LinkDownView> downs)
    {
        var ext = bands.Where(b => b.Kind == BandKind.ExternalNone).ToArray();
        var parts = new List<string>();
        if (ext.Length == 0) parts.Add("외부 응답 없음 관측 안 됨");
        else
        {
            var text = $"외부 응답 없음 {ext.Length}번";
            if (ext.Length == 1) text += $"({DurationText(ext[0])})";
            if (ext[^1].OpenEnd) text += " · 이어지는 중";
            parts.Add(text);
        }
        int partial = bands.Count(b => b.Kind == BandKind.PartialNone);
        if (partial > 0) parts.Add($"일부 응답 없음 {partial}번");
        if (downs.Count > 0) parts.Add($"링크 끊김 관측 {downs.Count}번" + (downs.Any(d => d.Ongoing) ? " · 이어지는 중" : ""));
        parts.Add(marks == 0 ? "증상 표시 없음" : $"증상 표시 {marks}개");
        int gapCount = bands.Count(b => b.Kind == BandKind.Gap);
        parts.Add(gapCount == 0 ? "관측 공백 없음" : $"관측 공백 {gapCount}번");
        return windowLabel + ": " + string.Join(", ", parts);
    }

    /// <summary>
    /// 길이 문구 (M5 설계 3.5 [1-6, 2-8]). 실제 끊김 길이로 단정하지 않는다:
    /// 범위가 있으면 "약 7~9초", 한쪽만 확실하면 "7초 이상", 측정 끝까지면 "측정 끝까지 이어짐 (7초 이상)",
    /// 한 회차면 "1회차"(앞뒤 성공이 있어 최댓값이 있을 때만 "1회차 · 최대 약 2초").
    /// </summary>
    public static string DurationText(TimelineBand b)
    {
        if (b.Kind == BandKind.Gap) return $"약 {Seconds(b.MinMs)}";
        if (b.Rounds == 1)
            return b.MaxMs is long m1 ? $"1회차 · 최대 약 {Seconds(m1)}" : b.OpenEnd ? "1회차 · 이어지는 중" : "1회차";
        if (b.OpenEnd) return $"끝까지 이어짐 ({AtLeast(b.MinMs)} 이상)";
        if (b.MaxMs is long max) return $"약 {Seconds(b.MinMs)}~{Seconds(max)}";
        return $"{AtLeast(b.MinMs)} 이상";
    }

    /// <summary>
    /// "○ 이상"에 쓰는 하한 길이: 올림 없이 버린다 (59.6초가 "1분 이상"이 되면 최소 길이보다 크게 말한다, M11 G4 1라운드).
    /// 형식은 <see cref="Seconds"/>와 같다.
    /// </summary>
    public static string AtLeast(long ms) => Seconds(Math.Max(0, ms) / 1000 * 1000);

    /// <summary>"9초", "1분 5초", "2시간 3분".</summary>
    public static string Seconds(long ms)
    {
        long s = (long)Math.Round(ms / 1000.0);
        if (s < 60) return $"{s}초";
        if (s < 3600) return s % 60 == 0 ? $"{s / 60}분" : $"{s / 60}분 {s % 60}초";
        long m = s / 60;
        return m % 60 == 0 ? $"{m / 60}시간" : $"{m / 60}시간 {m % 60}분";
    }
}

/// <summary>
/// 보기 창 상태 (M5 설계 4.7): 창 길이 단계, 오른쪽 끝, 지금 따라가기. 순수 상태 기계라 화면 없이 시험한다.
/// </summary>
public sealed class TimelineView
{
    /// <summary>창 길이 단계 (ms). 마지막 단계(0)는 "전체".</summary>
    public static readonly long[] Steps = [60_000, 120_000, 300_000, 600_000, 1_800_000, 3_600_000, 10_800_000, 21_600_000, 43_200_000, 0];

    public TimelineView(int stepIndex = 1) => StepIndex = Math.Clamp(stepIndex, 0, Steps.Length - 1);

    public int StepIndex { get; private set; }
    /// <summary>오른쪽 끝 (따라가기면 무시)</summary>
    public long EndMs { get; private set; }
    public bool Follow { get; private set; } = true;
    public bool IsAll => Steps[StepIndex] == 0;

    /// <summary>지금 측정의 끝(<paramref name="nowMs"/>)과 시작(0) 안에서 보이는 범위.</summary>
    public (long From, long To) Window(long nowMs)
    {
        nowMs = Math.Max(nowMs, 1);
        if (IsAll) return (0, nowMs);
        long len = Steps[StepIndex];
        long end = Follow ? nowMs : Math.Clamp(EndMs, Math.Min(len, nowMs), nowMs);
        long from = end - len;
        if (from < 0) { from = 0; end = Math.Max(end, Math.Min(len, nowMs)); }
        return (from, Math.Max(end, from + 1));
    }

    public void ZoomIn(long nowMs) => SetStep(Math.Max(0, (IsAll ? LargestFitting(nowMs) : StepIndex) - 1), nowMs);
    public void ZoomOut(long nowMs) => SetStep(Math.Min(Steps.Length - 1, StepIndex + 1), nowMs);
    public void ShowAll(long nowMs) { StepIndex = Steps.Length - 1; Follow = true; EndMs = nowMs; }

    /// <summary>창의 1/4만큼 과거로. 따라가기를 멈춘다.</summary>
    public void PanLeft(long nowMs)
    {
        if (IsAll) return;
        var (from, to) = Window(nowMs);
        EndMs = Math.Max(Math.Min(Steps[StepIndex], nowMs), to - (to - from) / 4);
        Follow = EndMs >= nowMs;
    }

    /// <summary>창의 1/4만큼 지금 쪽으로. 끝에 닿으면 따라가기를 다시 켠다.</summary>
    public void PanRight(long nowMs)
    {
        if (IsAll || Follow) return;
        var (from, to) = Window(nowMs);
        EndMs = to + (to - from) / 4;
        if (EndMs >= nowMs) { EndMs = nowMs; Follow = true; }
    }

    public void ToStart(long nowMs)
    {
        if (IsAll) return;
        EndMs = Math.Min(Steps[StepIndex], nowMs);
        Follow = EndMs >= nowMs;
    }

    public void ToNow() { Follow = true; }

    /// <summary>특정 시각을 가운데에 둔다 (증상 목록에서 고를 때).</summary>
    public void CenterOn(long atMs, long nowMs)
    {
        if (IsAll) return;
        long len = Steps[StepIndex];
        EndMs = Math.Min(nowMs, Math.Max(Math.Min(len, nowMs), atMs + len / 2));
        Follow = EndMs >= nowMs;
    }

    private void SetStep(int index, long nowMs)
    {
        if (!Follow && !IsAll)
        {
            // 확대·축소는 보던 창의 가운데를 유지한다
            var (from, to) = Window(nowMs);
            long center = (from + to) / 2;
            StepIndex = index;
            if (!IsAll) EndMs = Math.Min(nowMs, center + Steps[StepIndex] / 2);
            Follow = IsAll || EndMs >= nowMs;
            return;
        }
        StepIndex = index;
    }

    private static int LargestFitting(long nowMs)
    {
        for (int i = Steps.Length - 2; i >= 0; i--) if (Steps[i] < nowMs) return i + 1;
        return 1;
    }

    /// <summary>창 길이 문구 ("2분", "전체").</summary>
    public string Label => IsAll ? "전체" : TimelineModel.Seconds(Steps[StepIndex]);
}

/// <summary>
/// 회차별 대표값(<see cref="TimelineModel.Representative(RoundData, IReadOnlyList{Target})"/>)의 누적 캐시. 화면 하나가 하나를 갖는다.
/// 같은 측정의 회차 목록은 뒤로만 늘어나므로 새 회차만 구한다. 대상 목록이 다른 객체이거나 앞부분이 바뀌었으면 처음부터 다시 구한다.
/// 12시간 기록에서 그릴 때마다 4만 회차를 다시 구하던 것이 프레임 예산을 넘겼다 (M5 bench).
/// </summary>
public sealed class RepresentativeCache
{
    private readonly List<double?> router = [], internet = [];
    private readonly List<double> buffer = [];
    private IReadOnlyList<Target>? targets;
    private RoundData? last;
    private string? local;
    private HashSet<string> externals = [];
    private double internetSum;
    private int internetCount;

    public int Count => router.Count;
    public double? Router(int index) => router[index];
    public double? Internet(int index) => internet[index];

    /// <summary>값 있는 회차의 인터넷 대표값 평균 (없으면 null).</summary>
    public double? InternetAverage => internetCount == 0 ? null : internetSum / internetCount;

    public void Update(IReadOnlyList<RoundData> rounds, IReadOnlyList<Target> forTargets)
    {
        bool extends = ReferenceEquals(targets, forTargets) && rounds.Count >= router.Count
            && (router.Count == 0 || ReferenceEquals(rounds[router.Count - 1], last));
        if (!extends)
        {
            router.Clear();
            internet.Clear();
            internetSum = 0;
            internetCount = 0;
            targets = forTargets;
            (local, externals) = TimelineModel.Roles(forTargets);
        }
        for (int i = router.Count; i < rounds.Count; i++)
        {
            var r = rounds[i];
            var (a, b) = r.Gap ? (null, null) : TimelineModel.Representative(r, local, externals, buffer);
            router.Add(a);
            internet.Add(b);
            if (b is double v) { internetSum += v; internetCount++; }
        }
        last = rounds.Count == 0 ? null : rounds[^1];
    }
}
