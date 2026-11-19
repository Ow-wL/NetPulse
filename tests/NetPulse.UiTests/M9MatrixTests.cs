using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// M9 G5 (설계 7.4): 설정·처음 숨김 안내·프로필 TCP 장면 × 테마 × 배율, 고대비·효과·움직임 줄이기,
/// 트레이 메뉴(그 팝업 창만 캡처), 아이콘 대비, 데모 캡처의 UIA 글에 실제 경로·사용자 이름이 없는지.
/// </summary>
public sealed class M9MatrixTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    private static void Record(string name, object result) =>
        File.WriteAllText(Path.Combine(G5Dir, name + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

    private static M5MatrixTests.Scene Scene(string id) => M5MatrixTests.Scenes("m9-scenes.txt").Single(s => s.Id == id);

    public static TheoryData<string, string, string> Matrix()
    {
        var d = new TheoryData<string, string, string>();
        var only = Environment.GetEnvironmentVariable("NETPULSE_SCENES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var s in M5MatrixTests.Scenes("m9-scenes.txt").Where(s => only is not { Length: > 0 } || only.Contains(s.Id)))
            foreach (var t in new[] { "dark", "light" })
                foreach (var scale in new[] { "1", "1.5" })
                    d.Add(s.Id, t, scale);
        return d;
    }

    /// <summary>공개 전 검사와 같은 뜻: 데모 캡처의 글에 이 PC의 사용자 이름·호스트 이름·드라이브 절대 경로·UNC가 없어야 한다 (B2 [1-13]).</summary>
    private static IReadOnlyList<string> PrivateText(AutomationElement window)
    {
        var problems = new List<string>();
        var user = Environment.UserName;
        var host = Environment.MachineName;
        var abs = new Regex(@"(?<![%\w])[A-Za-z]:\\|\\\\[^\\\s]+\\");
        foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string text;
            try { text = e.Current.Name ?? ""; }
            catch (ElementNotAvailableException) { continue; }
            if (text.Length == 0 || e.Current.AutomationId == "diag.state") continue;   // 진단 값은 시험용 (사용자 폴더 경로 없음은 따로 본다)
            if (text.Contains(user, StringComparison.OrdinalIgnoreCase) || text.Contains(host, StringComparison.OrdinalIgnoreCase) || abs.IsMatch(text))
                problems.Add($"{e.Current.AutomationId}: 실제 경로·이름이 보임");
        }
        return problems;
    }

    /// <summary>장면을 끝낸다. 처음 숨김 안내는 [측정 멈추고 종료]로 (창 닫기가 안내에 막혀 있다).</summary>
    private static void Finish(AppDriver app, string id)
    {
        if (id == "hint-dialog")
        {
            app.Invoke("dialog.alternate");
            Assert.Equal(0, app.WaitExit(TimeSpan.FromSeconds(20)));
        }
        else Assert.Equal(0, app.Close());
    }

    [Theory, MemberData(nameof(Matrix))]
    public void SceneLooksRightAndFits(string id, string theme, string scale)
    {
        using var app = M5MatrixTests.Open(Scene(id), "--theme", theme, "--ui-scale", scale);
        if (!id.Contains("confirm", StringComparison.Ordinal) && id != "hint-dialog") app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        string name = $"m9-{id}-{theme}-{(scale == "1" ? "100" : "150")}";
        var path = app.Save(shot, "matrix-" + name);
        var problems = M5MatrixTests.LayoutProblems(app.LayoutNow());
        var diag = app.Diag();
        var privacy = PrivateText(app.Window);
        Record("matrix-" + name, new { name, scene = id, theme, scale, file = Path.GetFileName(path), w = shot.Width, h = shot.Height, layoutProblems = problems, privacy, diag });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(privacy.Count == 0, string.Join("\n", privacy));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        Finish(app, id);
    }

    public static TheoryData<string, string, string> Extras() => new()
    {
        { "m9-settings-normal-hc-aquatic", "settings-normal", "--simulate-high-contrast aquatic" },
        { "m9-settings-normal-hc-desert", "settings-normal", "--simulate-high-contrast desert" },
        { "m9-settings-moving-hc-aquatic", "settings-moving", "--simulate-high-contrast aquatic" },
        { "m9-hint-dialog-hc-desert", "hint-dialog", "--simulate-high-contrast desert" },
        { "m9-settings-normal-dark-reduced", "settings-normal", "--theme dark --simulate-transparency-off" },
        { "m9-settings-moving-light-reduced", "settings-moving", "--theme light --simulate-transparency-off" },
        { "m9-hint-dialog-dark-reducedmotion", "hint-dialog", "--theme dark --simulate-animations-off" },
        { "m9-settings-moving-light-reducedmotion", "settings-moving", "--theme light --simulate-animations-off" },
    };

    [Theory, MemberData(nameof(Extras))]
    public void ExtraSceneRenders(string name, string sceneId, string extra)
    {
        using var app = M5MatrixTests.Open(Scene(sceneId), extra.Split(' '));
        if (sceneId != "hint-dialog") app.CloseToasts();
        Thread.Sleep(700);
        using var shot = app.CaptureBitmap();
        var path = app.Save(shot, "matrix-" + name);
        var problems = M5MatrixTests.LayoutProblems(app.LayoutNow());
        Record("matrix-" + name, new { name, scene = sceneId, extra, file = Path.GetFileName(path), layoutProblems = problems, diag = app.Diag() });
        Assert.Equal((Size.WindowWidth, Size.WindowHeight), (shot.Width, shot.Height));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(app.ReadLog().All(e => e.GetProperty("kind").GetString() is not ("unhandled" or "renderFailed" or "diagFailed")), "앱 기록에 오류");
        Finish(app, sceneId);
    }

    /// <summary>트레이 메뉴: 데스크톱 UIA에서 앱 프로세스의 팝업 메뉴 창을 찾아 그 창만 캡처한다 (화면 영역 캡처 아님, A7).</summary>
    [Fact]
    public void TrayMenuIsCapturedAsItsOwnWindow()
    {
        var userData = AppDriver.NewUserData();
        M5Fixtures.SaveProfile(userData, "steady", TimeSpan.FromMinutes(60));
        try
        {
            using var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--start", "--demo-hold", "5", "--close-to-tray", "on");
            app.WaitDiag(d => d.GetProperty("host").GetString(), h => h == "Measuring", "측정 중");
            app.PostTray(AppDriver.WmContextMenu);
            var menu = app.TrayMenu();
            Assert.Equal(app.ProcessId, menu.Current.ProcessId);
            var items = AppDriver.WaitFor(() => app.TrayMenuItems(), i => i.Count >= 3, "메뉴 항목");
            // 메뉴는 나타나는 효과 동안 빈 그림으로 찍힐 수 있다: 글자가 그려질 때까지(색 수) 다시 찍고, PW_RENDERFULLCONTENT(2)와 WM_PRINT 경로(0)를 번갈아
            var hwnd = new IntPtr(menu.Current.NativeWindowHandle);
            Bitmap? shot = null;
            int colors = 0;
            uint flags = 0;
            try
            {
                AppDriver.WaitFor(() =>
                {
                    shot?.Dispose();
                    flags = flags == 2 ? 0u : 2u;
                    shot = AppDriver.CaptureWindow(hwnd, flags);
                    colors = DistinctColors(shot);
                    return colors;
                }, n => n >= 8, "메뉴 글자가 그려진 캡처", TimeSpan.FromSeconds(5));
                shot!.Save(Path.Combine(AppDriver.OutputDirectory, "m9-tray-menu.png"), ImageFormat.Png);
                Record("m9-tray-menu", new { items = items.Select(i => new { i.Name, i.Enabled }), w = shot.Width, h = shot.Height, colors, printFlags = flags });
                Assert.True(shot.Width > 40 && shot.Height > 40, "메뉴 캡처 크기");
            }
            finally { shot?.Dispose(); }
            app.PickTrayMenu("열기");
            Assert.Equal((0, 0), app.Exit());                                 // 측정 중 창 닫기는 안내가 뜨므로 --exit로 끝낸다
        }
        finally { AppDriver.DeleteUserData(userData); }
    }

    /// <summary>
    /// 아이콘: 실행 파일에 아이콘이 들어 있는지 확인하고, 실행 파일에 넣는 원본 NetPulse.ico(시험 출력으로 복사)의 각 크기를
    /// 밝은·어두운 작업 표시줄 색 위에 그려 맥박 선의 대비를 잰다. 배포본은 단일 파일이라 옆에 .ico가 없다(게이트 1회차).
    /// </summary>
    [Fact]
    public void IconReadsOnLightAndDarkTaskbars()
    {
        // 실행 파일 안의 아이콘 리소스 수 (없으면 0. ExtractAssociatedIcon은 없을 때도 기본 아이콘을 돌려줘 증거가 못 된다)
        Assert.True(ExtractIconExW(AppDriver.Executable, -1, null, null, 0) > 0, "실행 파일에 아이콘 리소스가 없음");
        var ico = Path.Combine(AppContext.BaseDirectory, "NetPulse.ico");
        Assert.True(File.Exists(ico), "원본 아이콘 파일");
        var results = new List<object>();
        foreach (int size in new[] { 16, 20, 24, 32, 48 })
        {
            using var icon = new Icon(ico, size, size);
            using var bmp = icon.ToBitmap();
            foreach (var (bgName, bg) in new[] { ("light", Color.FromArgb(0xf3, 0xf3, 0xf3)), ("dark", Color.FromArgb(0x20, 0x20, 0x20)) })
            {
                using var card = new Bitmap(size + 8, size + 8);
                using (var g = Graphics.FromImage(card)) { g.Clear(bg); g.DrawImage(bmp, 4, 4, size, size); }
                card.Save(Path.Combine(AppDriver.OutputDirectory, $"m9-icon-{size}-{bgName}.png"), ImageFormat.Png);
                // 맥박 선(강조색에 가까운 화소)과 둥근 바탕 사이 대비
                var line = new List<Color>();
                var plate = new List<Color>();
                for (int y = 4; y < 4 + size; y++)
                    for (int x = 4; x < 4 + size; x++)
                    {
                        var c = card.GetPixel(x, y);
                        if (c.B > 200 && c.G > 160 && c.R < 180) line.Add(c);
                        else if (c.R < 30 && c.G < 30 && c.B < 30) plate.Add(c);
                    }
                double ratio = line.Count == 0 || plate.Count == 0 ? 0 : Contrast(Avg(line), Avg(plate));
                results.Add(new { size, bg = bgName, linePixels = line.Count, ratio = Math.Round(ratio, 2) });
                Assert.True(line.Count >= size / 2, $"{size}px {bgName}: 맥박 선이 보이지 않음");
                Assert.True(ratio >= 3, $"{size}px {bgName}: 선과 바탕 대비 {ratio:0.00} < 3");
            }
        }
        Record("m9-icon", new { results });
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    private static int DistinctColors(Bitmap b)
    {
        var set = new HashSet<int>();
        for (int y = 0; y < b.Height; y += 2)
            for (int x = 0; x < b.Width; x += 2) set.Add(b.GetPixel(x, y).ToArgb());
        return set.Count;
    }

    private static Color Avg(List<Color> cs) => Color.FromArgb((int)cs.Average(c => c.R), (int)cs.Average(c => c.G), (int)cs.Average(c => c.B));

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double ch(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }
        double la = L(a), lb = L(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
