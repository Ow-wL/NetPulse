using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.App.Resident;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;
using NetPulse.Core.Records;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 설정 (M5 설계 4.5 → M9 설계 B2): 보기, 상주와 알림, 기록 저장 위치(바꾸기·옮기기), 진단 기본값, 측정 구성 열기.
/// 모든 저장은 <see cref="SettingsStore"/>를 거친다. 저장할 수 없는 실행(새 판 설정 파일)이면 위에 띠, 옮기기는 꺼진다.
/// </summary>
public sealed class SettingsScreen : Screen
{
    private enum RecordsState { Normal, Invalid, Busy, Moving, Done, Leftover, Partial, Stopped, Failed, Conflict, Interrupted, JournalProblem }

    private readonly PageHeader header;
    private readonly GroupPanel appearance, resident, records, diagnosis, measurement;
    private readonly StackPanel stack = new() { Spacing = Space.S5 };
    private readonly SettingToggle closeToTray, notifyDropout, notifyRecovered, notifyFinished;
    private readonly TextBlock residentNote;
    private readonly TextBlock folderText, recordsStatus;
    private readonly Meter progress;
    private readonly GlassButton change, toDefault, retry, stop;
    private readonly GlassTextBox tcp0, tcp1, domain0, domain1;
    private readonly GlassButton diagSave, diagReset;
    private readonly TextBlock diagStatus;
    private RecordsState state;
    private string? reason;
    private (int Done, int Total) moved;
    private CancellationTokenSource? moveStop;
    private bool diagDirty;
    /// <summary>칸마다 마지막으로 반영한 글 (저장 시도·기본값 때 다시 잡는다). 늦게 오는 TextChanged가 이미 반영한 글을 "고침"으로 되돌리지 않게.</summary>
    private readonly Dictionary<GlassTextBox, string> diagSeen = [];
    private string diagState = "normal";
    private IReadOnlyDictionary<string, string> diagErrors = new Dictionary<string, string>();

    private ResidentServices R => Context.Resident;
    private SettingsStore Store => Context.Settings;

