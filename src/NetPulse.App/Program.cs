using System.Text;
using NetPulse.Core.Live;

namespace NetPulse.App;

/// <summary>
/// 진입점 (M12 설계 B2). XAML이 만드는 <c>Main</c> 대신 이것을 써서, 관리 코드에 들어오자마자 시작 단계 기록을 연다.
/// 그보다 앞(단일 파일 압축 풀기·.NET 런타임 시작)은 앱이 기록할 수 없다.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        StartupTrace.Begin(args);
        XamlGeneratedProgram.XamlGeneratedMain();
    }
}

/// <summary>
/// 시작 단계 기록 쓰기 (형식·해석은 <see cref="StartTrace"/>). 사용자 데이터 폴더의 <c>startup-trace.jsonl</c>.
/// 실행 ID·단계 이름·시각·종료 코드만 쓴다. 쓰기가 실패해도 앱은 계속하고, 실패는 앱 기록에 따로 남긴다.
/// </summary>
public static class StartupTrace
{
    private static readonly object Gate = new();
    private static string? path;

    public static string Run { get; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>바로 앞 실행이 정상 종료 표시 없이 끝났으면 그 실행 (앱 기록·안내용)</summary>
    public static StartTraceRun? PreviousInterrupted { get; private set; }
    /// <summary>기록 파일을 열거나 쓰지 못한 첫 예외 종류 (앱 기록에 따로 남겨 "기록 없음"과 구분한다, 설계 B2 [2-5])</summary>
    public static string? Failure { get; private set; }

    public static void Begin(string[] args)
    {
        try
        {
            string folder;
            try { folder = StartupOptions.Parse(args).UserData; }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse");
            }
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, StartTrace.FileName);
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                PreviousInterrupted = StartTrace.PreviousInterrupted(lines, Run);
                if (StartTrace.Trimmed(lines, new FileInfo(path).Length) is { } kept) File.WriteAllLines(path, kept, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Failure ??= ex.GetType().Name;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Step(StartTrace.ExitStep, Environment.ExitCode);
        Step("main");
    }

    /// <summary>단계 하나를 쓴다. 정상 종료(<see cref="StartTrace.ExitStep"/>)는 프로세스가 관리 코드로 끝날 때 한 번 (종료 코드와 함께).</summary>
    public static void Step(string step, int? exitCode = null)
    {
        if (path is null) return;
        try { lock (Gate) File.AppendAllText(path, StartTrace.Line(Run, step, DateTimeOffset.Now, exitCode) + "\n", new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Failure ??= ex.GetType().Name; }
    }
}
