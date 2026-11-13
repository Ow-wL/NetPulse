using System.Globalization;
using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Live;
using NetPulse.Core.Targets;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 측정 구성 편집 (M5 설계 4.5, R1). 모든 변경은 초안이다: [저장]은 검증 오류가 0일 때만 켜지고, [취소]는 초안을 버린다
/// (그래서 삭제도 저장 전에는 되돌릴 수 있어 확인을 묻지 않는다). 측정 중에는 읽기 전용.
/// 측정 방식은 ICMP만 편집한다 (TCP는 M9). 새 대상 ID는 자동.
/// </summary>
public sealed class ProfileEditor : Screen
{
    /// <summary>첫 실행 문맥: 저장된 구성이 없을 때 시작할 초안 (탐지 제안).</summary>
    public static Profile? Seed { get; set; }
    /// <summary>뒤로 갈 화면 (첫 실행이면 firstrun).</summary>
    public static string ReturnTo { get; set; } = "measure";

    private sealed class DraftTarget
    {
        public required string Id;
        public string Name = "", Address = "", Provider = "";
        public TargetRole Role = TargetRole.External;
        public bool Enabled = true;
        public ProbeMethod Method = ProbeMethod.Icmp;
    }

    private sealed class DraftProfile
    {
        public required string Id;
        public string Name = "", Minutes = "10", TimeoutMs = "800";
        public ExpectedConnection Expected = ExpectedConnection.Any;
        public List<DraftTarget> Targets = [];
    }

    private readonly PageHeader header;
    private readonly GlassButton back, save, cancel;
    private readonly StackPanel content = new() { Spacing = Space.S5 };
    private readonly ScrollViewer scroll;
    private readonly StackPanel issues = new() { Spacing = Space.S2 };
    private List<DraftProfile> profiles = [];
    private string currentId = "";
    private bool dirty, locked, advanced;
    private readonly Dictionary<string, GlassTextBox> fields = [];

    public ProfileEditor(ScreenContext context) : base(context, "profile")
    {
        RowSpacing = Space.S5;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        back = new GlassButton(Ui, ButtonKind.Secondary, "profile.back", "뒤로", Icons.Back);
        cancel = new GlassButton(Ui, ButtonKind.Ghost, "profile.cancel", "취소");
        save = new GlassButton(Ui, ButtonKind.Primary, "profile.save", "저장", Icons.Check);
        back.Click += (_, _) => Leave(back);
        cancel.Click += (_, _) => { Load(); Rebuild(); };
        save.Click += (_, _) => Save();
        header = new PageHeader(Ui, "profile.header", "측정 구성", "대상과 프로필", back, cancel, save);
        Children.Add(header);
        scroll = new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        AutomationProperties.SetAutomationId(scroll, "profile.scroll");
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
    }

    public override IReadOnlyList<UIElement> Parts => [header, scroll];

    public override void Entered()
    {
        Load();
        Rebuild();
    }

    public override void Render(HostSnapshot s)
    {
        bool nowLocked = s.State is HostState.Measuring or HostState.Stopping or HostState.Starting;
        // 화면 호스트는 Entered 전에 한 번 그린다: 초안을 아직 읽지 않았으면 잠금 상태만 기억한다 (빈 목록에서 다시 만들다 앱이 멈췄다)
        if (profiles.Count == 0) { locked = nowLocked; return; }
        if (nowLocked != locked) { locked = nowLocked; Rebuild(); }
    }

    // ---------------- 초안 ----------------

    private void Load()
    {
        var set = Context.Host.Profiles;
        var source = set?.Profiles ?? (Seed is { } seed ? [seed] : [new Profile("p1", "기본", [], Profile.DefaultTimeout, Profile.DefaultDuration, ExpectedConnection.Any)]);
        profiles = source.Select(ToDraft).ToList();
        currentId = set?.LastUsedId is { } last && profiles.Any(p => p.Id == last) ? last : profiles[0].Id;
        dirty = set is null && Seed is not null;
    }

    private static DraftProfile ToDraft(Profile p) => new()
    {
        Id = p.Id, Name = p.Name, Minutes = ((int)p.Duration.TotalMinutes).ToString(CultureInfo.InvariantCulture),
        TimeoutMs = ((int)p.Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture), Expected = p.Expected,
        Targets = p.Targets.Select(t => new DraftTarget { Id = t.Id, Name = t.Name, Address = t.Address.ToString(), Provider = t.Provider ?? "", Role = t.Role, Enabled = t.Enabled, Method = t.Method }).ToList(),
    };

