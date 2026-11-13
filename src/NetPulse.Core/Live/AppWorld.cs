using NetPulse.Core.Demo;
using NetPulse.Core.Detection;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Live;

/// <summary>
/// 측정의 바깥 입력 묶음 (M5 설계 4.1): 실측 또는 데모. 앱은 이것만 바꿔 끼우고 측정 수명·화면 값은 같은 코드를 탄다.
/// </summary>
public interface IAppWorld
{
    /// <summary>"live" 또는 "demo:이름" (기록·진단용).</summary>
    string Kind { get; }

    /// <summary>앱 벽시계 (화면의 "지금"). 측정 시계와 다르다 (측정마다 새로 만든다).</summary>
    DateTimeOffset Now { get; }

    /// <summary>기록을 찾을 위치 (기본, 대체).</summary>
    IReadOnlyList<string> RecordPaths { get; }

    Task<DetectionResult> DetectAsync(CancellationToken cancellationToken);

    /// <summary>측정 하나를 시작한다 (측정마다 새 시계). 실패하면 <see cref="SessionStartException"/>.</summary>
    MeasurementSession StartSession(Profile profile, string appVersion);
}

/// <summary>데모 세계 (M5 설계 3.4). 기록은 사용자 폴더의 records와 대체 위치(임시 폴더 아래 demo).</summary>
public sealed class DemoAppWorld : IAppWorld
{
    private readonly DemoWorld world;
    private readonly DiskRecordRoot primary;
    private readonly DiskRecordRoot fallback;
    private readonly DemoRunRules rules;

    public DemoAppWorld(DemoWorld world, string userData, DemoRunRules rules, string? fallbackPath = null)
    {
        this.world = world;
        this.rules = rules;
        primary = new DiskRecordRoot(Path.Combine(userData, "records"), "primary");
        fallback = new DiskRecordRoot(fallbackPath ?? Path.Combine(Path.GetTempPath(), "NetPulse", "demo-records"), "fallback");
    }

    public DemoWorld World => world;
    public string Kind => "demo:" + world.Scenario.Name;
    public DateTimeOffset Now => world.TimeBase.Now;
    public IReadOnlyList<string> RecordPaths => [primary.Path!, fallback.Path!];

    public Task<DetectionResult> DetectAsync(CancellationToken cancellationToken) => world.DetectAsync(cancellationToken);

    public MeasurementSession StartSession(Profile profile, string appVersion)
    {
        // 기본 위치 폴더는 세션의 저장 위치 선택이 만든다 (여기서 만들다 실패하면 대체 위치로 가지 못했다, G4 M5 1라운드 A3)
        MeasurementSession? session = null;
        // 처리 속도에 맞춘 진행: 엔진이 처리보다 20회차 넘게 앞서면 시간을 더 진행하지 않는다
        var run = world.NewRun(primary, fallback, rules, () => session is not { } s || s.Engine.State.Rounds - s.Status.Observed - s.Status.Unprocessed < 20);
        session = MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = run.Clock, ProbeFor = run.ProbeFor, Primary = run.Primary, Fallback = run.Fallback,
            Routes = run.Network, Links = run.Network, Power = run.Power, LinkLogSettle = TimeSpan.Zero, AppVersion = appVersion,
        });
        return session;
    }
}

/// <summary>앞에만 붙는 목록. 사본은 그때까지의 개수만 본다 (복사 없이 화면에 넘긴다).</summary>
public sealed class AppendOnlyList<T>
{
    private T[] items = new T[256];
    private int count;
    private readonly object gate = new();

    public void Add(T item)
    {
        lock (gate)
        {
            if (count == items.Length)
            {
                var bigger = new T[items.Length * 2];
                Array.Copy(items, bigger, count);
                items = bigger;   // 이전 사본은 이전 배열을 계속 본다 (그 범위는 바뀌지 않음)
            }
            items[count++] = item;
        }
    }

    public int Count { get { lock (gate) return count; } }

    public ArraySegmentList<T> Snapshot()
    {
        lock (gate) return new ArraySegmentList<T>(items, count);
    }
}

/// <summary>배열 앞부분의 읽기 전용 보기.</summary>
public readonly struct ArraySegmentList<T>(T[] array, int count) : IReadOnlyList<T>
{
    public T this[int index] => (uint)index < (uint)count ? array[index] : throw new ArgumentOutOfRangeException(nameof(index));
    public int Count => count;
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < count; i++) yield return array[i]; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
