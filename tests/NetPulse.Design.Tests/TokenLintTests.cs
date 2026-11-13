namespace NetPulse.Design.Tests;

/// <summary>정적 검사기 자체의 회귀 테스트와 저장소 검사 (M2 설계 4.6).</summary>
public sealed class TokenLintTests
{
    private const string Screen = "src/NetPulse.App/Screens/Sample.cs";
    private const string ScreenXaml = "src/NetPulse.App/Screens/Sample.xaml";

    private static string[] Rules(string code, string file = Screen) =>
        TokenLint.ScanText(file, code).Select(f => f.Rule).Distinct().ToArray();

    [Theory]
    [InlineData("var c = Color.FromArgb(255, 1, 2, 3);", "color-literal")]
    [InlineData("brush.Color = Colors.Red;", "color-literal")]
    [InlineData("var hex = \"#7dd3fc\";", "color-literal")]
    [InlineData("panel.Margin = new Thickness(12);", "numeric-layout")]
    [InlineData("panel.Margin = new Thickness(Space.S2, 4, 0, 0);", "numeric-layout")]
    [InlineData("border.CornerRadius = new CornerRadius(18);", "numeric-layout")]
    [InlineData("text.FontSize = 14;", "numeric-layout")]
    [InlineData("stack.Spacing = 8;", "numeric-layout")]
    [InlineData("el.Opacity = 0.5;", "numeric-layout")]
    [InlineData("acrylic.TintOpacity = .7;", "numeric-layout")]
    [InlineData("line.StrokeThickness = 1.5;", "numeric-layout")]
    [InlineData("AppWindow.ResizeClient(new SizeInt32(1280, 800));", "numeric-layout")]
    [InlineData("text.FontSize =\n    14;", "numeric-layout")]
    [InlineData("panel.Margin = new(12);", "numeric-layout")]
    [InlineData("var t = new TextBlock\n{\n    FontSize\n        = 14,\n};", "numeric-layout")]
    [InlineData("private const double Gap = 12;", "hidden-constant")]
    [InlineData("static readonly Color Bg = x;", "hidden-constant")]
    [InlineData("private static readonly TimeSpan Wait = t;", "hidden-constant")]
    [InlineData("await Task.Delay(TimeSpan.FromMilliseconds(250));", "time-literal")]
    [InlineData("var t = new TimeSpan(0, 0, 1);", "time-literal")]
    [InlineData("acrylic.TintTransitionDuration = MotionTokens.Fast;", "time-literal")]
    [InlineData("var sb = new Storyboard();", "animation-api")]
    [InlineData("var a = new DoubleAnimation();", "animation-api")]
    [InlineData("var a = compositor.CreateScalarKeyFrameAnimation();", "animation-api")]
    [InlineData("visual.StartAnimation(\"Offset\", a);", "animation-api")]
    [InlineData("panel.ChildrenTransitions = new TransitionCollection();", "animation-api")]
    [InlineData("visual.ImplicitAnimations = c;", "animation-api")]
    [InlineData("var p = new Popup();", "popup-api")]
    [InlineData("var d = new ContentDialog();", "popup-api")]
    [InlineData("el.SetValue(FrameworkElement.MarginProperty, m);", "setvalue-bypass")]
    [InlineData("el.SetValue(Control.ForegroundProperty, b);", "setvalue-bypass")]
    public void CatchesEachBypass(string code, string rule) => Assert.Contains(rule, Rules(code));

    [Theory]
    [InlineData("panel.Margin = new Thickness(Space.S4);")]
    [InlineData("panel.Margin = new Thickness(0);")]
    [InlineData("text.FontSize = Typography.Body.Size;")]
    [InlineData("el.Opacity = 0;")]
    [InlineData("stack.Spacing = Space.S2 * 2;")]
    [InlineData("acrylic.TintTransitionDuration = TimeSpan.Zero;")]
    [InlineData("await Task.Delay(MotionTokens.Fast);")]
    [InlineData("var font = new FontFamily(\"ms-appx:///Fonts/Pretendard-Bold.otf#Pretendard\");")]
    [InlineData("// panel.Margin = new Thickness(12);  주석은 검사하지 않는다")]
    [InlineData("/// <summary>FontSize = 14 같은 설명</summary>")]
    [InlineData("log.Write(\"FontSize = 14 Storyboard Popup\");")]
    [InlineData("int count = 3;")]
    [InlineData("panel.Margin = new(Space.S2);")]
    [InlineData("text.FontSize =\n    Typography.Body.Size;")]
    public void AllowsTokenUse(string code) => Assert.Empty(Rules(code));

