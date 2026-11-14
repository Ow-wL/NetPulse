using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 진단 (M6 설계 4): 머리(시작), 결론 카드(결론 먼저), 검사 카드 일곱, 사이트 점검, 보조 도구.
/// 값은 <see cref="DiagnosisHost"/>의 장면과 <see cref="DiagnosisView"/>로만 정한다.
/// </summary>
public sealed class DiagnoseScreen : Screen
{
    private static readonly CheckId[] Order = [CheckId.Adapter, CheckId.Gateway, CheckId.Tcp, CheckId.Dns, CheckId.Https, CheckId.Portal, CheckId.Link];

    private readonly PageHeader header;
    private readonly GlassButton start;
    private readonly GroupPanel conclusionCard;
    private readonly StackPanel conclusion = new() { Spacing = Space.S3 };
    private readonly Grid checksGrid = new() { ColumnSpacing = Space.S4, RowSpacing = Space.S4 };
    private readonly Dictionary<CheckId, CheckCard> checkCards = [];
    private readonly GroupPanel siteCard, toolsCard;
    private readonly GlassTextBox siteInput;
    private readonly GlassButton siteRun;
    private readonly StackPanel siteResult = new() { Spacing = Space.S1 };
    private readonly GlassButton flush, folder;
    private readonly TextBlock toolsStatus;
    private readonly Grid lower = new() { ColumnSpacing = Space.S5, RowSpacing = Space.S5 };
    private long renderedVersion = -1;
    private string? renderedFolderKey;
    private string? copyState;
    private string? lastAction;
    private string? folderTarget;
    private int columns;

