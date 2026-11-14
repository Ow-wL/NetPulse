using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;

namespace NetPulse.Core.Presentation;

/// <summary>결론 카드 (M6 설계 3.6).</summary>
public sealed record ConclusionView(int Number, string Head, ConclusionWeight Weight, string Found, IReadOnlyList<string> Steps,
    IReadOnlyList<string> Also, IReadOnlyList<string> Notes, IReadOnlyList<string> Scope);

/// <summary>
/// 대상 한 줄. <paramref name="Shown"/>은 이 PC 화면의 펼친 상세에만 쓰는 주소·도메인이다
/// (복사·진단 기록·리포트에는 넣지 않는다, 설계 3.6 [1-4]).
/// </summary>
public sealed record TargetLine(string Slot, string Label, string? Shown, string Outcome, long ElapsedMs, string? Reason);

/// <summary>검사 카드.</summary>
public sealed record CheckView(CheckId Id, string Name, CheckJudgment Judgment, string JudgmentText, bool PartlyUnconfirmed, string What, string Explain,
    IReadOnlyList<TargetLine> Targets, string? StateText);

/// <summary>비교 띠.</summary>
public sealed record CompareView(string Title, IReadOnlyList<string> Lines, string? Skipped);

/// <summary>진단 화면용 순수 모델 (M6 설계 3.6). 화면은 이 값을 그리기만 한다.</summary>
public static class DiagnosisView
{
    public static ConclusionView Conclusion(DiagnosisResult r)
    {
        var (head, weight) = DiagnosisTexts.Head(r.Main.Number);
        var also = r.AlsoMatched.Select(n => DiagnosisTexts.Head(n).Head).ToArray();
        var notes = new List<string>();
        foreach (var c in r.Checks.Where(c => c.Judgment == CheckJudgment.Unmeasured && c.Id is CheckId.Portal or CheckId.Link))
            notes.Add($"확인하지 못한 검사: {QuickDiagnosis.CheckName(c.Id)} ({DiagnosisTexts.Reason(c.Code ?? StateCode(c.State))})");   // 조사 "을(를)" 없이
        return new ConclusionView(r.Main.Number, head, weight, r.Main.Found, r.Main.Steps, also, notes, DiagnosisTexts.Scope(r.Checks));
    }

    public static CheckView Check(CheckResult c, DiagnosisSettings settings)
    {
        var targets = c.Targets.Select((t, i) => new TargetLine(t.Slot, "대상 " + (i + 1), Shown(c.Id, i, settings),
            QuickDiagnosis.OutcomeName(t.Outcome), (long)t.Elapsed.TotalMilliseconds, t.Outcome == TargetOutcome.Ok ? null : DiagnosisTexts.Reason(t.Code ?? StateCode(RunState.Error)))).ToArray();
        string? state = c.State switch
        {
            RunState.NotRun => "실행하지 않음",
            RunState.Timeout => "시간 초과",
            RunState.Error => "오류",
            _ => null,
        };
        return new CheckView(c.Id, QuickDiagnosis.CheckName(c.Id), c.Judgment, QuickDiagnosis.JudgmentName(c.Judgment) + (c.PartlyUnconfirmed ? " (일부 미확인)" : ""),
            c.PartlyUnconfirmed, DiagnosisTexts.What(c.Id), DiagnosisTexts.Explain(c), targets, state);
    }

    /// <summary>펼친 상세의 대상 표시값 (이 PC 화면만): TCP는 주소, DNS·HTTPS는 도메인, 로그인은 호스트.</summary>
    public static string? Shown(CheckId id, int index, DiagnosisSettings s) => id switch
    {
        CheckId.Tcp when index < s.TcpTargets.Count => s.TcpTargets[index] + ":" + s.TcpPort,
        CheckId.Dns or CheckId.Https when index < s.Domains.Count => s.Domains[index],
        CheckId.Portal => s.PortalUri.Host,
        _ => null,
    };

    public static CompareView? Compare(DiagnosisRun run, int skippedRecords)
    {
        if (run.Previous is not { } prev || run.Changes is not { } changes) return null;
        string from = prev.FromRecord ? "지난 진단 기록과" : "지난 검사와";   // 받침에 따라 조사가 다르다 ("기록와"였음)
        string? skipped = skippedRecords > 0 ? $"읽지 못한 진단 기록 {skippedRecords}개는 건너뛰었습니다." : null;
        if (changes.Count == 0) return new CompareView($"{from} 같습니다.", [], skipped);
        var lines = changes.Select(c => $"{QuickDiagnosis.CheckName(c.Id)}: {QuickDiagnosis.JudgmentName(c.Before)} → {QuickDiagnosis.JudgmentName(c.After)}").ToArray();
        return new CompareView($"{from} 달라진 검사 {changes.Count}개", lines, skipped);
    }

    /// <summary>홈 카드 한 줄: 머리말과 무게 (최근 결과 또는 기록).</summary>
    public static (string Head, ConclusionWeight Weight) Recent(int conclusion) => DiagnosisTexts.Head(conclusion);

    private static string StateCode(RunState s) => s switch { RunState.Timeout => "timeout", RunState.NotRun => "not-run", _ => "ProbeError" };
}