    [Fact]
    public void AllowedFilesMayUseTheirRuleOnly()
    {
        Assert.Empty(Rules("var c = Color.FromArgb(255, 1, 2, 3);", "src/NetPulse.App/Design/TokenBrushes.cs"));
        Assert.Contains("numeric-layout", Rules("panel.Margin = new Thickness(12);", "src/NetPulse.App/Design/TokenBrushes.cs"));
        Assert.Empty(Rules("var sb = new Storyboard();", "src/NetPulse.App/Design/Motion.cs"));
        Assert.Contains("animation-api", Rules("var sb = new Storyboard();", "src/NetPulse.App/Design/Glass.cs"));
    }

    [Theory]
    [InlineData("<Border Background=\"#ff0000\"/>", "color-literal")]
    [InlineData("<Border Margin=\"12\"/>", "numeric-layout")]
    [InlineData("<Border CornerRadius=\"18,18,0,0\"/>", "numeric-layout")]
    [InlineData("<Storyboard x:Key=\"s\"/>", "animation-api")]
    [InlineData("<DoubleAnimation Duration=\"0:0:0.3\"/>", "animation-api")]
    [InlineData("<VisualTransition GeneratedDuration=\"0:0:0.2\"/>", "animation-api")]
    [InlineData("<Popup/>", "popup-api")]
    [InlineData("<Setter Property=\"FontSize\" Value=\"14\"/>", "numeric-layout")]
    [InlineData("<Setter Property=\"Margin\"\n        Value=\"12,0,0,0\"/>", "numeric-layout")]
    public void CatchesXamlBypass(string code, string rule) => Assert.Contains(rule, Rules(code, ScreenXaml));

    [Theory]
    [InlineData("<Border Margin=\"{StaticResource Gap}\"/>")]
    [InlineData("<Border Margin=\"0\"/>")]
    [InlineData("<Setter Property=\"Margin\" Value=\"{StaticResource Gap}\"/>")]
    [InlineData("<!-- <Border Margin=\"12\"/> -->")]
    public void AllowsXamlTokenUse(string code) => Assert.Empty(Rules(code, ScreenXaml));

    [Fact]
    public void ExceptionLinesNeedAllFourParts()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "src/NetPulse.App/X.cs|time-literal|FromSeconds(3)\n");
            Assert.Throws<FormatException>(() => LintException.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AppSourceHasNoValuesOutsideTokens()
    {
        var root = RepoRoot();
        var exceptions = LintException.Load(Path.Combine(root, "tests", "NetPulse.Design.Tests", "token-exceptions.txt"));
        var findings = TokenLint.ScanRepository(root, exceptions);
        Assert.True(findings.Count == 0, "토큰 밖 값·금지 API:\n" + string.Join("\n", findings));
    }

    [Fact]
    public void EveryExceptionIsStillNeeded()
    {
        // 쓰이지 않는 예외가 남아 있으면 범위가 넓어진다
        var root = RepoRoot();
        var exceptions = LintException.Load(Path.Combine(root, "tests", "NetPulse.Design.Tests", "token-exceptions.txt"));
        var raw = TokenLint.ScanRepository(root, []);
        var unused = exceptions.Where(e => !raw.Any(e.Covers)).ToArray();
        Assert.True(unused.Length == 0, "쓰이지 않는 예외:\n" + string.Join("\n", unused));
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NetPulse.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("저장소 루트(NetPulse.slnx)를 찾지 못했습니다.");
    }
}