    public SettingsScreen(ScreenContext context) : base(context, "settings")
    {
        RowSpacing = Space.S5;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        header = new PageHeader(Ui, "settings.header", null, "설정");
        Children.Add(header);
        if (!Store.CanPersist)
            stack.Children.Add(new Banner(Ui, "settings.notPersisted", BannerKind.Warn, "설정 파일을 읽지 못해 이번 실행의 변경은 저장되지 않습니다."));

        // ---- 보기 ----
        var s = Ui.Effects.Settings;
        var theme = new SegmentedChoice(Ui, "settings.theme", "테마", [("dark", "다크"), ("light", "라이트"), ("system", "Windows 설정 따르기")],
            s.Theme switch { ThemePreference.Light => "light", ThemePreference.System => "system", _ => "dark" });
        theme.Changed += v =>
        {
            Context.SetRevealOrigin(theme.Item(v));
            Ui.Effects.UpdateSettings(Ui.Effects.Settings with { Theme = v switch { "light" => ThemePreference.Light, "system" => ThemePreference.System, _ => ThemePreference.Dark } }, persist: true);
        };
        var aurora = Toggle("settings.aurora", "연한 오로라", s.Aurora, v => UpdateLook(x => x with { Aurora = v }));
        var effects = Toggle("settings.reduceEffects", "효과 줄이기 (유리 대신 불투명한 표면)", s.ReduceEffects, v => UpdateLook(x => x with { ReduceEffects = v }));
        var motion = Toggle("settings.reduceMotion", "움직임 줄이기", s.ReduceMotion, v => UpdateLook(x => x with { ReduceMotion = v }));
        (appearance, var appearanceBody) = ScreenParts.Card(Ui, "settings.appearance", "보기", "Windows의 투명 효과·애니메이션 설정도 따릅니다");
        appearanceBody.Set(Stack(theme, aurora, effects, motion));

        // ---- 상주와 알림 ----
        var r = Store.Current.Resident;
        closeToTray = Toggle("settings.closeToTray", "측정 중 창을 닫으면 트레이에서 계속", r.CloseToTray, v => UpdateResident(x => x with { CloseToTray = v }));
        notifyDropout = Toggle("settings.notifyDropout", "끊김이 관측되면 알림", r.NotifyDropout, v => UpdateResident(x => x with { NotifyDropout = v }));
        notifyRecovered = Toggle("settings.notifyRecovered", "끊김 관측이 끝났을 때도 알림", r.NotifyRecovered, v => UpdateResident(x => x with { NotifyRecovered = v }));
        notifyFinished = Toggle("settings.notifyFinished", "측정이 끝났을 때 알림", r.NotifyFinished, v => UpdateResident(x => x with { NotifyFinished = v }));
        residentNote = Fonts.Wrapped("", Typography.Caption, Ui.Brushes.Muted);
        // 긴 설명은 카드 머리(한 줄, 줄임표)가 아니라 본문에 줄바꿈 글로 둔다 (150% 배율에서 잘리지 않게)
        (resident, var residentBody) = ScreenParts.Card(Ui, "settings.resident", "상주와 알림");
        residentBody.Set(Stack(About("settings.resident.about",
            "창이 앞에 있으면 앱 안에서, 숨겨져 있거나 다른 창 뒤에 있으면 Windows 알림으로도 알립니다. Windows 알림 설정·방해 금지·정책에 따라 나타나지 않을 수 있습니다."),
            closeToTray, notifyDropout, notifyRecovered, notifyFinished, residentNote));

        // ---- 기록 저장 위치 ----
        folderText = Fonts.Wrapped("", Typography.Body, Ui.Brushes.Ink);
        AutomationProperties.SetAutomationId(folderText, "settings.records.folder");
        recordsStatus = Fonts.Wrapped("", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(recordsStatus, "settings.records.status");
        progress = new Meter(Ui, "settings.records.progress") { Visibility = Visibility.Collapsed };
        change = new GlassButton(Ui, ButtonKind.Secondary, "settings.records.change", "폴더 바꾸기", Icons.Pencil);
        toDefault = new GlassButton(Ui, ButtonKind.Ghost, "settings.records.default", "기본 위치로");
        retry = new GlassButton(Ui, ButtonKind.Secondary, "settings.records.retry", "다시 옮기기", Icons.Refresh);
        stop = new GlassButton(Ui, ButtonKind.Ghost, "settings.records.stop", "멈추기", Icons.Stop);
        change.Click += (_, _) => _ = ChangeAsync(MoveKind.New, change);
        toDefault.Click += (_, _) => _ = ChangeAsync(MoveKind.Default, toDefault);
        retry.Click += (_, _) => _ = ChangeAsync(MoveKind.Retry, retry);
        stop.Click += (_, _) => moveStop?.Cancel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        buttons.Children.Add(change);
        buttons.Children.Add(toDefault);
        buttons.Children.Add(retry);
        buttons.Children.Add(stop);
        (records, var recordsBody) = ScreenParts.Card(Ui, "settings.records", "기록 저장 위치");
        recordsBody.Set(Stack(About("settings.records.about", "새 측정을 저장할 폴더입니다. 바꾸면 지금까지의 기록도 확인한 뒤 옮깁니다."),
            folderText, buttons, progress, recordsStatus));
        state = R.RecordStartup.JournalProblem is not null ? RecordsState.JournalProblem : R.RecordStartup.Interrupted ? RecordsState.Interrupted : RecordsState.Normal;

        // ---- 진단 기본값 ----
        var cur = Context.Diagnosis.CurrentSettings;
        tcp0 = DiagField("settings.diag.tcp.0", "외부 TCP 주소 1", cur.TcpTargets.ElementAtOrDefault(0)?.ToString() ?? "");
        tcp1 = DiagField("settings.diag.tcp.1", "외부 TCP 주소 2", cur.TcpTargets.ElementAtOrDefault(1)?.ToString() ?? "");
        domain0 = DiagField("settings.diag.domain.0", "확인 도메인 1", cur.Domains.ElementAtOrDefault(0) ?? "");
        domain1 = DiagField("settings.diag.domain.1", "확인 도메인 2", cur.Domains.ElementAtOrDefault(1) ?? "");
        diagSave = new GlassButton(Ui, ButtonKind.Primary, "settings.diag.save", "저장", Icons.Check) { IsEnabled = false };
        diagReset = new GlassButton(Ui, ButtonKind.Ghost, "settings.diag.reset", "기본값으로");
        diagSave.Click += (_, _) => SaveDiagnosis();
        diagReset.Click += (_, _) => ResetDiagnosis();
        diagStatus = Fonts.Wrapped("진행 중인 진단은 시작할 때의 값으로 끝까지 돕니다.", Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(diagStatus, "settings.diag.status");
        var diagButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        diagButtons.Children.Add(diagSave);
        diagButtons.Children.Add(diagReset);
        (diagnosis, var diagnosisBody) = ScreenParts.Card(Ui, "settings.diagnosis", "진단 기본값");
        diagnosisBody.Set(Stack(About("settings.diagnosis.about", "빠른 진단이 확인할 외부 주소와 도메인입니다. 서로 다른 사업자의 주소를 두 개 적으세요."),
            tcp0, tcp1, domain0, domain1, diagButtons, diagStatus));

        // ---- 측정 구성 ----
        var open = new GlassButton(Ui, ButtonKind.Secondary, "settings.profile", "측정 구성 열기", Icons.Pencil);
        open.Click += (_, _) => { ProfileEditor.ReturnTo = "settings"; Context.Navigate("profile", null); };
        (measurement, var measurementBody) = ScreenParts.Card(Ui, "settings.measurement", "측정", null);
        measurementBody.Set(Stack(Fonts.Wrapped("측정 대상, 측정 방식(Ping·TCP), 측정 시간, 예상 연결을 바꿉니다.", Typography.Body, Ui.Brushes.Muted), open));

        foreach (var card in new UIElement[] { appearance, resident, records, diagnosis, measurement }) stack.Children.Add(card);
        var scroll = new ScrollViewer { Content = stack, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        AutomationProperties.SetAutomationId(scroll, "settings.scroll");
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
        Store.Changed += _ => DispatcherQueue.TryEnqueue(RenderRecords);
        R.WorkGate.Reopened += () => DispatcherQueue.TryEnqueue(RenderRecords);
        RenderResident();
        RenderRecords();
    }

    public override IReadOnlyList<UIElement> Parts => [header, appearance, resident, records, diagnosis, measurement];

    public override void Render(HostSnapshot snapshot) => RenderRecords();

    public override void Entered()
    {
        CheckFolder();
        RenderResident();
        RenderRecords();
    }

    /// <summary>지금 기본 위치를 쓸 수 있는가 (들어올 때·옮긴 뒤 한 번 검사, 그리기마다 디스크를 보지 않는다).</summary>
    private bool folderUsable = true;

    private void CheckFolder()
    {
        var folder = Context.Host.World.CurrentRecordFolder;
        _ = Task.Run(() =>
        {
            bool ok;
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ".netpulse-write-" + Guid.NewGuid().ToString("N")[..8]);
                File.WriteAllText(probe, "");
                File.Delete(probe);
                ok = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { ok = false; }
            DispatcherQueue.TryEnqueue(() => { folderUsable = ok; RenderRecords(); });
        });
    }

    // ---------------- 부품 ----------------

    private TextBlock About(string id, string text)
    {
        var t = Fonts.Wrapped(text, Typography.Caption, Ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(t, id);
        return t;
    }

    private SettingToggle Toggle(string id, string label, bool value, Action<bool> set)
    {
        var t = new SettingToggle(Ui, id, label, value);
        t.Checked += (_, _) => set(true);
        t.Unchecked += (_, _) => set(false);
        return t;
    }

    private static StackPanel Stack(params UIElement[] items)
    {
        var p = new StackPanel { Spacing = Space.S4 };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    private GlassTextBox DiagField(string id, string label, string value)
    {
        // 폭이 정해진 칸은 왼쪽에 (기본 늘이기 정렬이면 카드 가운데에 떠 보였다)
        var f = new GlassTextBox(Ui, id, label, value, null, Size.FieldM) { HorizontalAlignment = HorizontalAlignment.Left };
        diagSeen[f] = value;
        f.Box.TextChanged += (_, _) =>
        {
            if (f.Text == diagSeen[f]) return;
            diagSeen[f] = f.Text;
            diagDirty = true;
            diagState = "dirty";
            RenderDiagnosis();
        };
        return f;
    }

    private void UpdateLook(Func<AppSettings, AppSettings> change) => Ui.Effects.UpdateSettings(change(Ui.Effects.Settings), persist: true);

    private void UpdateResident(Func<ResidentSettings, ResidentSettings> change)
    {
        var result = Store.Update(f => f with { Resident = change(f.Resident) });
        if (result == SettingsSaveResult.Failed) Context.Toasts.Show(ToastKind.Error, "설정을 저장하지 못했습니다", "다시 시도하세요. 이번 실행에는 적용됩니다.");
        Context.QueueDiag();
    }

    private void RenderResident()
    {
        bool tray = Context.TrayAvailable();
        closeToTray.IsEnabled = tray;
        residentNote.Text = tray ? "" : "트레이를 쓸 수 없어 창을 닫으면 측정을 멈추고 종료합니다.";
        residentNote.Visibility = tray ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- 기록 저장 위치 ----------------

    private bool MeasuringNow => Context.Host.Snapshot.State is HostState.Starting or HostState.Measuring or HostState.Stopping;

    public const string DemoDefaultFolder = @"%LOCALAPPDATA%\NetPulse\records", DemoCustomFolder = @"%USERPROFILE%\Documents\NetPulse 기록 (데모)";

    private string FolderDisplay()
    {
        var world = Context.Host.World;
        bool isDefault = RecordFolderRules.Same(world.CurrentRecordFolder, world.DefaultRecordFolder);
        // 데모 화면에는 실제 경로 대신 고정 합성 경로 (공개 캡처에 사용자 이름·드라이브·공유 이름이 찍히지 않게, B2 [1-13]).
        // 사용자 지정 위치도 합성한다 (G4 M9 1라운드 b5: 데모에서 다른 위치로 바꾸면 실제 경로가 보였다)
        if (world is DemoAppWorld) return isDefault ? DemoDefaultFolder : DemoCustomFolder;
        return RecordFolderRules.Display(world.CurrentRecordFolder);
    }

    private void RenderRecords()
    {
        if (Context.IsClosing()) return;
        var world = Context.Host.World;
        bool isDefault = RecordFolderRules.Same(world.CurrentRecordFolder, world.DefaultRecordFolder);
        folderText.Text = FolderDisplay() + (isDefault ? " (기본 위치)" : "");
        bool moving = state == RecordsState.Moving;
        bool writable = R.Moves is not null && Store.RecordsWritable;
        bool canAct = !moving && !MeasuringNow && writable;
        change.IsEnabled = canAct;
        toDefault.IsEnabled = canAct && !isDefault;
        bool leftovers = Store.Current.Records.ExtraFolders.Count > 0;
        retry.Visibility = leftovers || state is RecordsState.Interrupted or RecordsState.Partial or RecordsState.Stopped ? Visibility.Visible : Visibility.Collapsed;
        retry.IsEnabled = canAct && leftovers;
        stop.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        progress.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        if (moving) progress.Set(moved.Total == 0 ? 0 : (double)moved.Done / moved.Total, $"기록 옮기는 중 {moved.Done}/{moved.Total}");
        string text = state switch
        {
            RecordsState.Moving => $"기록을 옮기는 중입니다 ({moved.Done}/{moved.Total}). 이동 중에는 측정·메모·리포트 내보내기를 할 수 없습니다.",
            RecordsState.Done => "옮겼습니다. 새 측정은 이 위치에 저장됩니다.",
            RecordsState.Leftover => "옮겼습니다. 옛 위치에 정리하지 못한 파일이 있습니다 (기록은 새 위치에서 엽니다).",
            RecordsState.Partial => $"일부만 옮겼습니다 ({moved.Done}/{moved.Total}). {reason} 옛 위치의 기록도 계속 목록에 보입니다.",
            RecordsState.Stopped => $"멈췄습니다 ({moved.Done}/{moved.Total}). 옛 위치의 기록도 계속 목록에 보입니다.",
            RecordsState.Failed => $"옮기지 못했습니다. {reason} 옛 기록은 그대로 목록에 보입니다.",
            RecordsState.Conflict => reason ?? "새 위치에 같은 이름의 다른 기록이 있습니다.",
            RecordsState.Invalid or RecordsState.Busy => reason ?? "",
            RecordsState.Interrupted => "지난번 옮기기가 중간에 끊겼습니다. 옛 위치의 기록도 계속 목록에 보입니다.",
            RecordsState.JournalProblem => "옮기기 기록을 확인하지 못했습니다. 기록 폴더는 그대로 두었습니다.",
            _ => "",
        };
        if (state == RecordsState.Normal || state == RecordsState.Done)
        {
            if (!writable) text = "설정을 저장할 수 없는 실행이라 저장 위치를 바꿀 수 없습니다.";
            else if (!folderUsable) text = "이 위치를 쓸 수 없습니다. 지금 측정은 대체 위치에 저장됩니다.";
            else if (MeasuringNow) text = "측정 중에는 바꿀 수 없습니다.";
            else if (leftovers && state == RecordsState.Normal) text = $"옛 위치 {Store.Current.Records.ExtraFolders.Count}곳의 기록도 목록에 보입니다.";
        }
        recordsStatus.Text = text;
        recordsStatus.Foreground = state is RecordsState.Failed or RecordsState.Conflict or RecordsState.Invalid or RecordsState.JournalProblem ? Ui.Brushes.Bad
            : state is RecordsState.Partial or RecordsState.Stopped or RecordsState.Interrupted or RecordsState.Busy or RecordsState.Leftover || !folderUsable ? Ui.Brushes.Warn : Ui.Brushes.Muted;
        recordsStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Context.QueueDiag();
    }

    private async Task ChangeAsync(MoveKind kind, Control opener)
    {
        if (state == RecordsState.Moving || MeasuringNow) return;
        string? picked = null;
        if (kind == MoveKind.New)
        {
            picked = Context.Options.PickFolder ?? await PickAsync();
            if (picked is null || Context.IsClosing()) return;
        }
        var moves = R.Moves;
        var preview = await Task.Run(() => moves.Preview(kind, picked));
        if (Context.IsClosing()) return;
        if (preview.Reason is not null)
        {
            state = preview.Conflicts.Count > 0 ? RecordsState.Conflict : RecordsState.Invalid;
            reason = preview.Reason;
            Context.Log.Write("recordsMove", new { step = "refused", kind = kind.ToString(), conflicts = preview.Conflicts.Count });
            RenderRecords();
            return;
        }
        if (preview.Count == 0) { await RunAsync(kind, picked); return; }
        void Closed(bool confirmed)
        {
            Context.Dialog.Closed -= Closed;
            if (confirmed) _ = RunAsync(kind, picked);
        }
        Context.Dialog.Closed += Closed;
        Context.Dialog.Open(opener, "기록을 옮길까요?",
            $"기록 {preview.Count}개(약 {Math.Max(1, preview.Bytes / (1024 * 1024))} MB)를 새 위치로 옮깁니다. 옮기는 동안 측정·메모·리포트 내보내기를 할 수 없습니다.",
            "옮기고 바꾸기", "취소");
    }

    private async Task<string?> PickAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Context.WindowHandle());
        picker.FileTypeFilter.Add("*");
        try { return (await picker.PickSingleFolderAsync())?.Path; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return null; }
    }

    private async Task RunAsync(MoveKind kind, string? picked)
    {
        state = RecordsState.Moving;
        moved = (0, 0);
        reason = null;
        moveStop = new CancellationTokenSource();
        RenderRecords();
        Context.Log.Write("recordsMove", new { step = "start", kind = kind.ToString() });
        var progressSink = new Progress<(int Done, int Total)>(p => { moved = p; RenderRecords(); });
        MoveReport report;
        try
        {
            var moves = R.Moves;
            var token = moveStop.Token;
            report = await Task.Run(() => moves.RunAsync(kind, picked, progressSink, token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = new MoveReport(MoveState.Failed, 0, 0, new Dictionary<string, string>(), [], [], "파일을 읽거나 쓰지 못했습니다.");
        }
        moveStop = null;
        moved = (report.Moved, report.Total);
        state = report.State switch
        {
            MoveState.Done when report.Leftovers.Count > 0 => RecordsState.Leftover,
            MoveState.Done => RecordsState.Done,
            MoveState.Partial => RecordsState.Partial,
            MoveState.Stopped => RecordsState.Stopped,
            MoveState.Conflict => RecordsState.Conflict,
            MoveState.Busy => RecordsState.Busy,
            MoveState.Invalid or MoveState.NotPersisted => RecordsState.Invalid,
            _ => RecordsState.Failed,
        };
        reason = report.Reason ?? (report.Failed.Count > 0 ? $"옮기지 못한 기록 {report.Failed.Count}개." : null);
        if (report.Leftovers.Count > 0 && state != RecordsState.Leftover) reason = (reason is null ? "" : reason + " ") + "옛 위치에 정리하지 못한 파일이 있습니다(기록은 새 위치에서 엽니다).";
        CheckFolder();
        Context.Log.Write("recordsMove", new
        {
            step = "end", state = report.State.ToString(), report.Moved, report.Total, failed = report.Failed.Values, leftovers = report.Leftovers.Count,
            folder = Store.Current.Records.Folder is null ? "default" : "custom", extra = Store.Current.Records.ExtraFolders.Count,
        });
        RenderRecords();
    }

    /// <summary>창을 닫을 때: 옮기기에 멈춤을 보낸다 (지금 기록을 끝내거나 되돌리고 멈춘다, A1 표).</summary>
    public void StopMove() => moveStop?.Cancel();

    // ---------------- 진단 기본값 ----------------

    private void RenderDiagnosis()
    {
        foreach (var (f, key) in new[] { (tcp0, "tcp.0"), (tcp1, "tcp.1"), (domain0, "domain.0"), (domain1, "domain.1") })
            f.SetError(diagErrors.GetValueOrDefault(key));
        diagSave.IsEnabled = diagDirty;
        diagStatus.Text = diagState switch
        {
            "saved" => "저장했습니다. 다음 진단부터 이 값으로 확인합니다.",
            "failed" => "설정을 저장하지 못했습니다. 이번 실행에는 적용됩니다.",
            "notPersisted" => "이번 실행에만 적용합니다. 설정 파일이 이 앱보다 새 판이라 저장하지 않았습니다.",
            "errors" => "고칠 칸이 있습니다.",
            _ => "진행 중인 진단은 시작할 때의 값으로 끝까지 돕니다.",
        };
        diagStatus.Foreground = diagState is "errors" or "failed" ? Ui.Brushes.Bad : diagState == "notPersisted" ? Ui.Brushes.Warn : Ui.Brushes.Muted;
        Context.QueueDiag();
    }

    private void SaveDiagnosis()
    {
        string[] tcp = [tcp0.Text.Trim(), tcp1.Text.Trim()], domains = [domain0.Text.Trim(), domain1.Text.Trim()];
        MarkDiagSeen();
        diagErrors = DiagnosisDefaults.Validate(tcp, domains);
        if (diagErrors.Count > 0) { diagState = "errors"; RenderDiagnosis(); return; }
        var result = Store.Update(f => f with { Diagnosis = new DiagnosisPrefs(tcp, domains) });
        App.ApplyDiagnosisDefaults(Context.Diagnosis, Context.Host.World.Diagnostics.Settings, Store, Context.Log);
        diagDirty = false;
        diagState = DiagSaved(result);
        RenderDiagnosis();
    }

    /// <summary>저장 결과 → 카드 상태. 저장할 수 없는 실행(새 판)은 "저장됨"이 아니다 (설계 4절 진단 카드 "설정 저장 불가").</summary>
    private static string DiagSaved(SettingsSaveResult result) => result switch
    {
        SettingsSaveResult.Failed => "failed",
        SettingsSaveResult.NotPersisted => "notPersisted",
        _ => "saved",
    };

    private void MarkDiagSeen()
    {
        foreach (var f in new[] { tcp0, tcp1, domain0, domain1 }) diagSeen[f] = f.Text;
    }

    private void ResetDiagnosis()
    {
        var result = Store.Update(f => f with { Diagnosis = DiagnosisPrefs.Default });
        App.ApplyDiagnosisDefaults(Context.Diagnosis, Context.Host.World.Diagnostics.Settings, Store, Context.Log);
        var cur = Context.Diagnosis.CurrentSettings;
        tcp0.Text = cur.TcpTargets.ElementAtOrDefault(0)?.ToString() ?? "";
        tcp1.Text = cur.TcpTargets.ElementAtOrDefault(1)?.ToString() ?? "";
        domain0.Text = cur.Domains.ElementAtOrDefault(0) ?? "";
        domain1.Text = cur.Domains.ElementAtOrDefault(1) ?? "";
        MarkDiagSeen();
        diagErrors = new Dictionary<string, string>();
        diagDirty = false;
        diagState = DiagSaved(result);
        RenderDiagnosis();
    }

    public override object? Diag() => new
    {
        persist = Store.CanPersist,
        records = new
        {
            state = state.ToString(),
            folder = RecordFolderRules.Same(Context.Host.World.CurrentRecordFolder, Context.Host.World.DefaultRecordFolder) ? "default" : "custom",
            extra = Store.Current.Records.ExtraFolders.Count,
            moved = moved.Done, total = moved.Total, reason, usable = folderUsable,
            changeEnabled = change.IsEnabled, retryVisible = retry.Visibility == Visibility.Visible,
        },
        diagnosis = new { state = diagState, errors = diagErrors.Keys, probed = (Context.Diagnosis.Probes as NetPulse.Core.Demo.DemoDiagnosticProbes)?.Probed },
        resident = new
        {
            closeToTray = closeToTray.IsChecked == true, tray = Context.TrayAvailable(),
            notifyDropout = notifyDropout.IsChecked == true, notifyRecovered = notifyRecovered.IsChecked == true, notifyFinished = notifyFinished.IsChecked == true,
        },
    };
}
