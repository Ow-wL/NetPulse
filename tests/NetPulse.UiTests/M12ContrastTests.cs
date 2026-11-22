using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using NetPulse.Design;
using NetPulse.Design.Tokens;
using Size = NetPulse.Design.Tokens.Size;

namespace NetPulse.UiTests;

/// <summary>
/// 부품과 카드 본문 글의 화면 대비 실측 (M12 설계 F, DECISIONS "M11 G4 2라운드"·"M12 비활성 단추 대비"). 방식은 <see cref="M11ContrastTests"/>와 같다:
/// 글자 있음/없음(<c>--hide-measured-text</c>) 두 장을 찍고, 내용(글·아이콘)만 투명하게 한 캡처의 바탕과 토큰 글자 색의 대비를 칸별로 잰다.
/// 재는 부품과 글자 색:
/// - 단추 <c>GlassButton</c>: 강조 채움(주요) 위 AccentInk, 유리(보조)·고스트 위 Ink, 아이콘 단추의 아이콘은 Ink(그래픽 3:1). 비활성 단추는 DisabledInk(3:1, 사용자 결정 "예외로 두기").
///   단추는 UIA에서 Button 하나라 글 상자 대신 내용 상자(단추 경계에서 좌우 여백 <c>Space.S5</c>를 빼고 줄 높이만큼 가운데)를 잰다. 아이콘 단추는 가운데 아이콘 칸.
/// - 상태 칩 <c>StatusChip</c>(옅은 채움 위 Ink 글, <c>&lt;id&gt;.label</c>), 정보 칩 <c>ScreenParts.InfoChip</c>(컨트롤 유리 위 Muted 라벨 <c>.label</c>·Ink 값 <c>.value</c>).
/// - 배너(내용 유리 위 Ink 글, <c>&lt;id&gt;.text</c>, 종류별 정보·주의·오류), 작은 글이 없는 카드의 본문 글(<c>measure.timeline.range</c>, 카드 유리 위 Muted).
/// 기준: 4.5 (큰 글이면 3.0, <see cref="TypeStyle.IsLarge"/>), 비활성 단추 3.0, 아이콘 단추 3.0. 장면마다 꼭 재야 하는 요소를 <see cref="Required"/>로 고정한다: 빠지면 실패.
/// </summary>
public sealed class M12ContrastTests
{
    private static string G5Dir { get { var d = Path.Combine(AppDriver.OutputDirectory, "g5"); Directory.CreateDirectory(d); return d; } }

    /// <summary>재는 부품 종류: 글자 색과 상자를 정한다.</summary>
    internal enum Part { AccentButton, GlassButton, GhostButton, IconButton, ChipLabel, InfoLabel, InfoValue, BannerText, CardBody }

    internal static readonly (string File, string Scene)[] Scenes =
    [
        ("m5-scenes.txt", "measure-done"), ("m5-scenes.txt", "measure-startfail"), ("m5-scenes.txt", "firstrun-detected"),
        ("m5-scenes.txt", "profile-locked"), ("m7-scenes.txt", "records-list"), ("m10-scenes.txt", "settings-about"),
    ];

