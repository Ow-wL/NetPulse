using System.IO;
using System.Text.Json;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M12 설계 C 표시 [1-7] (N11 Wi‑Fi 신호): 측정 끝(약해졌다 끊김·강한데 끊김·권한 없음), 기록 상세, 리포트 미리 보기를 다크·라이트 × 100%·150%로 찍고
/// 배치 검사(잘림·글자 잘림·겹침 0)와 타임라인 열 수(= 신호 축 여백을 뺀 그래프 폭)를 본다. 장면 목록은 m12-scenes.txt.
/// 타임라인 장면은 신호 표시(선·상태 글)가 기대와 같은지도 본다 (진단 값 timelineState.signal, legendSignal).
/// </summary>
public sealed class M12MatrixTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private const string HcA = "--simulate-high-contrast aquatic", HcD = "--simulate-high-contrast desert";

    /// <summary>타임라인 장면의 기대 신호 표시 (Line = 오른쪽 축 + 점선, State = 범례 자리 상태 글). 리포트 장면은 없음.</summary>
    private static readonly Dictionary<string, string> ExpectedSignal = new()
    {
        ["measure-wifi-weak-done"] = "Line",
        ["measure-wifi-strong-done"] = "Line",
        ["measure-wifi-denied-done"] = "State",
        ["records-detail-wifi"] = "Line",
    };

    public static TheoryData<string, string, string> Matrix()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var s in M5MatrixTests.Scenes("m12-scenes.txt"))
            foreach (var t in new[] { "dark", "light" })
                foreach (var scale in new[] { "1", "1.5" })
                    d.Add(s.Id, t, scale);
        return d;
    }

    /// <summary>
    /// 추가 장면 (이름, 장면 목록 파일, 장면, 인수): 고대비에서 신호 선은 시스템 색(Hotlight). 형식은 M11MatrixTests.Extras와 같다
    /// (ui-test.ps1이 이 줄에서 필수 결과 이름을 읽는다).
    /// </summary>
    public static TheoryData<string, string, string, string> Extras() => new()
    {
        { "m12-measure-wifi-weak-done-hc-aquatic", "m12-scenes.txt", "measure-wifi-weak-done", HcA },
        { "m12-records-detail-wifi-hc-desert", "m12-scenes.txt", "records-detail-wifi", HcD },
        { "m12-measure-wifi-denied-done-hc-aquatic-150", "m12-scenes.txt", "measure-wifi-denied-done", HcA + " --ui-scale 1.5" },
    };

    /// <summary>타임라인 진단 값의 신호 표시가 기대와 같은지 (다르면 문제 목록에).</summary>
    private static List<string> SignalProblems(string sceneId, JsonElement diag)
    {
        var problems = new List<string>();
        if (!ExpectedSignal.TryGetValue(sceneId, out var expected)) return problems;
        if (diag.GetProperty("screenDiag") is not { ValueKind: JsonValueKind.Object } sd || !sd.TryGetProperty("timelineState", out var t) || t.ValueKind != JsonValueKind.Object)
        {
            problems.Add("타임라인 진단 값 없음");
            return problems;
        }
        if (!t.TryGetProperty("signal", out var s) || s.ValueKind != JsonValueKind.Object) { problems.Add("신호 장면 없음"); return problems; }
        string display = s.GetProperty("display").GetString() ?? "";
        if (display != expected) problems.Add($"신호 표시 {display} ≠ {expected}");
        bool axis = t.GetProperty("signalAxis").GetBoolean();
        if (axis != (expected == "Line")) problems.Add($"신호 축 여백 {axis}");
        if (expected == "Line" && s.GetProperty("segments").GetInt32() == 0) problems.Add("신호 선 구간 0");
        string legend = sd.TryGetProperty("legendSignal", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "";
        if (!legend.StartsWith(expected == "Line" ? "line|" : "state|", StringComparison.Ordinal)) problems.Add($"범례 신호 항목 '{legend}'");
        return problems;
    }

    private static void Shoot(string name, string file, string sceneId, string[] extra, object meta)
    {
        var scene = M5MatrixTests.Scenes(file).Single(s => s.Id == sceneId);
        using var app = M5MatrixTests.Open(scene, extra);
        app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        var path = app.Save(shot, "matrix-" + name);
        var problems = M5MatrixTests.LayoutProblems(app.LayoutNow());
        var privacy = M9MatrixTests.PrivateText(app.Window);
        var diag = app.Diag();
        problems.AddRange(M5MatrixTests.TimelineProblems(diag));
        problems.AddRange(SignalProblems(sceneId, diag));
        Record("matrix-" + name, new { name, sceneFile = file, scene = sceneId, meta, file = Path.GetFileName(path), w = shot.Width, h = shot.Height, layoutProblems = problems, privacy, diag });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(privacy.Count == 0, string.Join("\n", privacy));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        Assert.Equal(0, app.Close());
    }

    [Theory, MemberData(nameof(Matrix))]
    public void SceneLooksRightAndFits(string id, string theme, string scale) =>
        Shoot($"m12-{id}-{theme}-{(scale == "1" ? "100" : "150")}", "m12-scenes.txt", id, ["--theme", theme, "--ui-scale", scale], new { theme, scale });

    [Theory, MemberData(nameof(Extras))]
    public void ExtraSceneRenders(string name, string file, string sceneId, string extra) => Shoot(name, file, sceneId, extra.Split(' '), new { extra });
}