    public DiagnoseScreen(ScreenContext context) : base(context, "diagnose")
    {
        RowSpacing = Space.S4;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        start = new GlassButton(Ui, ButtonKind.Primary, "diagnose.start", "빠른 진단 시작", Icons.Play);
        start.Click += (_, _) => Start();
        header = new PageHeader(Ui, "diagnose.header", "", "진단", start);
        Children.Add(header);

        var content = new StackPanel { Spacing = Space.S5 };
        conclusionCard = new GroupPanel("diagnose.conclusion", "결론");
        conclusionCard.Children.Add(Ui.Glass.Surface(GlassLevel.Content, conclusion, Radius.Outer, new Thickness(Space.S7, Space.S6, Space.S7, Space.S6)));
        content.Children.Add(conclusionCard);

        foreach (var id in Order)
        {
            var card = new CheckCard(Ui, id);
            checkCards[id] = card;
            checksGrid.Children.Add(card);
        }
        checksGrid.SizeChanged += (_, e) => ArrangeChecks(e.NewSize.Width);
        AutomationProperties.SetAutomationId(checksGrid, "diagnose.checks");
        AutomationProperties.SetName(checksGrid, "검사 일곱");
        content.Children.Add(checksGrid);

        // 사이트 점검
        siteInput = new GlassTextBox(Ui, "diagnose.site.input", "도메인", "", "예: example.com", Size.FieldL, multiline: false);
        siteInput.Box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; CheckSite(); } };
        siteInput.Box.TextChanged += (_, _) => siteInput.SetError(null);
        siteRun = new GlassButton(Ui, ButtonKind.Secondary, "diagnose.site.run", "점검", Icons.Globe) { VerticalAlignment = VerticalAlignment.Bottom };
        siteRun.Click += (_, _) => CheckSite();
        var siteRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        siteRow.Children.Add(siteInput);
        siteRow.Children.Add(siteRun);
        var siteStack = new StackPanel { Spacing = Space.S3 };
        siteStack.Children.Add(Fonts.Wrapped("도메인 하나의 이름 해석(DNS)과 HTTPS 응답을 따로 확인합니다.", Typography.Caption, Ui.Brushes.Muted));
        siteStack.Children.Add(siteRow);
        siteStack.Children.Add(siteResult);
        (siteCard, var siteBody) = ScreenParts.Card(Ui, "diagnose.site", "사이트 점검");
        siteBody.Set(siteStack);
        lower.Children.Add(siteCard);

        // 보조 도구
        flush = new GlassButton(Ui, ButtonKind.Secondary, "diagnose.flush", "DNS 캐시 비우기", Icons.Refresh);
        flush.Click += (_, _) => ConfirmFlush(flush);
        var network = new GlassButton(Ui, ButtonKind.Ghost, "diagnose.settings.network", "네트워크 설정", Icons.Settings);
        var wifi = new GlassButton(Ui, ButtonKind.Ghost, "diagnose.settings.wifi", "Wi‑Fi 설정", Icons.Settings);
        var proxy = new GlassButton(Ui, ButtonKind.Ghost, "diagnose.settings.proxy", "프록시 설정", Icons.Settings);
        network.Click += (_, _) => OpenSettings(SettingsPage.Network);
        wifi.Click += (_, _) => OpenSettings(SettingsPage.Wifi);
        proxy.Click += (_, _) => OpenSettings(SettingsPage.Proxy);
        folder = new GlassButton(Ui, ButtonKind.Ghost, "diagnose.folder", "기록 폴더 열기", Icons.Layers);
        folder.Click += (_, _) => OpenFolder();
        var toolsStack = new StackPanel { Spacing = Space.S2 };
        toolsStack.Children.Add(Fonts.Wrapped("DNS 캐시 비우기만 시스템을 바꾸므로 확인을 묻습니다. 끝나면 빠른 진단을 다시 돌립니다.", Typography.Caption, Ui.Brushes.Muted));
        toolsStack.Children.Add(flush);
        var settingsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S1 };
        settingsRow.Children.Add(network);
        settingsRow.Children.Add(wifi);
        settingsRow.Children.Add(proxy);
        toolsStack.Children.Add(settingsRow);
        toolsStack.Children.Add(folder);
        toolsStatus = Fonts.Wrapped("", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(toolsStatus, "diagnose.tools.status");
        toolsStack.Children.Add(toolsStatus);
        (toolsCard, var toolsBody) = ScreenParts.Card(Ui, "diagnose.tools", "보조 도구");
        toolsBody.Set(toolsStack);
        lower.Children.Add(toolsCard);
        lower.SizeChanged += (_, e) => ArrangeLower(e.NewSize.Width >= Size.HomeTwoColumns);
        ArrangeLower(true);
        content.Children.Add(lower);

        var scroll = new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        AutomationProperties.SetAutomationId(scroll, "diagnose.scroll");
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
    }

    public override IReadOnlyList<UIElement> Parts => [header, conclusionCard, checksGrid, lower];

    // ---------------- 조작 ----------------

    /// <summary>시작 (버튼, 홈 카드의 "진단 시작").</summary>
    public void Start()
    {
        Context.Diagnosis.StartDiagnosis();
        Render(Context.Host.Snapshot);
    }

    private void CheckSite()
    {
        var r = Context.Diagnosis.CheckSite(siteInput.Text);
        if (r == DiagnosisRequest.Invalid && Context.Diagnosis.Snapshot.Site.InputError is { } code) siteInput.SetError(DiagnosisTexts.InputError(code));
        Render(Context.Host.Snapshot);
    }

    private void ConfirmFlush(Control opener)
    {
        void Closed(bool ok)
        {
            Context.Dialog.Closed -= Closed;
            if (!ok) return;
            if (Context.Diagnosis.RequestFlush() == DiagnosisRequest.Queued)
                Context.Toasts.Show(ToastKind.Info, "진단이 끝나면 DNS 캐시를 비웁니다", "비운 뒤 빠른 진단을 다시 돌립니다.");
            Render(Context.Host.Snapshot);
        }
        Context.Dialog.Closed += Closed;
        Context.Dialog.Open(opener, "DNS 캐시를 비울까요?",
            "Windows가 기억한 이름 해석 결과를 지웁니다. 인터넷 연결이나 설정은 바뀌지 않습니다. 끝나면 빠른 진단을 다시 돌립니다.", "비우기", "취소");
    }

    private void OpenSettings(SettingsPage page)
    {
        var r = Context.Diagnosis.World.Actions.OpenSettings(page);
        lastAction = "settings:" + page;
        toolsStatus.Text = r.Ok ? "" : "설정을 열지 못했습니다.";
        Context.QueueDiag();
    }

    private void OpenFolder()
    {
        if (folderTarget is null) return;
        var r = Context.Diagnosis.World.Actions.OpenFolder(folderTarget, AllowedRoots());
        lastAction = "folder:" + FolderKind(folderTarget);
        toolsStatus.Text = r.Ok ? "" : $"폴더를 열지 못했습니다 ({DiagnosisTexts.Reason(r.Code)}).";
        Context.QueueDiag();
    }

    private IReadOnlyList<string> AllowedRoots() => [.. Context.Host.World.RecordPaths, Context.Diagnosis.Store.Folder];

    /// <summary>기록 폴더 단추가 고르는 폴더 (설계 3.3): 측정 중이면 그 측정의 마지막 조각, 아니면 기본 기록 위치(없으면 대체).</summary>
    private string? PickFolder()
    {
        var roots = Context.Host.World.RecordPaths;
        if (Context.Host.RunningId is { } id)
        {
            try { if (RecordReader.FindSegments(id, roots).LastOrDefault() is { } seg) return seg; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return roots.FirstOrDefault(Directory.Exists);
    }

    private string FolderKind(string path)
    {
        var roots = Context.Host.World.RecordPaths;
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        for (int i = 0; i < roots.Count; i++)
            if (string.Equals(full, Path.GetFullPath(roots[i]).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return i == 0 ? "root" : "fallback-root";
        return "segment";
    }

    private async void Copy(DiagnosisRun run)
    {
        var text = QuickDiagnosis.CopyText(run.Result);
        bool wrote = false, verified = false;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            wrote = true;
            var back = await Clipboard.GetContent().GetTextAsync();
            verified = back == text;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException) { }
        if (Context.IsClosing()) return;
        copyState = verified ? "ok" : wrote ? "unverified" : "failed";
        if (verified) Context.Toasts.Show(ToastKind.Success, "결과를 복사했습니다", "주소·도메인 값은 넣지 않았습니다.");
        else if (wrote) Context.Toasts.Show(ToastKind.Warning, "복사했지만 확인하지 못했습니다", "붙여 넣어 내용을 확인해 주세요.");
        else Context.Toasts.Show(ToastKind.Error, "복사하지 못했습니다", "다른 프로그램이 클립보드를 쓰고 있을 수 있습니다. 다시 시도해 주세요.");
        Context.QueueDiag();
    }

    // ---------------- 그리기 ----------------

    public override void Render(Core.Live.HostSnapshot snapshot)
    {
        var d = Context.Diagnosis.Snapshot;
        folderTarget = PickFolder();
        string folderKey = (folderTarget ?? "") + "|" + (Context.Host.RunningId ?? "");
        if (d.Version == renderedVersion && folderKey == renderedFolderKey) return;
        bool finished = renderedVersion >= 0 && d.Operation == DiagnosisOperation.None && d.Last is not null
            && !ReferenceEquals(d.Last, lastRun);
        renderedVersion = d.Version;
        renderedFolderKey = folderKey;

        start.IsEnabled = d.Operation == DiagnosisOperation.None;
        flush.IsEnabled = d.Operation != DiagnosisOperation.Flushing;
        folder.IsEnabled = folderTarget is not null;
        header.SetOverline(d.Operation switch
        {
            DiagnosisOperation.Diagnosing => $"검사 중 {d.Partial.Count}/7",
            DiagnosisOperation.Flushing => FlushStageText(d.Flush),
            _ => d.Last is { } l ? "마지막 검사 " + LiveSummary.Clock(l.Result.EndedAt, Context.Now) : null,
        });
        RenderConclusion(d);
        RenderChecks(d);
        RenderSite(d);
        if (folderTarget is null) toolsStatus.Text = "아직 기록이 없습니다.";
        else if (toolsStatus.Text == "아직 기록이 없습니다.") toolsStatus.Text = "";
        if (finished && d.Last is { } done)
        {
            var (head, _) = DiagnosisTexts.Head(done.Result.Main.Number);
            Context.Toasts.Show(done.Result.Main.Number is 1 or 5 or 6 ? ToastKind.Warning : ToastKind.Info, "진단을 마쳤습니다: " + head, done.Result.Main.Found);
        }
        lastRun = d.Last;
        Context.QueueDiag();
    }

    private DiagnosisRun? lastRun;

    private static string FlushStageText(FlushStage? s) => s switch
    {
        FlushStage.Waiting => "진단이 끝나면 DNS 캐시를 비웁니다",
        FlushStage.Flushing => "DNS 캐시를 비우는 중",
        _ => "DNS 캐시를 비웠고 다시 검사하는 중",
    };

    private void RenderConclusion(DiagnosisSnapshot d)
    {
        conclusion.Children.Clear();
        if (d.Operation != DiagnosisOperation.None)
        {
            string title = d.Operation == DiagnosisOperation.Flushing ? FlushStageText(d.Flush) : $"검사 중… {d.Partial.Count}/7";
            var t = Fonts.Text(title, Typography.Headline, Ui.Brushes.Ink);
            AutomationProperties.SetAutomationId(t, "diagnose.conclusion.head");
            AutomationProperties.SetName(t, title);
            conclusion.Children.Add(t);
            conclusion.Children.Add(GlassCard.Skeleton(Ui));
            AutomationProperties.SetItemStatus(conclusionCard, "running");
            return;
        }
        if (d.Last is not { } run)
        {
            if (d.Recent is { } recent)
            {
                var (rh, _) = DiagnosisTexts.Head(recent.Conclusion);
                conclusion.Children.Add(Text("diagnose.conclusion.head", "지난 진단: " + rh, Typography.Headline, Ui.Brushes.Ink));
                conclusion.Children.Add(Text("diagnose.conclusion.found", LiveSummary.When(recent.StartedAt, Context.Now) + "에 검사한 결과입니다. 지금 상태를 보려면 빠른 진단을 시작하세요.",
                    Typography.Body, Ui.Brushes.Muted));
                AutomationProperties.SetItemStatus(conclusionCard, "recent");
            }
            else
            {
                conclusion.Children.Add(Text("diagnose.conclusion.head", "아직 진단하지 않았습니다", Typography.Headline, Ui.Brushes.Ink));
                conclusion.Children.Add(Text("diagnose.conclusion.found", "빠른 진단은 아래 일곱 가지를 한 번에 검사하고, 무엇이 문제인지와 해볼 것을 알려 줍니다 (최대 약 6초).",
                    Typography.Body, Ui.Brushes.Muted));
                AutomationProperties.SetItemStatus(conclusionCard, "empty");
            }
            foreach (var id in Order) conclusion.Children.Add(Fonts.Wrapped("· " + QuickDiagnosis.CheckName(id) + ": " + DiagnosisTexts.What(id), Typography.Caption, Ui.Brushes.Muted));
            return;
        }

        var v = DiagnosisView.Conclusion(run.Result);
        var (icon, brush) = DiagnosisLook.Weight(Ui, v.Weight);
        var headRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        headRow.Children.Add(Icons.Make(icon, brush, Size.IconL));
        var headText = Text("diagnose.conclusion.head", v.Head, Typography.Headline, Ui.Brushes.Ink);
        headText.VerticalAlignment = VerticalAlignment.Center;
        headRow.Children.Add(headText);
        conclusion.Children.Add(headRow);
        conclusion.Children.Add(Text("diagnose.conclusion.found", v.Found, Typography.Body, Ui.Brushes.Ink));
        if (v.Scope.Count > 0)
        {
            var scope = new StackPanel { Spacing = Space.S1 };
            AutomationProperties.SetAutomationId(scope, "diagnose.conclusion.scope");
            AutomationProperties.SetName(scope, string.Join(". ", v.Scope));
            foreach (var line in v.Scope) scope.Children.Add(Fonts.Wrapped("· " + line, Typography.Caption, Ui.Brushes.Muted));
            conclusion.Children.Add(scope);
        }
        if (v.Steps.Count > 0)
        {
            var steps = new StackPanel { Spacing = Space.S1 };
            AutomationProperties.SetAutomationId(steps, "diagnose.conclusion.steps");
            AutomationProperties.SetName(steps, string.Join(" ", v.Steps.Select((s, i) => $"{i + 1}. {s}")));
            steps.Children.Add(Fonts.Text("해볼 것", Typography.Label, Ui.Brushes.Ink));
            for (int i = 0; i < v.Steps.Count; i++) steps.Children.Add(Fonts.Wrapped($"{i + 1}. {v.Steps[i]}", Typography.Body, Ui.Brushes.Ink));
            conclusion.Children.Add(steps);
        }
        // 그 자리 도구 (안내형: 해볼 것을 그 자리에서)
        if (v.Number == 3)
        {
            var here = new GlassButton(Ui, ButtonKind.Secondary, "diagnose.conclusion.flush", "DNS 캐시 비우기", Icons.Refresh);
            here.Click += (_, _) => ConfirmFlush(here);
            conclusion.Children.Add(here);
        }
        else if (v.Number is 1 or 5)
        {
            var here = new GlassButton(Ui, ButtonKind.Secondary, "diagnose.conclusion.settings", "네트워크 설정 열기", Icons.Settings);
            here.Click += (_, _) => OpenSettings(SettingsPage.Network);
            conclusion.Children.Add(here);
        }
        if (v.Also.Count > 0) conclusion.Children.Add(Text("diagnose.conclusion.also", "다른 가능성: " + string.Join(" · ", v.Also), Typography.Caption, Ui.Brushes.Muted));
        foreach (var note in v.Notes) conclusion.Children.Add(Fonts.Wrapped(note, Typography.Caption, Ui.Brushes.Muted));
        if (run.Flush is { } f)
            conclusion.Children.Add(new Banner(Ui, "diagnose.flushresult", f.Ok ? BannerKind.Info : BannerKind.Warn, DiagnosisTexts.FlushMessage(f.Ok, f.Code)));
        if (DiagnosisView.Compare(run, d.SkippedRecords) is { } cmp)
        {
            var strip = new StackPanel { Spacing = Space.S1 };
            AutomationProperties.SetAutomationId(strip, "diagnose.compare");
            AutomationProperties.SetName(strip, cmp.Title + (cmp.Lines.Count > 0 ? ": " + string.Join(", ", cmp.Lines) : ""));
            strip.Children.Add(Fonts.Text(cmp.Title, Typography.Label, cmp.Lines.Count > 0 ? Ui.Brushes.Accent : Ui.Brushes.Muted));
            foreach (var line in cmp.Lines) strip.Children.Add(Fonts.Text("· " + line, Typography.Caption, Ui.Brushes.Ink));
            if (cmp.Skipped is { } sk) strip.Children.Add(Fonts.Text(sk, Typography.Caption, Ui.Brushes.Muted));
            conclusion.Children.Add(strip);
        }
        if (run.Save.Error is not null)
            conclusion.Children.Add(new Banner(Ui, "diagnose.saveerror", BannerKind.Warn, "진단 결과를 저장하지 못했습니다. 화면의 결과는 그대로입니다."));
        var copy = new GlassButton(Ui, ButtonKind.Ghost, "diagnose.copy", "결과 복사", Icons.Card);
        copy.Click += (_, _) => Copy(run);
        conclusion.Children.Add(copy);
        AutomationProperties.SetItemStatus(conclusionCard, "result");
    }

    private void RenderChecks(DiagnosisSnapshot d)
    {
        var settings = d.Settings;
        IReadOnlyList<CheckResult> shown = d.Operation == DiagnosisOperation.None ? (IReadOnlyList<CheckResult>?)d.Last?.Result.Checks ?? [] : d.Partial;
        foreach (var id in Order)
        {
            var r = shown.FirstOrDefault(c => c.Id == id);
            checkCards[id].Show(r is null ? null : DiagnosisView.Check(r, settings));
        }
    }

    private void RenderSite(DiagnosisSnapshot d)
    {
        siteRun.IsEnabled = !d.Site.Running;
        siteResult.Children.Clear();
        if (d.Site.Running)
        {
            siteResult.Children.Add(Text("diagnose.site.status", "점검 중…", Typography.Body, Ui.Brushes.Muted));
            return;
        }
        if (d.Site.Result is not { } r) return;
        siteResult.Children.Add(Text("diagnose.site.domain", r.Domain, Typography.Label, Ui.Brushes.Ink));
        foreach (var (id, c) in new[] { ("dns", r.Dns), ("https", r.Https) })
        {
            var (icon, brush) = DiagnosisLook.Judgment(Ui, c.Judgment);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
            row.Children.Add(Icons.Make(icon, brush, Size.IconS));
            string text = $"{QuickDiagnosis.CheckName(c.Id)}: {QuickDiagnosis.JudgmentName(c.Judgment)}"
                + (c.Judgment == CheckJudgment.Normal ? "" : " · " + DiagnosisTexts.Reason(c.Targets.FirstOrDefault()?.Code ?? c.Code ?? (c.State == RunState.Timeout ? "timeout" : null)));
            row.Children.Add(Text("diagnose.site." + id, text, Typography.Body, Ui.Brushes.Ink));
            siteResult.Children.Add(row);
        }
    }

    /// <summary>자동화 ID·이름이 붙은 줄바꿈 글.</summary>
    private static TextBlock Text(string id, string text, TypeStyle style, Microsoft.UI.Xaml.Media.Brush brush)
    {
        var t = Fonts.Wrapped(text, style, brush);
        AutomationProperties.SetAutomationId(t, id);
        AutomationProperties.SetName(t, text);
        return t;
    }

    private void ArrangeChecks(double width)
    {
        int n = Math.Clamp((int)(width / Size.TargetCardMin), 1, 3);
        if (n == columns) return;
        columns = n;
        checksGrid.ColumnDefinitions.Clear();
        checksGrid.RowDefinitions.Clear();
        for (int c = 0; c < n; c++) checksGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < (Order.Length + n - 1) / n; r++) checksGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < Order.Length; i++)
        {
            Grid.SetRow(checkCards[Order[i]], i / n);
            Grid.SetColumn(checkCards[Order[i]], i % n);
        }
    }

    private bool? lowerTwo;

    private void ArrangeLower(bool two)
    {
        if (lowerTwo == two) return;
        lowerTwo = two;
        lower.ColumnDefinitions.Clear();
        lower.RowDefinitions.Clear();
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (two) lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        else { lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
        Grid.SetColumn(siteCard, 0); Grid.SetRow(siteCard, 0);
        Grid.SetColumn(toolsCard, two ? 1 : 0); Grid.SetRow(toolsCard, two ? 0 : 1);
    }

    public override object? Diag()
    {
        var d = Context.Diagnosis.Snapshot;
        return new
        {
            operation = d.Operation.ToString(),
            flush = d.Flush?.ToString(),
            partial = d.Partial.Count,
            conclusion = d.Last?.Result.Main.Number,
            head = d.Last is { } l ? DiagnosisTexts.Head(l.Result.Main.Number).Head : null,
            checks = d.Last?.Result.Checks.ToDictionary(c => c.Id.ToString(), c => c.Judgment.ToString()),
            measurementId = d.Last?.MeasurementId,
            saved = d.Last?.Save.File is { } f ? Path.GetFileName(f) : null,
            saveError = d.Last?.Save.Error,
            flushResult = d.Last?.Flush is { } fr ? (fr.Ok ? "ok" : fr.Code) : null,
            compare = d.Last is { } run && DiagnosisView.Compare(run, d.SkippedRecords) is { } c ? c.Title : null,
            changes = d.Last?.Changes?.Select(x => x.Id.ToString()).ToArray(),
            recent = d.Recent?.Conclusion,
            site = new { d.Site.InputError, d.Site.Running, dns = d.Site.Result?.Dns.Judgment.ToString(), https = d.Site.Result?.Https.Judgment.ToString(), httpsState = d.Site.Result?.Https.State.ToString() },
            expanded = checkCards.Values.Where(c => c.Expanded).Select(c => c.Id.ToString()).ToArray(),
            copyState,
            lastAction,
            actions = (Context.Diagnosis.World.Actions as DemoSystemActions)?.Calls.Select(c => c.StartsWith("folder:", StringComparison.Ordinal) ? "folder:" + FolderKind(c[7..]) : c).ToArray(),
            folderEnabled = folder.IsEnabled,
        };
    }
}
