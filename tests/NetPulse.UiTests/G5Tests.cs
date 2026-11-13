using System.IO;
using System.Drawing;
using System.Text.Json;
using NetPulse.Design;
using NetPulse.Design.Tokens;

namespace NetPulse.UiTests;

/// <summary>
/// 디자인 게이트 G5의 자동 검사 (M2 설계 5.4, 5.9, 7.3, 7.4). 결과는 NETPULSE_UITEST_OUT/g5/*.json으로 남고
/// build/ui-test.ps1이 g5.json으로 묶는다. 캡처는 창 전용이고 조작은 UI Automation 패턴만 쓴다.
/// </summary>
public sealed class G5Tests
{
    private static string G5Dir
    {
        get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; }
    }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static AppDriver Showcase(params string[] extra) => AppDriver.Launch(["--showcase", .. extra]);

    // ---------------- 프레임·자원 (5.9) ----------------

    [Fact]
    public void FramesAndResourcesStayWithinBudget()
    {
        JsonElement bench = default;
        string? invalid = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            using var app = Showcase("--bench");
            bench = AppDriver.WaitFor(() => app.ReadLog().FirstOrDefault(e => e.GetProperty("kind").GetString() == "bench"),
                e => e.ValueKind == JsonValueKind.Object, "벤치 결과", TimeSpan.FromMinutes(4)).GetProperty("data");
            app.WaitExit(TimeSpan.FromSeconds(30));
            Assert.False(bench.TryGetProperty("error", out var err), "벤치 오류: " + err);
            invalid = bench.GetProperty("segments").EnumerateArray()
                .Where(s => s.GetProperty("Samples").GetInt32() < s.GetProperty("Seconds").GetDouble() * FrameBudget.MinSamplesPerSecond)
                .Select(s => s.GetProperty("Segment").GetString()).FirstOrDefault();
            if (invalid is null) break;   // 표본이 모자라면 측정 무효로 1회 다시 (설계 5.9)
        }
        Record("bench", new { bench, invalidSegment = invalid });
        Assert.True(invalid is null, "표본이 모자란 구간 (2회 연속 무효): " + invalid);

        var problems = new List<string>();
        foreach (var s in bench.GetProperty("segments").EnumerateArray())
        {
            string name = s.GetProperty("Segment").GetString()!;
            double p95 = s.GetProperty("P95").GetDouble(), max = s.GetProperty("Max").GetDouble();
            int over = s.GetProperty("Over33").GetInt32();
            if (name.StartsWith("idle", StringComparison.Ordinal) && p95 > FrameBudget.IdleP95) problems.Add($"{name} 95백분위 {p95} ms > {FrameBudget.IdleP95}");
            if (name.StartsWith("transition", StringComparison.Ordinal) && max > FrameBudget.TransitionMax) problems.Add($"{name} 최대 {max} ms > {FrameBudget.TransitionMax}");
            if (name.StartsWith("transition", StringComparison.Ordinal) && over > FrameBudget.TransitionHitches) problems.Add($"{name} 33 ms 초과 {over}회 > {FrameBudget.TransitionHitches}");
        }
        if (bench.GetProperty("fallbacks").GetInt32() != 0) problems.Add("원형 전환 대체(페이드)가 일어났다");
        if (bench.GetProperty("unclippedNewThemeFrames").GetInt32() != 0) problems.Add("새 테마 색이 클립 없이 그려진 렌더링 콜백이 있다");
        var left = bench.GetProperty("leftovers");
        if (left.GetProperty("snapshotLayers").GetInt32() != 0) problems.Add("찍은 화면이 남았다");
        if (left.GetProperty("repeatsAtEnd").GetInt32() != left.GetProperty("repeatsAtStart").GetInt32()) problems.Add($"반복 모션 수가 달라졌다 {left.GetProperty("repeatsAtStart")}→{left.GetProperty("repeatsAtEnd")}");
        if (left.GetProperty("subscribersAtEnd").GetInt32() != left.GetProperty("subscribersAtStart").GetInt32()) problems.Add("모드 구독자 수가 늘었다");
        var mem = bench.GetProperty("memory");
        if (mem.GetProperty("plateauGrowthMb").GetDouble() > FrameBudget.PlateauGrowthMb) problems.Add($"전환 22→62회 사이 메모리 증가 {mem.GetProperty("plateauGrowthMb")} MB > {FrameBudget.PlateauGrowthMb}");
        if (mem.GetProperty("at62Mb").GetDouble() > FrameBudget.PrivateLimitMb) problems.Add($"전용 메모리 {mem.GetProperty("at62Mb")} MB > {FrameBudget.PrivateLimitMb}");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ---------------- 실측 대비 (7.3) ----------------

    public static TheoryData<string> Themes => new() { "dark", "light" };

    [Theory, MemberData(nameof(Themes))]
    public void MeasuredContrastOnGlassMeetsTheThresholds(string theme)
    {
        var palette = theme == "dark" ? Palette.Dark : Palette.Light;
        string[] common = ["--freeze-motion", "--section", "contrast", "--theme", theme];
        Bitmap shown, hidden;
        var boxes = new Dictionary<string, Rectangle>();
        string[] ids = ["contrast.content.ink", "contrast.content.muted", "contrast.control.ink", "contrast.control.muted", "contrast.float.ink", "contrast.dialog.ink", "contrast.dialog.muted"];
        using (var app = Showcase(common))
        {
            app.WaitDiag(d => d.GetProperty("section").ToString(), s => s == "contrast", "대비 시험 장면");
            Thread.Sleep(600);
            foreach (var id in ids) boxes[id] = app.ToClient(app.Find(id).Current.BoundingRectangle);
            shown = app.CaptureBitmap();
            app.Save(shown, $"g5-contrast-{theme}-text");
            Assert.Equal(0, app.Close());
        }
        using (var app = Showcase([.. common, "--hide-measured-text"]))
        {
            app.WaitDiag(d => d.GetProperty("section").ToString(), s => s == "contrast", "대비 시험 장면 (글자 숨김)");
            Thread.Sleep(600);
            hidden = app.CaptureBitmap();
            app.Save(hidden, $"g5-contrast-{theme}-bg");
            Assert.Equal(0, app.Close());
        }
        var results = new List<object>();
        var problems = new List<string>();
        foreach (var id in ids)
        {
            var fg = id.EndsWith(".ink", StringComparison.Ordinal) ? palette.Ink : palette.Muted;
            var r = MeasureContrast(hidden, boxes[id], fg, threshold: 4.5);
            results.Add(new { id, r.CellP10Min, r.P1, r.LargestFailingBlob, r.Cells });
            if (r.CellP10Min < 4.5) problems.Add($"{id}: 칸별 10백분위 최저 {r.CellP10Min:0.00} < 4.5");
            if (r.LargestFailingBlob > 12) problems.Add($"{id}: 기준 미달 연결 영역 {r.LargestFailingBlob} px > 12");
        }
        shown.Dispose();
        hidden.Dispose();
        Record("contrast-" + theme, results);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private sealed record ContrastMeasure(double CellP10Min, double P1, int LargestFailingBlob, int Cells);

    /// <summary>
    /// 글자 없는 캡처에서 글자 요소 경계 + 4 px 영역의 픽셀마다 (지정 글자색, 그 픽셀)의 대비를 구한다 (설계 7.3).
    /// 칸(글자 높이 크기 정사각) 마다 10백분위, 기준 미달 픽셀의 4방향 연결 덩어리 크기, 전체 1백분위.
    /// </summary>
    private static ContrastMeasure MeasureContrast(Bitmap bg, Rectangle box, Rgba fg, double threshold)
    {
        var r = Rectangle.Intersect(Rectangle.Inflate(box, 4, 4), new Rectangle(Point.Empty, bg.Size));
        var px = ImageCompare.Pixels(bg, r);
        var ratio = new double[r.Width * r.Height];
        for (int i = 0; i < ratio.Length; i++)
            ratio[i] = Contrast.Ratio(fg, new Rgba(px[i * 3 + 2], px[i * 3 + 1], px[i * 3]));
        int cell = Math.Max(4, box.Height);
        double cellMin = double.MaxValue;
        int cells = 0;
        for (int cy = 0; cy < r.Height; cy += cell)
            for (int cx = 0; cx < r.Width; cx += cell)
            {
                var vals = new List<double>();
                for (int y = cy; y < Math.Min(cy + cell, r.Height); y++)
                    for (int x = cx; x < Math.Min(cx + cell, r.Width); x++) vals.Add(ratio[y * r.Width + x]);
                vals.Sort();
                cellMin = Math.Min(cellMin, vals[(int)(vals.Count * 0.10)]);
                cells++;
            }
        var sorted = ratio.OrderBy(v => v).ToArray();
        // 기준 미달 픽셀의 4방향 연결 덩어리
        var seen = new bool[ratio.Length];
        int largest = 0;
        var stack = new Stack<int>();
        for (int i = 0; i < ratio.Length; i++)
        {
            if (seen[i] || ratio[i] >= threshold) continue;
            int size = 0;
            stack.Push(i);
            seen[i] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                size++;
                int x = k % r.Width, y = k / r.Width;
                foreach (var n in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (n.Item1 < 0 || n.Item2 < 0 || n.Item1 >= r.Width || n.Item2 >= r.Height) continue;
                    int j = n.Item2 * r.Width + n.Item1;
                    if (!seen[j] && ratio[j] < threshold) { seen[j] = true; stack.Push(j); }
                }
            }
            largest = Math.Max(largest, size);
        }
        return new ContrastMeasure(Math.Round(cellMin, 2), Math.Round(sorted[(int)(sorted.Length * 0.01)], 2), largest, cells);
    }

    // ---------------- 효과 줄이기 표면 (7.3) ----------------

    [Fact]
    public void ReducedEffectsSurfacesIgnoreWhatIsBehindThem()
    {
        Bitmap a, b;
        var panels = new List<Rectangle>();
        string[] common = ["--freeze-motion", "--section", "contrast", "--simulate-transparency-off", "--hide-measured-text"];
        using (var app = Showcase(common))
        {
            app.WaitDiag(d => d.GetProperty("material").ToString(), m => m == "opaque", "불투명 표면");
            Thread.Sleep(600);
            foreach (var id in new[] { "contrast.content", "contrast.control", "contrast.float", "contrast.dialog" })
                panels.Add(Rectangle.Inflate(app.ToClient(app.Find(id).Current.BoundingRectangle), -Radius_Inset, -Radius_Inset));
            a = app.CaptureBitmap();
            Assert.Equal(0, app.Close());
        }
        using (var app = Showcase([.. common, "--alt-pattern"]))
        {
            app.WaitDiag(d => d.GetProperty("material").ToString(), m => m == "opaque", "불투명 표면 (다른 무늬)");
            Thread.Sleep(600);
            b = app.CaptureBitmap();
            Assert.Equal(0, app.Close());
        }
        var diffs = panels.Select(p => ImageCompare.Diff(a, b, p)).ToArray();
        a.Dispose();
        b.Dispose();
        Record("reduced-surface", diffs.Select(d => new { d.Mean, d.P99 }));
        Assert.All(diffs, d => Assert.True(d.P99 <= 2, $"효과 줄이기 표면에 뒤 무늬가 비친다: {d}"));
    }

    /// <summary>둥근 모서리·테두리를 빼고 표면 안쪽만 비교하는 여백 (px)</summary>
    private const int Radius_Inset = 20;

    // ---------------- 원형 전환이 찍은 화면의 충실도 (5.4) ----------------

    public static TheoryData<string, string> FidelityCases => new()
    {
        { "dark", "overview" }, { "light", "overview" }, { "dark", "toasts" }, { "light", "toasts" },
    };

    [Theory, MemberData(nameof(FidelityCases))]
    public void SnapshotUsedByTheRevealMatchesTheWindow(string theme, string section)
    {
        using var app = Showcase("--freeze-motion", "--aurora", "on", "--theme", theme, "--section", section, "--selftest", "snapshot");
        var r = app.WaitSelfTest("snapshot");
        Thread.Sleep(300);
        using var window = app.CaptureBitmap();
        using var snap = new Bitmap(Path.Combine(app.UserData, r.GetProperty("file").GetString()!));
        using var snapSized = snap.Size == window.Size ? new Bitmap(snap) : new Bitmap(snap, window.Size);
        app.Save(snapSized, $"g5-snapshot-{theme}-{section}");
        var diff = ImageCompare.Diff(window, snapSized);
        Record($"fidelity-{theme}-{section}", new { diff.Mean, diff.P99, snapshot = new { w = snap.Width, h = snap.Height }, window = new { w = window.Width, h = window.Height } });
        Assert.True(diff.Mean <= 3 && diff.P99 <= 12, $"찍은 화면이 실제 창과 다르다: {diff}");
        Assert.Equal(0, app.Close());
    }

    // ---------------- 실행 중 테마 바꾸기 = 그 테마로 시작한 화면 ----------------

    [Theory]
    [InlineData("dark", "light")]
    [InlineData("light", "dark")]
    public void SwitchingThemeAtRuntimeLooksLikeStartingInIt(string from, string to)
    {
        // 원형 전환이 끝난 뒤 남는 것(빛 색, 유리 대체색, 브러시)이 처음부터 그 테마로 띄운 화면과 같아야 한다
        Bitmap switched, cold;
        using (var app = Showcase("--freeze-motion", "--theme", from))
        {
            app.WaitDiag(d => d.GetProperty("theme").ToString(), t => t == from, "시작 테마");
            app.Select("toolbar.theme." + to);
            app.WaitDiag(d => (d.GetProperty("theme").ToString(), d.GetProperty("inTransition").GetBoolean(), d.GetProperty("transitions").GetInt32()),
                x => x.Item1 == to && !x.Item2 && x.Item3 >= 1, "원형 전환 끝");
            Thread.Sleep(800);
            switched = app.CaptureBitmap();
            app.Save(switched, $"g5-switched-{from}-to-{to}");
            Assert.Equal(0, app.Close());
        }
        using (var app = Showcase("--freeze-motion", "--theme", to))
        {
            app.WaitDiag(d => d.GetProperty("theme").ToString(), t => t == to, "처음부터 그 테마");
            Thread.Sleep(800);
            cold = app.CaptureBitmap();
            Assert.Equal(0, app.Close());
        }
        var diff = ImageCompare.Diff(switched, cold);
        switched.Dispose();
        cold.Dispose();
        Record($"switch-{from}-to-{to}", new { diff.Mean, diff.P99 });
        Assert.True(diff.Mean <= 1 && diff.P99 <= 8, $"실행 중에 바꾼 테마가 처음부터 띄운 화면과 다르다: {diff}");
    }

    // ---------------- 전환 경계 프레임 (7.3, G4 M2 1라운드 지적 7) ----------------

    [Theory]
    [InlineData("dark", "light")]
    [InlineData("light", "dark")]
    public void RevealNeverShowsTheNewThemeOutsideTheCircle(string from, string to)
    {
        // 원형은 도구 막대의 테마 단추에서 퍼진다. 가장 먼 왼쪽 아래 구석은 전환이 끝나기 직전까지 이전 테마여야 한다.
        // 시작 직후부터 창 캡처를 촘촘히 연속으로 찍어 구석의 색을 본다. 모든 프레임을 찍지는 못하므로 순간 노출을 완전히 배제하지는 못한다.
        using var app = Showcase("--freeze-motion", "--theme", from);
        app.WaitDiag(d => d.GetProperty("theme").ToString(), t => t == from, "시작 테마");
        Thread.Sleep(500);
        var corner = new Rectangle(8, NetPulse.Design.Tokens.Size.WindowHeight - 28, 20, 20);
        using var before = app.CaptureBitmap();
        var oldCorner = ImageCompare.Pixels(before, corner);
        var frames = new List<(double Ms, double DiffFromOld)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        app.Select("toolbar.theme." + to);
        // 시작부터 전환이 끝난 뒤(원형 550 ms + 찍기)까지 이어서 찍는다: 끝 경계(클립 해제·아크릴 복귀)도 본다 (G4 M2 2라운드 지적 2)
        while (sw.ElapsedMilliseconds < 1100)
        {
            using var f = app.CaptureBitmap();
            var px = ImageCompare.Pixels(f, corner);
            frames.Add((sw.Elapsed.TotalMilliseconds, px.Zip(oldCorner, (x, y) => Math.Abs(x - y)).Average()));
        }
        app.WaitDiag(d => (d.GetProperty("inTransition").GetBoolean(), d.GetProperty("transitions").GetInt32()), x => !x.Item1 && x.Item2 >= 1, "전환 끝");
        Thread.Sleep(300);
        using var after = app.CaptureBitmap();
        double finalDiff = ImageCompare.Pixels(after, corner).Zip(oldCorner, (x, y) => Math.Abs(x - y)).Average();
        // 구석은 "이전 테마 → 새 테마"로 한 번만 바뀌어야 한다. 중간(합성 중) 값은 이전에도 새것에도 가깝지 않은 프레임으로 센다.
        var states = frames.Select(f => f.DiffFromOld <= 12 ? 'o' : Math.Abs(f.DiffFromOld - finalDiff) <= 12 ? 'n' : 'm').ToArray();
        string sequence = new string(states);
        int firstNew = Array.IndexOf(states, 'n');
        Record($"boundary-{from}-to-{to}", new { frames = frames.Select(f => new { ms = Math.Round(f.Ms), diff = Math.Round(f.DiffFromOld, 1) }), finalDiff, sequence });
        Assert.True(frames.Count >= 10, $"연속 캡처가 너무 적다: {frames.Count}");
        Assert.True(finalDiff > 40, $"전환이 끝났는데 구석이 새 테마가 아니다 (차이 {finalDiff:0.0})");
        Assert.True(firstNew > 0, "구석이 처음부터 새 테마였거나 끝까지 바뀌지 않았다: " + sequence);
        Assert.True(frames[firstNew].Ms >= 300, $"구석이 너무 일찍 새 테마가 됐다 ({frames[firstNew].Ms:0} ms): " + sequence);
        // 한 번 새 테마가 된 뒤 이전 테마나 중간값으로 돌아가면 끝 경계의 깜빡임이다
        Assert.True(states.Skip(firstNew).All(s => s == 'n'), "끝 경계에서 깜빡였다 (o=이전, n=새, m=중간): " + sequence);
        // 원형 가장자리가 구석을 지나는 동안은 중간값이 나온다(감속 곡선이라 끝에서 느리다). 차이는 줄어들지 않고 늘기만 해야 한다.
        for (int i = 1; i < frames.Count; i++)
            Assert.True(frames[i].DiffFromOld >= frames[i - 1].DiffFromOld - 6,
                $"구석이 새 테마 쪽으로 가다 되돌아갔다 ({frames[i - 1].Ms:0}→{frames[i].Ms:0} ms): " + sequence);
        Assert.Equal(0, app.Close());
    }

    // ---------------- 캡처 매트릭스 (7.4) ----------------

    public static TheoryData<string, string> Matrix()
    {
        var d = new TheoryData<string, string>();
        foreach (var t in new[] { "dark", "light" })
        {
            d.Add($"01-overview-{t}-normal", $"--section overview --theme {t}");
            d.Add($"03-overview-{t}-problem", $"--section overview --theme {t} --state problem");
            d.Add($"05-overview-{t}-aurora", $"--section overview --theme {t} --aurora on");
            d.Add($"07-materials-{t}", $"--section materials --theme {t}");
            d.Add($"07-contrast-{t}", $"--section contrast --theme {t}");
            d.Add($"09-buttons-{t}", $"--section buttons --theme {t}");
            d.Add($"11-cards-{t}", $"--section cards --theme {t}");
            d.Add($"13-dialog-{t}", $"--section dialog --theme {t} :open-dialog");
            d.Add($"15-toasts-{t}", $"--section toasts --theme {t}");
            d.Add($"17-chart-{t}", $"--section chart --theme {t}");
            d.Add($"19-overview-{t}-reduced", $"--section overview --theme {t} --simulate-transparency-off");
            foreach (var s in new[] { "overview", "buttons", "cards", "toasts", "chart", "materials" })
                d.Add($"23-{s}-{t}-150", $"--section {s} --theme {t} --ui-scale 1.5");
            d.Add($"25-dialog-{t}-150", $"--section dialog --theme {t} --ui-scale 1.5 :open-dialog");
        }
        d.Add("21-overview-hc-aquatic", "--section overview --simulate-high-contrast aquatic");
        d.Add("22-overview-hc-desert", "--section overview --simulate-high-contrast desert");
        d.Add("22-overview-dark-reducedmotion", "--section motion --simulate-animations-off");
        foreach (var p in new[] { "0", "0.25", "0.5", "0.75", "1" })
            d.Add($"37-reveal-{p}", $"--section overview --theme dark --reveal-hold {p} :switch-light");
        return d;
    }

    [Theory, MemberData(nameof(Matrix))]
    public void CaptureMatrix(string name, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var appArgs = parts.Where(p => !p.StartsWith(':')).ToList();
        appArgs.Add("--freeze-motion");
        using var app = Showcase([.. appArgs]);
        string section = appArgs[appArgs.IndexOf("--section") + 1];
        app.WaitDiag(d => d.GetProperty("section").ToString(), s => s == section, "절 " + section);
        if (parts.Contains(":open-dialog"))
        {
            app.Invoke("dialog.open");
            app.WaitDiag(d => d.GetProperty("dialogOpen").GetBoolean(), o => o, "대화상자 열림");
        }
        if (parts.Contains(":switch-light"))
        {
            app.Select("toolbar.theme.light");
            app.WaitDiag(d => d.GetProperty("inTransition").GetBoolean(), t => t, "원형 전환 진행 중 (진행도 고정)");
        }
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        var path = app.Save(shot, "matrix-" + name);
        var diag = app.Diag();
        Record("matrix-" + name, new { name, args, file = Path.GetFileName(path), w = shot.Width, h = shot.Height, diag });
        Assert.Equal((NetPulse.Design.Tokens.Size.WindowWidth, NetPulse.Design.Tokens.Size.WindowHeight), (shot.Width, shot.Height));
        Assert.Equal(0, app.Close());
    }
}
