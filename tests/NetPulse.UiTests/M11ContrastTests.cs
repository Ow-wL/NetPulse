using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using NetPulse.Design;
using NetPulse.Design.Tokens;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// 화면 위 대비 실측을 남은 화면으로 넓힌다 (M11 설계 C2). 홈·진단·기록·리포트(M5~M8)와 같은 방식:
/// 글자 있음/없음(<c>--hide-measured-text</c>) 두 장을 찍고, 글자 없는 배경과 토큰 글자 색의 대비를 칸별로 잰다.
/// 측정 요소는 장면에 있는 화면 머리(제목·위 글)와 카드(제목·작은 글) 전부 — 놓인 유리 단계가 다르므로 요소마다 따로.
/// 기준: 4.5 (본문 글자, M2 토큰 기준). 제목·카드 제목은 Ink, 위 글·카드 작은 글은 Muted.
/// </summary>
public sealed class M11ContrastTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    public static TheoryData<string, string, string> Cases()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var (file, scene) in new[] { ("m5-scenes.txt", "measure-live"), ("m5-scenes.txt", "measure-outage"), ("m5-scenes.txt", "firstrun-detected"),
                     ("m5-scenes.txt", "profile-ok"), ("m9-scenes.txt", "settings-normal"), ("m10-scenes.txt", "terms") })
            foreach (var theme in new[] { "dark", "light" })
                d.Add(file, scene, theme);
        return d;
    }

    private static bool Measured(string id) => id.EndsWith(".title", StringComparison.Ordinal) || id.EndsWith(".overline", StringComparison.Ordinal) || id.EndsWith(".note", StringComparison.Ordinal);

    [Theory, MemberData(nameof(Cases))]
    public void HeaderAndCardTextIsReadableOnTheScreen(string file, string sceneId, string theme)
    {
        var palette = theme == "dark" ? Palette.Dark : Palette.Light;
        var scene = M5MatrixTests.Scenes(file).Single(s => s.Id == sceneId);
        var boxes = new Dictionary<string, Rectangle>();
        Bitmap? hidden = null;
        foreach (var hide in new[] { false, true })
        {
            string[] extra = ["--theme", theme];
            using var app = M5MatrixTests.Open(scene, hide ? [.. extra, "--hide-measured-text"] : extra);
            if (!sceneId.StartsWith("terms", StringComparison.Ordinal)) app.CloseToasts();
            Thread.Sleep(1600);   // 배경 빛 상태 전환이 끝난 뒤
            if (!hide)
            {
                var client = new Rectangle(0, 0, Size.WindowWidth, Size.WindowHeight);
                foreach (AutomationElement e in app.Window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
                {
                    string id, name;
                    try { id = e.Current.AutomationId; name = e.Current.Name; } catch (ElementNotAvailableException) { continue; }
                    if (!Measured(id) || string.IsNullOrWhiteSpace(name) || e.Current.IsOffscreen) continue;
                    var box = app.ToClient(e.Current.BoundingRectangle);
                    if (box.Width < 4 || box.Height < 4 || !client.Contains(box)) continue;   // 스크롤 밖·잘린 요소는 재지 않는다 (잘림은 배치 검사가 본다)
                    boxes[id] = box;
                }
            }
            var shot = app.CaptureBitmap();
            app.Save(shot, $"g5-m11-contrast-{sceneId}-{theme}-{(hide ? "bg" : "text")}");
            if (hide) hidden = shot; else shot.Dispose();
            Assert.Equal(0, app.Close());
        }
        Assert.True(boxes.Count >= 2, $"{sceneId}: 잴 요소가 {boxes.Count}개");                 // 적어도 화면 머리 제목과 카드 하나
        var results = new List<object>();
        var problems = new List<string>();
        foreach (var (id, box) in boxes.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            Rgba fg = id.EndsWith(".title", StringComparison.Ordinal) ? palette.Ink : palette.Muted;
            var r = G5Tests.MeasureContrast(hidden!, box, fg, threshold: 4.5);
            results.Add(new { id, ink = id.EndsWith(".title", StringComparison.Ordinal) ? "Ink" : "Muted", r.CellP10Min, r.P1, r.LargestFailingBlob, r.Cells });
            if (r.CellP10Min < 4.5) problems.Add($"{id}: 칸별 10백분위 최저 {r.CellP10Min:0.00} < 4.5");
            if (r.LargestFailingBlob > 12) problems.Add($"{id}: 기준 미달 연결 영역 {r.LargestFailingBlob} px > 12");
        }
        hidden!.Dispose();
        File.WriteAllText(Path.Combine(G5Dir, $"contrast-m11-{sceneId}-{theme}.json"), JsonSerializer.Serialize(new { scene = sceneId, theme, elements = results }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