    public static TheoryData<string, string, string> Cases()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var (file, scene) in Scenes)
            foreach (var theme in new[] { "dark", "light" })
                d.Add(file, scene, theme);
        return d;
    }

    /// <summary>
    /// 장면별 필수 측정 요소. 단추는 단추 ID, 칩·배너·본문 글은 글 요소 ID. 활성·비활성은 실행 중 UIA <c>IsEnabled</c>로 읽어 색과 기준을 고른다
    /// (예: measure-done의 "증상 표시", profile-locked의 저장·취소·추가·삭제는 비활성).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Id, Part Part)[]> Required = new Dictionary<string, (string, Part)[]>
    {
        ["measure-done"] = [("measure.start", Part.AccentButton), ("measure.mark", Part.GlassButton), ("measure.config", Part.GlassButton),
            ("measure.now", Part.GlassButton), ("measure.zoomout", Part.IconButton), ("measure.all", Part.IconButton), ("measure.timeline.range", Part.CardBody)],
        ["measure-startfail"] = [("measure.banner.starterror.text", Part.BannerText), ("measure.start", Part.AccentButton), ("measure.mark", Part.GlassButton)],
        ["firstrun-detected"] = [("firstrun.virtual.text", Part.BannerText), ("firstrun.gateways.text", Part.BannerText),
            ("firstrun.start", Part.AccentButton), ("firstrun.edit", Part.GlassButton)],
        ["profile-locked"] = [("profile.locked.text", Part.BannerText), ("profile.back", Part.GlassButton), ("profile.cancel", Part.GhostButton),
            ("profile.save", Part.AccentButton), ("profile.add", Part.GhostButton), ("profile.remove", Part.GhostButton)],
        ["records-list"] = [("records.row.0.end.label", Part.ChipLabel), ("records.row.1.end.label", Part.ChipLabel), ("records.compare", Part.GlassButton)],
        ["settings-about"] = [("settings.about.version.label", Part.InfoLabel), ("settings.about.version.value", Part.InfoValue),
            ("settings.about.build.label", Part.InfoLabel), ("settings.about.build.value", Part.InfoValue),
            ("settings.about.license.label", Part.InfoLabel), ("settings.about.license.value", Part.InfoValue), ("settings.about.legal", Part.GlassButton)],
    };

    private static bool IsButton(Part p) => p is Part.AccentButton or Part.GlassButton or Part.GhostButton or Part.IconButton;

    /// <summary>글자 단계 (큰 글 판정용). 아이콘 단추는 그래픽이라 따로 3:1.</summary>
    private static TypeStyle Style(Part p) => p switch
    {
        Part.InfoLabel or Part.CardBody => Typography.Caption,
        Part.BannerText => Typography.Body,
        _ => Typography.Label,
    };

    /// <summary>글자 색 토큰 이름과 값, 기준.</summary>
    internal static (string Ink, Rgba Fg, double Threshold) Foreground(Palette p, Part part, bool enabled)
    {
        if (IsButton(part) && !enabled) return ("DisabledInk", p.DisabledInk, 3.0);   // 비활성 단추: 4.5 예외, 3:1 이상 (사용자 결정)
        double text = Style(part).IsLarge ? 3.0 : 4.5;
        return part switch
        {
            Part.AccentButton => ("AccentInk", p.AccentInk, text),
            Part.IconButton => ("Ink", p.Ink, 3.0),   // 아이콘만 있는 단추: 그래픽 기준
            Part.InfoLabel or Part.CardBody => ("Muted", p.Muted, text),
            _ => ("Ink", p.Ink, text),
        };
    }

    /// <summary>
    /// 단추 내용 상자: 단추 경계에서 좌우 여백(<c>Space.S5</c>)을 빼고 줄 높이만큼 가운데. 아이콘 단추는 가운데 아이콘 칸.
    /// 대비 측정이 상자를 4 px 넓히므로, 넓힌 상자가 단추 바탕(강조 채움·유리) 안에 머물도록 단추 가장자리·둥근 모서리에서 떨어진 안쪽만 잡는다.
    /// </summary>
    private static Rectangle ContentBox(Rectangle button, Part part)
    {
        double scale = button.Height / Size.ControlM;
        if (part == Part.IconButton)
        {
            int side = (int)Math.Round(Size.IconM * scale);
            return new Rectangle(button.X + (button.Width - side) / 2, button.Y + (button.Height - side) / 2, side, side);
        }
        int inset = (int)Math.Round(Space.S5 * scale);
        int line = (int)Math.Round(Math.Max(Typography.Label.Size * Typography.Label.LineHeight, Size.IconM) * scale);
        return new Rectangle(button.X + inset, button.Y + (button.Height - line) / 2, button.Width - 2 * inset, line);
    }

    [Theory, MemberData(nameof(Cases))]
    public void PartsAndCardBodyTextAreReadableOnTheScreen(string file, string sceneId, string theme)
    {
        var palette = theme == "dark" ? Palette.Dark : Palette.Light;
        var scene = M5MatrixTests.Scenes(file).Single(s => s.Id == sceneId);
        var found = new Dictionary<string, (Rectangle Box, bool Enabled)>();
        Bitmap? hidden = null;
        foreach (var hide in new[] { false, true })
        {
            string[] extra = ["--theme", theme];
            using var app = M5MatrixTests.Open(scene, hide ? [.. extra, "--hide-measured-text"] : extra);
            app.CloseToasts();
            Thread.Sleep(1600);   // 배경 빛 상태 전환이 끝난 뒤 (M11ContrastTests와 같다)
            if (!hide)
            {
                var client = new Rectangle(0, 0, Size.WindowWidth, Size.WindowHeight);
                foreach (var (id, part) in Required[sceneId])
                {
                    var e = app.Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    if (e is null) continue;
                    Rectangle bounds;
                    bool enabled;
                    try
                    {
                        if (e.Current.IsOffscreen) continue;
                        bounds = app.ToClient(e.Current.BoundingRectangle);
                        enabled = e.Current.IsEnabled;
                    }
                    catch (ElementNotAvailableException) { continue; }
                    var box = IsButton(part) ? ContentBox(bounds, part) : bounds;
                    if (box.Width < 4 || box.Height < 4 || !client.Contains(Rectangle.Inflate(box, 4, 4))) continue;   // 스크롤 밖·잘린 요소는 재지 않는다 (필수 누락으로 실패)
                    found[id] = (box, enabled);
                }
            }
            var shot = app.CaptureBitmap();
            app.Save(shot, $"g5-m12-contrast-{sceneId}-{theme}-{(hide ? "bg" : "text")}");
            if (hide) hidden = shot; else shot.Dispose();
            Assert.Equal(0, app.Close());
        }
        var missing = Required[sceneId].Where(r => !found.ContainsKey(r.Id)).Select(r => r.Id).ToArray();
        Assert.True(missing.Length == 0, $"{sceneId}: 재야 할 요소가 화면에 없음 (또는 화면 밖) — {string.Join(", ", missing)}");
        var results = new List<object>();
        var problems = new List<string>();
        foreach (var (id, part) in Required[sceneId])
        {
            var (box, enabled) = found[id];
            var (ink, fg, threshold) = Foreground(palette, part, enabled);
            var r = G5Tests.MeasureContrast(hidden!, box, fg, threshold);
            results.Add(new { id, part = part.ToString(), enabled, ink, threshold, box = new[] { box.X, box.Y, box.Width, box.Height }, r.CellP10Min, r.P1, r.LargestFailingBlob, r.Cells });
            if (r.CellP10Min < threshold) problems.Add($"{id} ({part}, {ink}): 칸별 10백분위 최저 {r.CellP10Min:0.00} < {threshold}");
            if (r.LargestFailingBlob > 12) problems.Add($"{id} ({part}, {ink}): 기준 미달 연결 영역 {r.LargestFailingBlob} px > 12");
        }
        hidden!.Dispose();
        File.WriteAllText(Path.Combine(G5Dir, $"contrast-m12-{sceneId}-{theme}.json"), JsonSerializer.Serialize(new { scene = sceneId, theme, elements = results }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
