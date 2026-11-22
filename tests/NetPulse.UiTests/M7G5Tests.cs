using System.Drawing;
using System.IO;
using System.Text.Json;
using NetPulse.Design;
using NetPulse.Design.Tokens;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M7 G5 추가 (설계 7.4·5절): 고대비 둘 × (목록, 상세), 효과 줄이기(상세), 움직임 줄이기(목록 → 상세), 목록 → 상세 교차 페이드,
/// 결론 카드 실측 대비, 기록 화면 bench(프레임·메모리).
/// </summary>
public sealed class M7G5Tests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static M5MatrixTests.Scene Scene(string id) => M5MatrixTests.Scenes("m7-scenes.txt").Single(s => s.Id == id);

    public static TheoryData<string, string, string> Extras() => new()
    {
        { "m7-records-list-hc-aquatic", "records-list-problems", "--simulate-high-contrast aquatic" },
        { "m7-records-list-hc-desert", "records-list-problems", "--simulate-high-contrast desert" },
        { "m7-records-detail-hc-aquatic", "records-detail-outages", "--simulate-high-contrast aquatic" },
        { "m7-records-detail-hc-desert", "records-detail-outages", "--simulate-high-contrast desert" },
        { "m7-records-detail-dark-reduced", "records-detail-outages", "--theme dark --simulate-transparency-off" },
        { "m7-records-detail-light-reduced", "records-detail-outages", "--theme light --simulate-transparency-off" },
        { "m7-records-detail-dark-reducedmotion", "records-detail-episode", "--theme dark --simulate-animations-off" },
        { "m7-records-detail-light-reducedmotion", "records-detail-episode", "--theme light --simulate-animations-off" },
    };

    [Theory, MemberData(nameof(Extras))]
    public void ExtraSceneRenders(string name, string sceneId, string extra)
    {
        using var app = M5MatrixTests.Open(Scene(sceneId), extra.Split(' '));
        app.CloseToasts();
        var (settledShot, shotWait) = app.WaitSettledAndCapture();   // 고정 대기 대신 상태 기반 대기 (M12 G1-B)
        using var shot = settledShot;
        var path = app.Save(shot, "matrix-" + name);
        var problems = M5MatrixTests.LayoutProblems(app.LayoutNow());
        Record("matrix-" + name, new { name, scene = sceneId, extra, file = Path.GetFileName(path), captureWait = shotWait.Json, layoutProblems = problems, diag = app.Diag() });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        Assert.Equal(0, app.Close());
    }

    public static TheoryData<string> Progress => new() { "0", "0.5", "1" };

    /// <summary>목록 → 상세 교차 페이드를 진행도에 멈춰 찍는다.</summary>
    [Theory, MemberData(nameof(Progress))]
    public void ListToDetailCrossfade(string progress)
    {
        using var app = M5MatrixTests.Open(Scene("records-list-problems"), "--theme", "dark", "--motion-hold", progress);
        app.Invoke("records.row.0");
        app.WaitDiag(d => d.GetProperty("screenDiag").TryGetProperty("detail", out var x) && x.ValueKind == JsonValueKind.Object
            && d.GetProperty("screenDiag").TryGetProperty("detailComplete", out var c) && c.GetBoolean(), b => b, "상세");
        // 고정 대기 대신 상태 기반 대기 (M12 G1-B). 진행도 고정 모션은 앱이 세지 않는다. 최대 대기는 지금까지의 500 ms 그대로
        var (settledShot, shotWait) = app.WaitSettledAndCapture(TimeSpan.FromMilliseconds(500));
        using var shot = settledShot;
        string name = $"m7-transition-crossfade-{progress}";
        var path = app.Save(shot, "matrix-" + name);
        Record("matrix-" + name, new { name, kind = "crossfade", progress, file = Path.GetFileName(path), captureWait = shotWait.Json, diag = app.Diag() });
        Assert.Equal(0, app.Close());
    }

    public static TheoryData<string> Themes => new() { "dark", "light" };

    /// <summary>상세 결론 카드 글자의 실측 대비 (M2 방식): 글자 있음/없음 두 장.</summary>
    [Theory, MemberData(nameof(Themes))]
    public void MeasuredContrastOnTheRecordConclusion(string theme)
    {
        var palette = theme == "dark" ? Palette.Dark : Palette.Light;
        string[] ids = ["records.detail.grade", "records.detail.sentence"];
        var boxes = new Dictionary<string, Rectangle>();
        Bitmap? hidden = null;
        foreach (var hide in new[] { false, true })
        {
            string[] extra = ["--theme", theme];
            using var app = M5MatrixTests.Open(Scene("records-detail-outages"), hide ? [.. extra, "--hide-measured-text"] : extra);
            app.CloseToasts();
            Thread.Sleep(1600);
            if (!hide) foreach (var id in ids) boxes[id] = app.ToClient(app.Find(id).Current.BoundingRectangle);
            var shot = app.CaptureBitmap();
            app.Save(shot, $"g5-records-contrast-{theme}-{(hide ? "bg" : "text")}");
            if (hide) hidden = shot; else shot.Dispose();
            Assert.Equal(0, app.Close());
        }
        var results = new List<object>();
        var problems = new List<string>();
        foreach (var id in ids)
        {
            var r = G5Tests.MeasureContrast(hidden!, boxes[id], palette.Ink, threshold: 4.5);
            results.Add(new { id, r.CellP10Min, r.P1, r.LargestFailingBlob, r.Cells });
            if (r.CellP10Min < 4.5) problems.Add($"{id}: 칸별 10백분위 최저 {r.CellP10Min:0.00} < 4.5");
            if (r.LargestFailingBlob > 12) problems.Add($"{id}: 기준 미달 연결 영역 {r.LargestFailingBlob} px > 12");
        }
        hidden!.Dispose();
        Record("contrast-records-" + theme, results);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// 기록 화면 bench (설계 5절, 구현 중 결정 3·6): 기록 40개(12시간 2개). 구간마다 전환 최대 50 ms·33 ms 초과 2회, 최고 전용 메모리 400 MB.
    /// 400 MB를 넘으면 실측값과 함께 실패해 기준을 다시 정하게 한다 (근거 없이 늘리지 않음).
    /// </summary>
    [Fact]
    public void RecordsScreenStaysWithinBudget()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            M7Fixtures.BenchRecords(userData);
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "home", "--bench-records");
            var bench = AppDriver.WaitFor(() => app.ReadLog().FirstOrDefault(e => e.GetProperty("kind").GetString() == "bench"),
                e => e.ValueKind == JsonValueKind.Object, "records bench", TimeSpan.FromMinutes(10)).GetProperty("data");
            app.WaitExit(TimeSpan.FromSeconds(60));
            Record("bench-records", bench);
            Assert.False(bench.TryGetProperty("error", out var err), "bench 실패: " + err);
            var problems = new List<string>();
            // 필수 구간이 모두 있고 구간마다 프레임 표본이 있어야 한다 (빠진 구간·표본 없는 구간을 통과로 세지 않음, G4 M7 1라운드 b7)
            string[] required = ["records-enter", "records-open", "records-timeline", "records-switch", "records-compare", "records-nav-during-analysis", "records-reenter", "records-after-memo"];
            var seen = bench.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("Segment").GetString()!).ToArray();
            foreach (var r in required.Except(seen)) problems.Add($"{r} 구간이 없습니다");
            var verdicts = new List<object>();
            foreach (var s in bench.GetProperty("segments").EnumerateArray())
            {
                string seg = s.GetProperty("Segment").GetString()!;
                if (s.GetProperty("Samples").GetInt32() < 10) problems.Add($"{seg} 프레임 표본 {s.GetProperty("Samples").GetInt32()}개 (10개 미만)");
                verdicts.Add(Perf.Check(s, PerfKind.Transition, problems));   // M12 설계 G2
            }
            Perf.Record("records", verdicts);
            long peak = bench.GetProperty("peakPrivateMb").GetInt64();
            if (peak > 400) problems.Add($"최고 전용 메모리 {peak} MB > 400 (실측값으로 기준을 다시 정할 것)");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
