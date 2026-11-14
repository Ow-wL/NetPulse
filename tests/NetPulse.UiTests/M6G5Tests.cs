using System.Drawing;
using System.IO;
using System.Text.Json;
using NetPulse.Design;
using NetPulse.Design.Tokens;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M6 G5 추가 장면 (설계 7.4): 고대비 둘 × 결론 화면 둘, 효과 줄이기, 움직임 줄이기, 홈 → 진단 교차 페이드, 결론 카드 실측 대비.
/// 장면은 m6-scenes.txt의 것을 쓰고, 판정은 M5와 같다 (배치 검사 0, 앱 기록 오류 0).
/// </summary>
public sealed class M6G5Tests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static M5MatrixTests.Scene Scene(string id) => M5MatrixTests.Scenes("m6-scenes.txt").Single(s => s.Id == id);

    public static TheoryData<string, string, string> Extras() => new()
    {
        { "m6-diagnose-dns-hc-aquatic", "diagnose-dns", "--simulate-high-contrast aquatic" },
        { "m6-diagnose-dns-hc-desert", "diagnose-dns", "--simulate-high-contrast desert" },
        { "m6-diagnose-outside-hc-aquatic", "diagnose-outside", "--simulate-high-contrast aquatic" },
        { "m6-diagnose-outside-hc-desert", "diagnose-outside", "--simulate-high-contrast desert" },
        { "m6-diagnose-dns-dark-reduced", "diagnose-dns", "--theme dark --simulate-transparency-off" },
        { "m6-diagnose-dns-light-reduced", "diagnose-dns", "--theme light --simulate-transparency-off" },
        { "m6-diagnose-running-dark-reducedmotion", "diagnose-running", "--theme dark --simulate-animations-off" },
        { "m6-diagnose-running-light-reducedmotion", "diagnose-running", "--theme light --simulate-animations-off" },
    };

    [Theory, MemberData(nameof(Extras))]
    public void ExtraSceneRenders(string name, string sceneId, string extra)
    {
        using var app = M5MatrixTests.Open(Scene(sceneId), extra.Split(' '));
        app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        var path = app.Save(shot, "matrix-" + name);
        var problems = M5MatrixTests.LayoutProblems(app.LayoutNow());
        Record("matrix-" + name, new { name, scene = sceneId, extra, file = Path.GetFileName(path), layoutProblems = problems, diag = app.Diag() });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        Assert.Equal(0, app.Close());
    }

    public static TheoryData<string> Progress => new() { "0", "0.5", "1" };

    /// <summary>홈 → 진단 탐색 교차 페이드를 진행도에 멈춰 찍는다 (M5 전환 시험과 같은 방식).</summary>
    [Theory, MemberData(nameof(Progress))]
    public void HomeToDiagnoseCrossfade(string progress)
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "home", "--freeze-motion", "--theme", "dark", "--motion-hold", progress);
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "home", "홈");
            app.Select("nav.diagnose");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "diagnose", "진단 화면");
            Thread.Sleep(500);
            using var shot = app.CaptureBitmap();
            string name = $"m6-transition-crossfade-{progress}";
            var path = app.Save(shot, "matrix-" + name);
            Record("matrix-" + name, new { name, kind = "crossfade", progress, file = Path.GetFileName(path), diag = app.Diag() });
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    public static TheoryData<string> Themes => new() { "dark", "light" };

    /// <summary>결론 카드 글자의 실측 대비 (M2 방식): 글자 있음/없음 두 장, 글자 없는 배경과 토큰 색의 대비.</summary>
    [Theory, MemberData(nameof(Themes))]
    public void MeasuredContrastOnTheConclusionCard(string theme)
    {
        var palette = theme == "dark" ? Palette.Dark : Palette.Light;
        string[] ids = ["diagnose.conclusion.head", "diagnose.conclusion.found"];
        var boxes = new Dictionary<string, Rectangle>();
        Bitmap? hidden = null;
        foreach (var hide in new[] { false, true })
        {
            string[] extra = ["--theme", theme];
            using var app = M5MatrixTests.Open(Scene("diagnose-outside"), hide ? [.. extra, "--hide-measured-text"] : extra);
            app.CloseToasts();
            Thread.Sleep(1600);   // 배경 빛 전환이 끝난 뒤
            if (!hide) foreach (var id in ids) boxes[id] = app.ToClient(app.Find(id).Current.BoundingRectangle);
            var shot = app.CaptureBitmap();
            app.Save(shot, $"g5-diagnose-contrast-{theme}-{(hide ? "bg" : "text")}");
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
        Record("contrast-diagnose-" + theme, results);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
