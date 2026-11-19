using System.Globalization;
using NetPulse.Core.Analysis;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Presentation;

/// <summary>대상 카드 값 (M5 설계 3.5). 숫자는 <see cref="StatsCalculator"/> 결과를 그대로 쓴다.</summary>
/// <param name="Latest">"18 ms" / "응답 없음" / "검사 오류" / "측정 전"</param>
/// <param name="LossSentence">"100번 중 2번 응답이 없었습니다" (검사 오류는 분모에서 뺀다)</param>
/// <param name="MaxConsecutiveLoss">최대 연속 실패 (회차 수, N9)</param>
public sealed record TargetCard(string TargetId, string Name, string Role, string Latest, ProbeOutcome? LatestOutcome,
    string LossRate, string LossSentence, string Average, int MaxConsecutiveLoss, DateTimeOffset? LastSuccess, DateTimeOffset? LastLoss, int Errors);

/// <summary>등급 표시: 글자, 이름, 고리 채움 칸 수(0~4, 관측 부족·판단 불가는 0 + 점선).</summary>
public sealed record GradeBadge(string Letter, string Name, int Fill, bool Dashed);

/// <summary>
/// 홈·측정 화면의 문장과 숫자 (순수). 점수를 만들지 않는다: 분석에 점수 개념이 없고 근거 없는 정밀도를 보여주지 않는다 (M5 설계 4.5).
/// </summary>
public static class LiveSummary
{
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    public static GradeBadge Badge(Grade g) => g switch
    {
        Grade.Good => new("A", Sentences.GradeName(g), 4, false),
        Grade.Fair => new("B", Sentences.GradeName(g), 3, false),
        Grade.Bad => new("C", Sentences.GradeName(g), 2, false),
        Grade.VeryBad => new("D", Sentences.GradeName(g), 1, false),
        Grade.Undecidable => new("?", Sentences.GradeName(g), 0, true),
        _ => new("–", Sentences.GradeName(g), 0, true),
    };

    public static IReadOnlyList<TargetCard> Cards(IReadOnlyList<Target> targets, MeasurementStats stats, RoundData? latest)
    {
        var byId = stats.Targets.ToDictionary(s => s.TargetId);
        return targets.Select(t =>
        {
            byId.TryGetValue(t.Id, out var s);
            var r = latest?.Results.FirstOrDefault(x => x.TargetId == t.Id);
            string latestText = r is null ? "측정 전" : r.Outcome switch
            {
                ProbeOutcome.Success when r.RttMs is double ms => PathMapModel.Ms(ms),
                ProbeOutcome.Lost => "응답 없음",
                _ => "검사 오류",
            };
            int counted = s is null ? 0 : s.Success + s.Lost;
            string rate = s?.LossRate is double lr ? Percent(lr) : "값 없음";
            string sentence = counted == 0 ? "아직 판단할 응답이 없습니다" : $"{counted}번 중 {s!.Lost}번 응답이 없었습니다";
            string avg = s?.AvgRttMs is double a ? PathMapModel.Ms(a) : "값 없음";
            // 측정 방식이 TCP면 역할 옆에 (M9 설계 B5): "외부 · TCP 443"
            string role = (t.Role == TargetRole.Local ? "로컬" : "외부") + (t.Method.Kind == ProbeMethodKind.Tcp ? " · " + RecordView.MethodText(t.Method) : "");
            return new TargetCard(t.Id, t.Name, role, latestText, r?.Outcome, rate, sentence, avg,
                s?.MaxConsecutiveLoss ?? 0, s?.LastSuccess, s?.LastLoss, s?.Error ?? 0);
        }).ToArray();
    }

    /// <summary>
    /// 숫자 줄: "최근 10분 동안 끊김 1회 · 평균 지연 18 ms · 응답 성공 99.9%".
    /// 끊김 = 분석의 끊김 구간 수, 평균 지연 = 회차별 인터넷 대표값의 평균, 응답 성공 = 모든 대상의 성공 / (성공 + 손실).
    /// </summary>
    public static string Numbers(long elapsedMs, AnalysisResult? analysis, IReadOnlyList<RoundData> rounds, IReadOnlyList<Target> targets, MeasurementStats stats,
        RepresentativeCache? cache = null)
    {
        var parts = new List<string>();
        string span = TimelineModel.Seconds(elapsedMs);
        parts.Add(analysis is null ? $"{span} 측정" : $"{span} 동안 끊김 {analysis.Episodes.Count}회");
        double? average;
        if (cache is not null)
        {
            cache.Update(rounds, targets);   // 그릴 때마다 전체를 다시 구하지 않는다 (M5 bench)
            average = cache.InternetAverage;
        }
        else
        {
            var reps = rounds.Where(r => !r.Gap).Select(r => TimelineModel.Representative(r, targets).Internet).Where(v => v is not null).Select(v => v!.Value).ToArray();
            average = reps.Length > 0 ? reps.Average() : null;
        }
        if (average is double avg) parts.Add($"평균 지연 {PathMapModel.Ms(avg)}");
        int success = stats.Targets.Sum(s => s.Success), lost = stats.Targets.Sum(s => s.Lost);
        if (success + lost > 0) parts.Add($"응답 성공 {Percent((double)success / (success + lost))}");
        return string.Join(" · ", parts);
    }

    public static string Percent(double ratio)
    {
        double p = ratio * 100;
        return p is > 0 and < 0.1 ? "0.1% 미만" : p.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>시:분:초 ("오후 2:32:10"). 기록에서 읽은 시각은 UTC라 지금 시계의 시간대로 바꿔 쓴다 (<see cref="When"/>과 같게).</summary>
    public static string Clock(DateTimeOffset t, DateTimeOffset now) => t.ToOffset(now.Offset).ToString("tt h:mm:ss", Ko);

    /// <summary>"오늘 오후 1:20", "어제 오후 9:02", "11월 10일 오후 8:45" (<paramref name="now"/>는 앱 벽시계).</summary>
    public static string When(DateTimeOffset t, DateTimeOffset now)
    {
        var local = t.ToOffset(now.Offset);
        var day = local.Date;
        if (day == now.Date) return "오늘 " + local.ToString("tt h:mm", Ko);
        if (day == now.Date.AddDays(-1)) return "어제 " + local.ToString("tt h:mm", Ko);
        return local.ToString(day.Year == now.Year ? "M월 d일 tt h:mm" : "yyyy년 M월 d일 tt h:mm", Ko);
    }

    /// <summary>머리 날짜 "11월 13일 목요일".</summary>
    public static string Date(DateTimeOffset now) => now.ToString("M월 d일 dddd", Ko);

    /// <summary>"10분 중 3분 12초".</summary>
    public static string Progress(long elapsedMs, long plannedMs) =>
        $"{TimelineModel.Seconds(plannedMs)} 중 {TimelineModel.Seconds(Math.Min(elapsedMs, plannedMs))}";
}
