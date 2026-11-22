using NetPulse.Core.Detection;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;

namespace NetPulse.Platform;

/// <summary>
/// 실측 세계 (M5 설계 4.1): 실제 탐지·검사기·경로·링크·절전 알림. 기록은 사용자 폴더의 records, 대체 위치는 M3 기본값.
/// 측정마다 새 <see cref="SystemClock"/> (설계 3.4 [1-2]: 시계는 만들 때부터 흐르고 경과 시간이 곧 회차 번호).
/// </summary>
public sealed class LiveAppWorld(string userData) : IAppWorld
{
    private readonly string defaultFolder = Path.Combine(userData, "records");
    private volatile DiskRecordRoot primary = new(Path.Combine(userData, "records"), "primary");
    private volatile IReadOnlyList<string> extra = [];
    private readonly DiskRecordRoot fallback = DiskRecordRoot.Defaults().Fallback;

    public string DefaultRecordFolder => defaultFolder;
    public string CurrentRecordFolder => primary.Path!;
    public string FallbackRecordFolder => fallback.Path!;

    /// <summary>기본 위치(설정)와 옛 위치들 (M9 설계 B3). 다음 측정부터 새 위치에 쓴다.</summary>
    public void SetRecordFolders(string? folder, IReadOnlyList<string> extraFolders)
    {
        primary = new DiskRecordRoot(folder ?? defaultFolder, "primary");
        extra = [.. extraFolders];
    }

    private DiagnosisWorld? diagnostics;

    /// <summary>실제 검사기 (M4), 기본 진단 설정, 고정 명령 시스템 동작 (M6 설계 3.3).</summary>
    public DiagnosisWorld Diagnostics => diagnostics ??= new DiagnosisWorld(new DiagnosticProbes(), new NetPulse.Core.Diagnostics.DiagnosisSettings(), new SystemActions());

    public string Kind => "live";
    public DateTimeOffset Now => DateTimeOffset.Now;
    public IReadOnlyList<string> RecordPaths => [primary.Path!, .. extra, fallback.Path!];

    public Task<DetectionResult> DetectAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try { return DetectionPlanner.Plan(NetworkInfo.Snapshot(DetectionPlanner.RouteProbeAddress)); }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new DetectionResult(DetectionStatus.Unknown, null, "연결 정보를 읽지 못했습니다. 다시 확인하세요.", null, Tristate.Unknown, 0);
        }
    }, cancellationToken);

    public MeasurementSession StartSession(Profile profile, string appVersion)
    {
        // 기본 위치 폴더는 세션의 저장 위치 선택이 만든다 (여기서 만들다 실패하면 대체 위치로 가지 못했다, G4 M5 1라운드 A3)
        return MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = new SystemClock(), ProbeFor = t => ProbeFactory.For(t, profile.Timeout),
            Primary = primary, Fallback = fallback,
            Routes = new RouteObserver(), Power = new PowerEvents(), Links = new LinkObserver(), LinkLog = new LinkLogReader(),
            Signal = new WlanSignalReader(),   // M12 설계 C: 위치 권한이 필요 없는 호출만 (핸들은 처음 읽을 때 연다)
            AppVersion = appVersion,
        });
    }
}
