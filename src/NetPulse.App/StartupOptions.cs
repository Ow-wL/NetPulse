using System.Globalization;

namespace NetPulse.App;

/// <summary>실행 인수.</summary>
/// <param name="Demo">데모 시나리오 이름 또는 경로. 없으면 실제 측정</param>
/// <param name="DemoSpeed">데모 배속</param>
/// <param name="UserData">사용자 데이터 폴더. 없으면 %LOCALAPPDATA%\NetPulse (테스트는 실행마다 새 폴더를 준다)</param>
/// <param name="RunSeconds">0보다 크면 그 시간 뒤 스스로 닫힌다 (자동화용)</param>
public sealed record StartupOptions(string? Demo, double DemoSpeed, string UserData, int RunSeconds)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        string? demo = null, userData = null;
        double speed = 1;
        int runSeconds = 0;
        for (int i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} 뒤에 값이 필요합니다.");
            switch (args[i])
            {
                case "--demo": demo = Next(); break;
                case "--demo-speed": speed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--user-data": userData = Next(); break;
                case "--run-seconds": runSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"알 수 없는 인수입니다: {args[i]}");
            }
        }
        userData ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse");
        return new StartupOptions(demo, speed, userData, runSeconds);
    }
}
