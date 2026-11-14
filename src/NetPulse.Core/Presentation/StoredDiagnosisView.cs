using NetPulse.Core.Diagnostics;

namespace NetPulse.Core.Presentation;

/// <summary>
/// 저장된 지난 진단 다시 보기 (M7 설계 3.6 [1-5]). 진단 기록에는 판정·대상 슬롯 결과·결론 번호만 있고 문장·주소는 없다.
/// <list type="bullet">
/// <item>주 결론은 <b>저장된</b> 번호의 머리말·무게, 다른 가능성도 저장된 번호.</item>
/// <item>검사 카드의 대상은 "대상 1·2" 순번만 (현재 설정의 주소를 붙이지 않음).</item>
/// <item>설명 문장·해볼 것은 저장 판정으로 다시 결론을 내 <b>번호가 같을 때만</b> "그때 결과에 대한 설명"으로. 다르면 붙이지 않고 그 사실만 (원인을 단정하지 않음).</item>
/// <item>그 자리 도구(시스템 동작)는 없다.</item>
/// </list>
/// </summary>
public sealed record StoredDiagnosisView(
    DateTimeOffset StartedAt, string? MeasurementId, int Number, string Head, ConclusionWeight Weight, IReadOnlyList<string> Also,
    string? Found, IReadOnlyList<string> Steps, string? Mismatch, IReadOnlyList<CheckView> Checks)
{
    public const string RecheckHint = "지금 상태는 빠른 진단을 다시 돌려 확인하세요.";
    public const string MismatchText = "저장된 결론과 지금 다시 해석한 결론이 달라 설명을 붙이지 않습니다.";

    public static StoredDiagnosisView From(StoredDiagnosis s)
    {
        var (head, weight) = DiagnosisTexts.Head(s.MainConclusion);
        var checks = s.Checks.Select(c => new CheckResult(c.Id, c.Judgment, c.PartlyUnconfirmed, c.State, c.Code, c.Targets,
            TimeSpan.FromMilliseconds(c.ElapsedMs), new Dictionary<string, string>())).ToArray();
        string? found = null, mismatch = null;
        IReadOnlyList<string> steps = [];
        try
        {
            var again = QuickDiagnosis.Conclude(checks);
            if (again.Main.Number == s.MainConclusion) { found = again.Main.Found; steps = again.Main.Steps; }
            else mismatch = MismatchText;
        }
        catch (InvalidOperationException) { mismatch = MismatchText; }   // 검사가 빠진 기록 (일곱이 다 없음)
        return new StoredDiagnosisView(s.StartedAt, s.MeasurementId, s.MainConclusion, head, weight,
            s.AlsoMatched.Select(n => DiagnosisTexts.Head(n).Head).ToArray(), found, steps, mismatch,
            checks.Select(c => DiagnosisView.Check(c, null)).ToArray());
    }

    /// <summary>목록 한 줄: 머리말, 측정 중 진단 여부.</summary>
    public static (string Head, ConclusionWeight Weight, bool DuringMeasurement) Row(StoredDiagnosis s)
    {
        var (head, weight) = DiagnosisTexts.Head(s.MainConclusion);
        return (head, weight, s.MeasurementId is not null);
    }
}
