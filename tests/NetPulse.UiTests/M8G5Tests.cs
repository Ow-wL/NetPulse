using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetPulse.Core.Analysis;
using NetPulse.Core.Live;
using NetPulse.Core.Records;
using NetPulse.Core.Report;
using NetPulse.Design;
using NetPulse.Design.Tokens;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M8 G5 추가 (설계 7.4·5절): 고대비 둘 × 미리 보기, 효과·움직임 줄이기, 홈 → 리포트 전환, 종이 면 실측 대비,
/// 리포트 bench(문서 만들기 시간·크기·메모리, 미리 보기 프레임), 실제 HTML을 Windows 기본 Edge로 그린 화면·A4 인쇄.
/// </summary>
public sealed class M8G5Tests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static M5MatrixTests.Scene Scene(string id) => M5MatrixTests.Scenes("m8-scenes.txt").Single(s => s.Id == id);

    public static TheoryData<string, string, string> Extras() => new()
    {
        { "m8-report-preview-hc-aquatic", "report-preview", "--simulate-high-contrast aquatic" },
        { "m8-report-preview-hc-desert", "report-preview", "--simulate-high-contrast desert" },
        { "m8-report-figure-hc-aquatic", "report-preview-figure", "--simulate-high-contrast aquatic" },
        { "m8-report-preview-dark-reduced", "report-preview", "--theme dark --simulate-transparency-off" },
        { "m8-report-preview-light-reduced", "report-preview", "--theme light --simulate-transparency-off" },
        { "m8-report-preview-dark-reducedmotion", "report-preview-figure", "--theme dark --simulate-animations-off" },
        { "m8-report-preview-light-reducedmotion", "report-preview-figure", "--theme light --simulate-animations-off" },
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
        var diag = app.Diag();
        Record("matrix-" + name, new { name, scene = sceneId, extra, file = Path.GetFileName(path), layoutProblems = problems, diag });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        if (name.Contains("-hc-", StringComparison.Ordinal))
            Assert.True(diag.GetProperty("screenDiag").GetProperty("highContrast").GetBoolean(), "고대비에서는 미리 보기가 시스템 색을 따라야 합니다");
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        Assert.Equal(0, app.Close());
    }

    public static TheoryData<string> Progress => new() { "0", "0.5", "1" };

    /// <summary>홈 → 리포트 교차 페이드를 진행도에 멈춰 찍는다.</summary>
    [Theory, MemberData(nameof(Progress))]
    public void HomeToReportCrossfade(string progress)
    {
        var userData = AppDriver.NewUserData();
        try
        {
            M5Fixtures.Prepare(userData, "rec-mixed");
            using var app = AppDriver.LaunchWith(userData, [.. M5Fixtures.Args("--demo steady --screen home"), "--theme", "dark", "--motion-hold", progress]);
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "home", "홈");
            app.Select("nav.report");
            app.WaitDiag(d => M5Fixtures.Str(d, "screen"), s => s == "report", "리포트 화면");
            Thread.Sleep(800);
            using var shot = app.CaptureBitmap();
            string name = $"m8-transition-crossfade-{progress}";
            var path = app.Save(shot, "matrix-" + name);
            Record("matrix-" + name, new { name, kind = "crossfade", progress, file = Path.GetFileName(path), diag = app.Diag() });
            Assert.Equal(0, app.Close());
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    public static TheoryData<string> Themes => new() { "dark", "light" };

    /// <summary>미리 보기 종이 면 글자의 실측 대비 (M2 방식): 글자 있음/없음 두 장. 종이는 두 테마 모두 문서 팔레트.</summary>
    [Theory, MemberData(nameof(Themes))]
    public void MeasuredContrastOnThePaper(string theme)
    {
        var ink = Rgba.Hex(ReportPalette.Document.Ink);
        string[] ids = ["report.field.summary.grade", "report.field.summary.sentence", "report.field.summary.start"];   // 스크롤 영역 안에 다 보이는 칸만
        var boxes = new Dictionary<string, Rectangle>();
        Bitmap? hidden = null;
        foreach (var hide in new[] { false, true })
        {
            string[] extra = ["--theme", theme];
            using var app = M5MatrixTests.Open(Scene("report-preview"), hide ? [.. extra, "--hide-measured-text"] : extra);
            app.CloseToasts();
            app.ScrollTo("report.scroll", 15);
            Thread.Sleep(1600);
            if (!hide) foreach (var id in ids) boxes[id] = app.ToClient(app.Find(id).Current.BoundingRectangle);
            var shot = app.CaptureBitmap();
            app.Save(shot, $"g5-report-contrast-{theme}-{(hide ? "bg" : "text")}");
            if (hide) hidden = shot; else shot.Dispose();
            Assert.Equal(0, app.Close());
        }
        var results = new List<object>();
        var problems = new List<string>();
        foreach (var id in ids)
        {
            var r = G5Tests.MeasureContrast(hidden!, boxes[id], ink, threshold: 4.5);
            results.Add(new { id, r.CellP10Min, r.P1, r.LargestFailingBlob, r.Cells });
            if (r.CellP10Min < 4.5) problems.Add($"{id}: 칸별 10백분위 최저 {r.CellP10Min:0.00} < 4.5");
            if (r.LargestFailingBlob > 12) problems.Add($"{id}: 기준 미달 연결 영역 {r.LargestFailingBlob} px > 12");
        }
        hidden!.Dispose();
        Record("contrast-report-" + theme, results);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ---------------- bench ----------------

    /// <summary>
    /// 리포트 화면 bench (설계 5절 report-preview): 12시간 기록과 최악 사례의 미리 보기 프레임. 기록 화면과 같은 프레임 기준, 최고 전용 메모리 400 MB.
    /// 예외 하나: 최악 사례 열기(report-worst, 대상 8·12시간 기록을 캐시 없이 읽고 분석)는 최대 80 ms·33 ms 초과 6회 (사용자 결정, DECISIONS "M8 미리 보기 bench 기준").
    /// </summary>
    [Fact]
    public void ReportPreviewStaysWithinBudget()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            M7Fixtures.BenchReport(userData);
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "home", "--bench-report");
            var bench = AppDriver.WaitFor(() => app.ReadLog().FirstOrDefault(e => e.GetProperty("kind").GetString() == "bench"),
                e => e.ValueKind == JsonValueKind.Object, "report bench", TimeSpan.FromMinutes(10)).GetProperty("data");
            app.WaitExit(TimeSpan.FromSeconds(60));
            Record("bench-report", bench);
            Assert.False(bench.TryGetProperty("error", out var err), "bench 실패: " + err);
            var problems = new List<string>();
            string[] required = ["report-enter", "report-scroll", "report-options", "report-worst", "report-worst-scroll"];
            var seen = bench.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("Segment").GetString()!).ToArray();
            foreach (var r in required.Except(seen)) problems.Add($"{r} 구간이 없습니다");
            foreach (var s in bench.GetProperty("segments").EnumerateArray())
            {
                string seg = s.GetProperty("Segment").GetString()!;
                if (s.GetProperty("Samples").GetInt32() < 10) problems.Add($"{seg} 프레임 표본 {s.GetProperty("Samples").GetInt32()}개 (10개 미만)");
                double max = s.GetProperty("Max").GetDouble();
                int over = s.GetProperty("Over33").GetInt32();
                var (maxLimit, overLimit) = seg == "report-worst" ? (WorstOpenMax, WorstOpenHitches) : (FrameBudget.TransitionMax, FrameBudget.TransitionHitches);
                if (max > maxLimit || over > overLimit) problems.Add($"{seg} 최대 {max:0.0} ms, 33 ms 초과 {over}회 (기준 {maxLimit} ms·{overLimit}회)");
            }
            long peak = bench.GetProperty("peakPrivateMb").GetInt64();
            if (peak > 400) problems.Add($"최고 전용 메모리 {peak} MB > 400 (실측값으로 기준을 다시 정할 것)");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>최악 사례 열기 구간의 기준 (사용자 결정 2026-11-16 "최악만 80ms·6회로").</summary>
    private const double WorstOpenMax = 80;
    private const int WorstOpenHitches = 6;

    private static long PrivateMb() => Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024);

    /// <summary>
    /// 문서 만들기 bench (설계 5절 report-build): 보통(12시간)·최악 사례, 구간(읽기·분석 / 문서 / 안전망 / HTML)마다 5번.
    /// 기준: 문서 만들기 5번 중 최대(95백분위 대신) 보통 2초·최악 5초, HTML 보통 3 MB·최악 5 MB, 최고 전용 메모리 400 MB.
    /// </summary>
    [Fact]
    public void ReportBuildStaysWithinBudget()
    {
        var userData = AppDriver.NewUserData();
        try
        {
            var (typical, worst) = M7Fixtures.BenchReport(userData);
            var roots = M5Fixtures.RecordRoots(userData);
            long peak = PrivateMb();
            var cases = new List<object>();
            var problems = new List<string>();
            foreach (var (name, id, buildLimit, sizeLimit) in new[] { ("typical", typical, 2000L, 3L << 20), ("worst", worst, 5000L, 5L << 20) })
            {
                var read = new List<long>();
                var build = new List<long>();
                var check = new List<long>();
                var html = new List<long>();
                long bytes = 0;
                int episodes = 0;
                for (int i = 0; i < 5; i++)
                {
                    var w = Stopwatch.StartNew();
                    var record = RecordReader.Read(RecordReader.FindSegments(id, roots));
                    var a = MeasurementAnalysis.Analyze(record);
                    read.Add(w.ElapsedMilliseconds);
                    episodes = a.Episodes.Count;
                    w.Restart();
                    var doc = ReportBuilder.Build(new ReportInput("k", "s", record, a, RecordLifecycle.Ended, [], new ReportOptions(IncludeMemos: true, EpisodeDetails: ReportOptions.MaxEpisodeDetails), M5Fixtures.DemoNow, "bench"));
                    build.Add(w.ElapsedMilliseconds);
                    w.Restart();
                    var leaks = doc.Guard!.Check(doc);
                    check.Add(w.ElapsedMilliseconds);
                    Assert.Empty(leaks);
                    w.Restart();
                    var text = ReportHtml.Write(doc);
                    Assert.Empty(ReportHtml.Verify(text));
                    html.Add(w.ElapsedMilliseconds);
                    bytes = System.Text.Encoding.UTF8.GetByteCount(text);
                    peak = Math.Max(peak, PrivateMb());
                }
                cases.Add(new { name, episodes, readAnalyzeMs = read, buildMs = build, checkMs = check, htmlMs = html, bytes });
                if (build.Max() > buildLimit) problems.Add($"{name}: 문서 만들기 최대 {build.Max()} ms > {buildLimit}");
                if (bytes > sizeLimit) problems.Add($"{name}: HTML {bytes} B > {sizeLimit}");
            }
            Record("bench-report-build", new { cases, peakPrivateMb = peak });
            if (peak > 400) problems.Add($"최고 전용 메모리 {peak} MB > 400");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    // ---------------- 실제 HTML (Edge) ----------------

    private static string? EdgePath() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
    }.FirstOrDefault(File.Exists);

    private static void RunEdge(string edge, string profile, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(edge) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--user-data-dir=" + profile }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(90))) { p.Kill(true); throw new TimeoutException("Edge가 제한시간 안에 끝나지 않았습니다 (미완료)"); }
    }

    /// <summary>
    /// 내보낸 HTML(보통·최악)을 Windows 기본 Edge의 헤드리스 모드로 그린다 (설계 7.4, 구현 중 결정 5): 화면 그림 1280×2000과 A4 PDF.
    /// 판정: 그림이 비어 있지 않음(색 10개 이상), PDF 쪽 수가 범위 안. Edge가 없거나 실패하면 미완료로 실패 (설치하지 않는다).
    /// 쪽마다 표 머리 반복은 PDF 글을 뽑지 않고 CSS 규칙(thead table-header-group)과 PDF를 G4 시각 검토에 내는 것으로 본다.
    /// </summary>
    [Fact]
    public void ExportedHtmlRendersInEdgeOnScreenAndOnA4()
    {
        var edge = EdgePath();
        Assert.True(edge is not null, "미완료: Windows 기본 Edge(msedge.exe)를 찾지 못했습니다 (새 도구를 설치하지 않음)");
        var version = FileVersionInfo.GetVersionInfo(edge!).ProductVersion;
        var userData = AppDriver.NewUserData();
        var profile = Directory.CreateTempSubdirectory("np-edge-").FullName;
        try
        {
            var (typical, worst) = M7Fixtures.BenchReport(userData);
            var roots = M5Fixtures.RecordRoots(userData);
            var outputs = new List<object>();
            var problems = new List<string>();
            foreach (var (name, id, minPages, maxPages) in new[] { ("typical", typical, 2, 40), ("worst", worst, 5, 400) })
            {
                var record = RecordReader.Read(RecordReader.FindSegments(id, roots));
                var a = MeasurementAnalysis.Analyze(record);
                var doc = ReportBuilder.Build(new ReportInput("k", "s", record, a, RecordLifecycle.Ended, [], new ReportOptions(), M5Fixtures.DemoNow, "edge"));
                var rendered = ReportExport.Render(doc);
                Assert.True(rendered.Ok, name + ": 내보낼 HTML을 만들지 못했습니다");
                var htmlPath = Path.Combine(AppDriver.OutputDirectory, $"edge-{name}.html");
                File.WriteAllText(htmlPath, rendered.Html!, new System.Text.UTF8Encoding(false));
                var png = Path.Combine(AppDriver.OutputDirectory, $"edge-{name}-screen.png");
                var pdf = Path.Combine(AppDriver.OutputDirectory, $"edge-{name}-a4.pdf");
                foreach (var f in new[] { png, pdf }) if (File.Exists(f)) File.Delete(f);
                var url = new Uri(htmlPath).AbsoluteUri;
                string[] screenArgs = ["--window-size=1280,2000", "--hide-scrollbars", "--screenshot=" + png, url];
                string[] printArgs = ["--no-pdf-header-footer", "--print-to-pdf=" + pdf, url];
                RunEdge(edge!, profile, screenArgs);
                RunEdge(edge!, profile, printArgs);
                Assert.True(File.Exists(png) && File.Exists(pdf), $"미완료: Edge가 {name} 결과를 만들지 못했습니다");
                int colors;
                using (var bmp = new Bitmap(png))
                {
                    var set = new HashSet<int>();
                    for (int y = 0; y < bmp.Height; y += 7)
                        for (int x = 0; x < bmp.Width; x += 7) set.Add(bmp.GetPixel(x, y).ToArgb());
                    colors = set.Count;
                    if (bmp.Width != 1280) problems.Add($"{name}: 화면 그림 폭 {bmp.Width}");
                }
                var pdfText = File.ReadAllText(pdf, System.Text.Encoding.Latin1);
                int pages = Regex.Matches(pdfText, @"/Type\s*/Page(?![a-z])").Count;
                if (colors < 10) problems.Add($"{name}: 화면 그림이 비어 있음 (색 {colors}개)");
                if (pages < minPages || pages > maxPages) problems.Add($"{name}: PDF {pages}쪽 (기대 {minPages}~{maxPages})");
                outputs.Add(new { name, html = Path.GetFileName(htmlPath), png = Path.GetFileName(png), pdf = Path.GetFileName(pdf), colors, pages, bytes = new FileInfo(htmlPath).Length });
            }
            Record("html-render", new
            {
                edge = "msedge.exe", version, args = new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--user-data-dir=(격리 임시 프로필)" },
                window = "1280x2000", paper = "A4 세로, 여백 0.4in (문서의 @page)", outputs,
            });
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally
        {
            AppDriver.DeleteUserData(userData);
            try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