    private DraftProfile Current => profiles.First(p => p.Id == currentId);

    /// <summary>초안 → 프로필 (주소를 못 읽으면 null, 그 칸 오류는 FieldErrors가 낸다).</summary>
    private static Profile? Build(DraftProfile d)
    {
        var targets = new List<Target>();
        foreach (var t in d.Targets)
        {
            if (!IPAddress.TryParse(t.Address.Trim(), out var ip)) return null;
            targets.Add(new Target(t.Id, t.Name.Trim(), ip, t.Role, t.Provider.Trim().Length == 0 ? null : t.Provider.Trim(), t.Method, t.Enabled));
        }
        if (!int.TryParse(d.Minutes.Trim(), out var minutes) || !int.TryParse(d.TimeoutMs.Trim(), out var timeout)) return null;
        return new Profile(d.Id, d.Name.Trim(), targets, TimeSpan.FromMilliseconds(timeout), TimeSpan.FromMinutes(minutes), d.Expected);
    }

    /// <summary>칸별 오류 (칸 아래 글). 프로필 전체 규칙은 <see cref="ProfileValidator"/>가 낸다.</summary>
    private static Dictionary<string, string> FieldErrors(DraftProfile d)
    {
        var e = new Dictionary<string, string>();
        if (d.Name.Trim().Length == 0) e["profile.name"] = "프로필 이름을 적어 주세요.";
        if (!int.TryParse(d.Minutes.Trim(), out var m) || m < Profile.MinDuration.TotalMinutes || m > Profile.MaxDuration.TotalMinutes) e["profile.minutes"] = "1~720 사이의 분을 적어 주세요.";
        if (!int.TryParse(d.TimeoutMs.Trim(), out var t) || t < Profile.MinTimeout.TotalMilliseconds || t > Profile.MaxTimeout.TotalMilliseconds) e["profile.timeout"] = "200~900 ms 사이로 적어 주세요.";
        for (int i = 0; i < d.Targets.Count; i++)
        {
            var x = d.Targets[i];
            if (x.Name.Trim().Length == 0) e[$"profile.target.{i}.name"] = "이름을 적어 주세요.";
            if (!IPAddress.TryParse(x.Address.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || x.Address.Count(c => c == '.') != 3)
                e[$"profile.target.{i}.address"] = "IPv4 주소를 적어 주세요 (예: 192.0.2.1).";
        }
        return e;
    }

    private (bool Ok, IReadOnlyList<ValidationIssue> Issues, Dictionary<string, string> Fields) Check()
    {
        var fieldErrors = FieldErrors(Current);
        var built = Build(Current);
        var list = built is null ? [] : ProfileValidator.Validate(built);
        bool others = profiles.Where(p => p.Id != currentId).Any(p => FieldErrors(p).Count > 0 || Build(p) is not { } b || ProfileValidator.HasErrors(ProfileValidator.Validate(b)));
        return (fieldErrors.Count == 0 && !ProfileValidator.HasErrors(list) && !others, list, fieldErrors);
    }

    private void Changed()
    {
        dirty = true;
        Revalidate();
    }

    private void Revalidate()
    {
        var (ok, list, fieldErrors) = Check();
        foreach (var (id, field) in fields) field.SetError(fieldErrors.GetValueOrDefault(id));
        issues.Children.Clear();
        foreach (var i in list.Where(i => i.Severity == IssueSeverity.Error))
            issues.Children.Add(new Banner(Ui, "profile.error." + i.Code, BannerKind.Error, i.Message));
        foreach (var i in list.Where(i => i.Severity == IssueSeverity.Warning))
            issues.Children.Add(new Banner(Ui, "profile.warn." + i.Code, BannerKind.Warn, i.Message));
        save.IsEnabled = ok && dirty && !locked;
        cancel.IsEnabled = dirty && !locked;
        Context.QueueDiag();
    }

    // ---------------- 화면 ----------------

    private void Rebuild()
    {
        content.Children.Clear();
        fields.Clear();
        if (locked) content.Children.Add(new Banner(Ui, "profile.locked", BannerKind.Info, "측정 중에는 바꿀 수 없습니다. 측정을 멈추면 고칠 수 있습니다."));
        content.Children.Add(ProfilesRow());
        content.Children.Add(issues);
        content.Children.Add(ProfileCard());
        content.Children.Add(TargetsCard());
        Revalidate();
    }

    private UIElement ProfilesRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            var b = new GlassButton(Ui, p.Id == currentId ? ButtonKind.Primary : ButtonKind.Secondary, $"profile.select.{i}", p.Name.Length == 0 ? "(이름 없음)" : p.Name);
            b.Click += (_, _) => { currentId = p.Id; Rebuild(); };
            row.Children.Add(b);
        }
        var add = new GlassButton(Ui, ButtonKind.Ghost, "profile.add", "새 프로필", Icons.Plus);
        add.Click += (_, _) =>
        {
            var copy = ToDraft(Build(Current) ?? new Profile(NewProfileId(), "", [], Profile.DefaultTimeout, Profile.DefaultDuration, ExpectedConnection.Any));
            copy.Id = NewProfileId();
            copy.Name = Current.Name + " 복사본";
            profiles.Add(copy);
            currentId = copy.Id;
            dirty = true;
            Rebuild();
        };
        var remove = new GlassButton(Ui, ButtonKind.Ghost, "profile.remove", "이 프로필 삭제", Icons.Trash) { IsEnabled = profiles.Count > 1 && !locked };
        remove.Click += (_, _) =>
        {
            profiles.RemoveAll(p => p.Id == currentId);
            currentId = profiles[0].Id;
            dirty = true;
            Rebuild();
        };
        add.IsEnabled = !locked;
        row.Children.Add(add);
        row.Children.Add(remove);
        var panel = new GroupPanel("profile.list", "프로필");
        panel.Children.Add(row);
        return panel;
    }

    private string NewProfileId()
    {
        for (int n = 1; ; n++) if (profiles.All(p => p.Id != "p" + n)) return "p" + n;
    }

    private string NewTargetId()
    {
        for (int n = 1; ; n++) if (Current.Targets.All(t => t.Id != "t" + n)) return "t" + n;
    }

    private GlassTextBox Field(string id, string label, string value, double width, Action<string> set, string? placeholder = null)
    {
        var f = new GlassTextBox(Ui, id, label, value, placeholder, width);
        f.Box.IsReadOnly = locked;
        // 글 상자는 처음 그려질 때도 바뀜 알림을 낸다: 값이 실제로 바뀐 때만 초안을 바꾼다 (변경 없이 "저장하지 않은 변경"이 되던 문제)
        string last = value;
        f.Box.TextChanged += (_, _) =>
        {
            if (f.Text == last) return;
            last = f.Text;
            set(f.Text);
            Changed();
        };
        fields[id] = f;
        return f;
    }

    private UIElement ProfileCard()
    {
        var d = Current;
        var stack = new StackPanel { Spacing = Space.S4 };
        stack.Children.Add(Field("profile.name", "프로필 이름", d.Name, Size.FieldL, v => d.Name = v));
        var minutesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
        var minutes = Field("profile.minutes", "측정 시간 (분)", d.Minutes, Size.FieldS, v => d.Minutes = v);
        minutesRow.Children.Add(minutes);
        foreach (var (value, label) in new[] { ("10", "10분"), ("30", "30분"), ("60", "1시간") })
        {
            var quick = new GlassButton(Ui, ButtonKind.Ghost, "profile.minutes." + value, label) { IsEnabled = !locked, VerticalAlignment = VerticalAlignment.Bottom };
            quick.Click += (_, _) => minutes.Text = value;
            minutesRow.Children.Add(quick);
        }
        stack.Children.Add(minutesRow);
        var expected = new SegmentedChoice(Ui, "profile.expected", "예상 연결", [("any", "상관없음"), ("wired", "유선"), ("wireless", "Wi‑Fi")],
            d.Expected switch { ExpectedConnection.Wired => "wired", ExpectedConnection.Wireless => "wireless", _ => "any" }) { IsHitTestVisible = !locked };
        expected.Changed += v => { d.Expected = v switch { "wired" => ExpectedConnection.Wired, "wireless" => ExpectedConnection.Wireless, _ => ExpectedConnection.Any }; Changed(); };
        var expectedBox = new StackPanel { Spacing = Space.S1 };
        expectedBox.Children.Add(Fonts.Text("예상 연결 (다르면 판단을 보류합니다)", Typography.Label, Ui.Brushes.Muted));
        expectedBox.Children.Add(expected);
        stack.Children.Add(expectedBox);
        var adv = new SettingToggle(Ui, "profile.advanced", "고급 설정 보기", advanced);
        var timeout = Field("profile.timeout", "응답 제한 시간 (ms, 200~900)", d.TimeoutMs, Size.FieldS, v => d.TimeoutMs = v);
        timeout.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        adv.Checked += (_, _) => { advanced = true; timeout.Visibility = Visibility.Visible; };
        adv.Unchecked += (_, _) => { advanced = false; timeout.Visibility = Visibility.Collapsed; };
        stack.Children.Add(adv);
        stack.Children.Add(timeout);
        var (card, body) = ScreenParts.Card(Ui, "profile.card", "프로필", null);
        body.Set(stack);
        return card;
    }

    private UIElement TargetsCard()
    {
        var d = Current;
        var stack = new StackPanel { Spacing = Space.S4 };
        stack.Children.Add(Fonts.Wrapped("대상 순서는 실제 패킷 경로를 증명하지 않습니다. 로컬 대상은 공유기처럼 집 안의 장비, 외부 대상은 서로 다른 사업자의 주소로 두세요.",
            Typography.Caption, Ui.Brushes.Muted));
        for (int i = 0; i < d.Targets.Count; i++) stack.Children.Add(TargetRow(d, i));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
        var add = new GlassButton(Ui, ButtonKind.Secondary, "profile.target.add", "대상 추가", Icons.Plus) { IsEnabled = !locked && d.Targets.Count < Profile.MaxTargets };
        add.Click += (_, _) =>
        {
            d.Targets.Add(new DraftTarget { Id = NewTargetId(), Role = d.Targets.Any(t => t.Role == TargetRole.Local) ? TargetRole.External : TargetRole.Local });
            Changed();
            Rebuild();
        };
        var redetect = new GlassButton(Ui, ButtonKind.Ghost, "profile.redetect", "자동 탐지로 다시 채우기", Icons.Refresh) { IsEnabled = !locked };
        redetect.Click += async (_, _) =>
        {
            redetect.IsEnabled = false;
            var r = await Context.Host.DetectAsync(CancellationToken.None);
            redetect.IsEnabled = true;
            if (r.Proposal is not { } p) { Context.Toasts.Show(ToastKind.Warning, "탐지 제안이 없습니다", r.Notice ?? "연결을 확인하지 못했습니다."); return; }
            d.Targets = ToDraft(p).Targets;
            Changed();
            Rebuild();
        };
        actions.Children.Add(add);
        actions.Children.Add(redetect);
        stack.Children.Add(actions);
        var (card, body) = ScreenParts.Card(Ui, "profile.targets", "대상", $"{d.Targets.Count(t => t.Enabled)}개 사용 · 최대 {Profile.MaxTargets}개");
        body.Set(stack);
        return card;
    }

    private UIElement TargetRow(DraftProfile d, int i)
    {
        var t = d.Targets[i];
        var lines = new StackPanel { Spacing = Space.S2 };
        var first = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
        var up = new GlassButton(Ui, ButtonKind.Icon, $"profile.target.{i}.up", "위로", Icons.Up) { IsEnabled = !locked && i > 0, VerticalAlignment = VerticalAlignment.Bottom };
        var down = new GlassButton(Ui, ButtonKind.Icon, $"profile.target.{i}.down", "아래로", Icons.Down) { IsEnabled = !locked && i < d.Targets.Count - 1, VerticalAlignment = VerticalAlignment.Bottom };
        up.Click += (_, _) => { (d.Targets[i - 1], d.Targets[i]) = (d.Targets[i], d.Targets[i - 1]); Changed(); Rebuild(); };
        down.Click += (_, _) => { (d.Targets[i + 1], d.Targets[i]) = (d.Targets[i], d.Targets[i + 1]); Changed(); Rebuild(); };
        first.Children.Add(up);
        first.Children.Add(down);
        first.Children.Add(Field($"profile.target.{i}.name", "이름", t.Name, Size.FieldM, v => t.Name = v));
        first.Children.Add(Field($"profile.target.{i}.address", "주소 (IPv4)", t.Address, Size.FieldM, v => t.Address = v, "192.0.2.1"));
        lines.Children.Add(first);
        var second = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        var enabled = new SettingToggle(Ui, $"profile.target.{i}.enabled", "사용", t.Enabled) { IsEnabled = !locked, VerticalAlignment = VerticalAlignment.Center };
        enabled.Checked += (_, _) => { t.Enabled = true; Changed(); };
        enabled.Unchecked += (_, _) => { t.Enabled = false; Changed(); };
        second.Children.Add(enabled);
        var role = new SegmentedChoice(Ui, $"profile.target.{i}.role", "역할", [("local", "로컬"), ("external", "외부")], t.Role == TargetRole.Local ? "local" : "external") { IsHitTestVisible = !locked };
        role.Changed += v => { t.Role = v == "local" ? TargetRole.Local : TargetRole.External; Changed(); };
        second.Children.Add(role);
        second.Children.Add(Field($"profile.target.{i}.provider", "사업자 (선택)", t.Provider, Size.FieldM, v => t.Provider = v));
        if (t.Method.Kind == ProbeMethodKind.Tcp)
            second.Children.Add(Fonts.Text($"TCP {t.Method.Port} · 편집은 다음 단계에서", Typography.Caption, Ui.Brushes.Muted));
        var remove = new GlassButton(Ui, ButtonKind.Icon, $"profile.target.{i}.remove", "대상 삭제", Icons.Trash) { IsEnabled = !locked, VerticalAlignment = VerticalAlignment.Bottom };
        remove.Click += (_, _) => { d.Targets.RemoveAt(i); Changed(); Rebuild(); };
        second.Children.Add(remove);
        lines.Children.Add(second);
        var panel = new GroupPanel($"profile.target.{i}", t.Name.Length == 0 ? $"대상 {i + 1}" : t.Name);
        panel.Children.Add(new Border { BorderBrush = Ui.Brushes.Edge, BorderThickness = new Thickness(0, 0, 0, Size.Hairline), Padding = new Thickness(0, 0, 0, Space.S4), Child = lines });
        return panel;
    }

    // ---------------- 저장·나가기 ----------------

    private void Save()
    {
        var (ok, _, _) = Check();
        if (!ok || locked) return;
        var built = profiles.Select(Build).ToArray();
        if (built.Any(b => b is null)) return;
        try
        {
            Context.Host.SaveProfiles(new ProfileSet(built!, currentId));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Context.Toasts.Show(ToastKind.Error, "측정 구성을 저장하지 못했습니다", ex.Message);
            return;
        }
        dirty = false;
        Seed = null;
        Context.Log.Write("profileSaved", new { profiles = built.Length, targets = built.Sum(b => b!.Targets.Count) });
        Context.Toasts.Show(ToastKind.Success, "측정 구성을 저장했습니다", "다음 측정부터 이 구성으로 측정합니다.");
        Revalidate();
        if (ReturnTo == "firstrun") Go();
    }

    private void Leave(Control opener)
    {
        if (!dirty || locked) { Go(); return; }
        void OnClosed(bool confirmed)
        {
            Context.Dialog.Closed -= OnClosed;
            if (confirmed) { Load(); Go(); }
        }
        Context.Dialog.Closed += OnClosed;
        Context.Dialog.Open(opener, "저장하지 않은 변경이 있습니다", "변경을 버리고 나갈까요? 저장하려면 [계속 편집]을 누른 뒤 [저장]을 누르세요.", "버리고 나가기", "계속 편집");
        Context.QueueDiag();
    }

    private void Go()
    {
        var target = ReturnTo;
        ReturnTo = "measure";
        Context.Navigate(target, null);
    }

    public override bool Escape()
    {
        Leave(back);
        return true;
    }

    public override object? Diag()
    {
        var (ok, list, fieldErrors) = Check();
        return new { dirty, locked, saveEnabled = save.IsEnabled, ok, errors = list.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code).Concat(fieldErrors.Keys).ToArray(),
            warnings = list.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.Code).ToArray(), profiles = profiles.Count, targets = Current.Targets.Count };
    }
}
