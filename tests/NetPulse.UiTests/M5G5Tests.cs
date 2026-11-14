using System.Drawing;
using System.IO;
using System.Text.Json;
using NetPulse.Design;
using NetPulse.Design.Tokens;

namespace NetPulse.UiTests;

/// <summary>M5 G5 자동 검사 (설계 5절, 7.4): 측정 화면 bench(M6 진단 구간 포함), 움직임 줄이기, 홈 실측 대비.</summary>
public sealed class M5G5Tests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    [Fact]
    public void MeasureScreenStaysWithinBudget()
    {
        // 재현 절차 (설계 5절): 720분 시나리오를 42,600회차까지 처리 속도로 채운 뒤 1배속 → 준비 → idle-live → zoom-full → pan → nav
        JsonElement? result = null;
        for (int attempt = 0; attempt < 2 && result is null; attempt++)
        {
            var userData = AppDriver.NewUserData();
            M5Fixtures.SaveProfile(userData, "long");
            try
            {
                using var app = AppDriver.LaunchWith(userData, "--demo", "long", "--demo-speed", "600", "--demo-speed-after", "42600:1", "--start", "--bench");
                var bench = AppDriver.WaitFor(() => app.ReadLog().FirstOrDefault(e => e.GetProperty("kind").GetString() == "bench"),
                    e => e.ValueKind == JsonValueKind.Object, "bench", TimeSpan.FromMinutes(15)).GetProperty("data");
                app.WaitExit(TimeSpan.FromSeconds(60));
                bool valid = bench.GetProperty("segments").EnumerateArray().All(s => s.GetProperty("Samples").GetInt32() >= s.GetProperty("Seconds").GetDouble() * 30)
                    && bench.GetProperty("analysis").GetProperty("samples").GetInt32() >= 20;
                if (valid) result = bench;
                else Record("bench-measure-invalid-" + attempt, bench);
            }
            finally { AppDriver.DeleteUserData(userData); }
        }
        Assert.True(result is not null, "bench 측정이 두 번 모두 무효 (표본 부족)");
        var b = result!.Value;
        Record("bench-measure", b);
        // M6 진단 구간은 같은 bench 실행 끝에 붙는다 (설계 5절). 따로 묶어 G5 필수 결과로 남기고, 아래 같은 기준으로 판정한다
        var diagSegments = b.GetProperty("segments").EnumerateArray().Where(s => s.GetProperty("Segment").GetString()!.StartsWith("diag-", StringComparison.Ordinal)).ToArray();
        Record("bench-diagnose", new { segments = diagSegments, navSteps = b.GetProperty("navSteps").EnumerateArray().Where(n => n.TryGetProperty("segment", out _)).ToArray() });
        Assert.Equal(["diag-enter", "diag-run", "diag-reenter"], diagSegments.Select(s => s.GetProperty("Segment").GetString()));
        var problems = new List<string>();
        foreach (var s in b.GetProperty("segments").EnumerateArray())
        {
            string seg = s.GetProperty("Segment").GetString()!;
            double p95 = s.GetProperty("P95").GetDouble(), max = s.GetProperty("Max").GetDouble();
            int over = s.GetProperty("Over33").GetInt32();
            if (seg == "idle-live" && p95 > FrameBudget.IdleP95) problems.Add($"{seg} 95백분위 {p95:0.0} > {FrameBudget.IdleP95}");
            if (seg != "idle-live" && (max > FrameBudget.TransitionMax || over > FrameBudget.TransitionHitches)) problems.Add($"{seg} 최대 {max:0.0} ms, 33 ms 초과 {over}회");
        }
        double analysisP95 = b.GetProperty("analysis").GetProperty("p95Ms").GetDouble();
        if (analysisP95 > 500) problems.Add($"잠정 분석 95백분위 {analysisP95:0} ms > 500");
        long mb = b.GetProperty("privateMb").GetInt64();
        if (mb > 400) problems.Add($"전용 메모리 {mb} MB > 400");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void ReducingMotionStopsTheMapFlowAndFreezesTheScreen()
    {
        // 홈 측정 중: 경로 맵의 흐르는 빛(반복)이 있다 → 설정에서 움직임 줄이기 → 활성 이동·크기·반복 0, 멈춘 회차에서 캡처 두 장이 같다
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady");
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "600", "--demo-hold", "150", "--start", "--screen", "home");
            app.WaitDiag(d => d.GetProperty("held").GetBoolean(), h => h, "멈춤");
            app.WaitDiag(d => d.GetProperty("active").GetProperty("repeat").GetInt32(), n => n > 0, "반복 모션 있음 (전제)");
            app.CloseToasts();
            app.Select("nav.settings");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "settings", "설정");
            app.Toggle("settings.reduceMotion");
            app.Select("nav.home");
            var d = app.WaitDiag(x => x, x => M5Fixtures.Str(x, "screen") == "home" && x.GetProperty("reduceMotion").GetBoolean()
                && x.GetProperty("active").GetProperty("move").GetInt32() + x.GetProperty("active").GetProperty("scale").GetInt32() + x.GetProperty("active").GetProperty("repeat").GetInt32() == 0, "움직임 0");
            Thread.Sleep(700);
            using var a = app.CaptureBitmap();
            Thread.Sleep(500);
            using var b = app.CaptureBitmap();
            var diff = ImageCompare.Diff(a, b);
            Record("reducedmotion-home", new { diff.Mean, diff.P99, active = d.GetProperty("active") });
            Assert.True(diff.Mean <= 1.0 && diff.P99 <= 4, $"캡처 차이 평균 {diff.Mean:0.00}, 99백분위 {diff.P99}");
            app.Close();
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    public static TheoryData<string> Themes => new() { "dark", "light" };

    [Theory, MemberData(nameof(Themes))]
    public void MeasuredContrastOnHomeDuringAnOutage(string theme)
    {
        // 끊김 중(배경 빛이 붉음) 홈 큰 카드의 글자: 글자 있음/없음 두 장, 글자 없는 배경과 토큰 색의 대비 (M2 방식)
        var palette = theme == "dark" ? Palette.Dark : Palette.Light;
        string[] ids = ["home.sentence", "home.numbers", "home.status"];
        var boxes = new Dictionary<string, Rectangle>();
        Bitmap? hidden = null;
        foreach (var hide in new[] { false, true })
        {
            var userData = AppDriver.NewUserData();
            M5Fixtures.SaveProfile(userData, "outage-long");
            try
            {
                string[] args = ["--demo", "outage-long", "--demo-speed", "600", "--demo-hold", "120", "--start", "--screen", "home", "--freeze-motion", "--theme", theme];
                using var app = AppDriver.LaunchWith(userData, hide ? [.. args, "--hide-measured-text"] : args);
                app.WaitDiag(d => d.GetProperty("held").GetBoolean() && d.GetProperty("analysis").ValueKind == JsonValueKind.Object, h => h, "멈춤과 분석");
                app.CloseToasts();
                Thread.Sleep(1600);   // 배경 빛 상태 전환(1.2초)이 끝난 뒤
                if (!hide) foreach (var id in ids) boxes[id] = app.ToClient(app.Find(id).Current.BoundingRectangle);
                var shot = app.CaptureBitmap();
                app.Save(shot, $"g5-home-contrast-{theme}-{(hide ? "bg" : "text")}");
                if (hide) hidden = shot; else shot.Dispose();
                app.Close();
            }
            finally { AppDriver.DeleteUserData(userData); }
        }
        var results = new List<object>();
        var problems = new List<string>();
        foreach (var id in ids)
        {
            var fg = id == "home.sentence" ? palette.Ink : palette.Muted;
            var r = G5Tests.MeasureContrast(hidden!, boxes[id], fg, threshold: 4.5);
            results.Add(new { id, r.CellP10Min, r.P1, r.LargestFailingBlob, r.Cells });
            if (r.CellP10Min < 4.5) problems.Add($"{id}: 칸별 10백분위 최저 {r.CellP10Min:0.00} < 4.5");
            if (r.LargestFailingBlob > 12) problems.Add($"{id}: 기준 미달 연결 영역 {r.LargestFailingBlob} px > 12");
        }
        hidden!.Dispose();
        Record("contrast-home-" + theme, results);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
