using System.Reflection;

namespace NetPulse.App;

/// <summary>
/// 앱 버전과 빌드 커밋 (M10 설계 A1). 출처는 하나: <c>Directory.Build.props</c>의 <c>Version</c>이 만든
/// 제품 버전 <c>1.0.0+&lt;커밋 전체 SHA&gt;</c> (SDK가 SourceLink로 붙인다). 기록 머리·리포트·정보 카드가 모두 이것을 쓴다.
/// </summary>
public sealed record AppInfo(string Version, string? Commit)
{
    private static AppInfo? current;

    /// <summary>이 실행의 값. 시험 전용 <c>--fake-version</c>이 있으면 <see cref="Override"/>로 바뀐다.</summary>
    public static AppInfo Current => current ??= Parse(typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>시험 전용 (데모 + 명시한 사용자 폴더에서만 부른다): 빌드 모름 장면.</summary>
    public static void Override(string informational) => current = Parse(informational);

    /// <summary>제품 버전 글 해석 (<see cref="NetPulse.Design.AppVersionText.Parse"/>).</summary>
    public static AppInfo Parse(string? informational)
    {
        var (version, commit) = NetPulse.Design.AppVersionText.Parse(informational);
        return new(version, commit);
    }
}
