using NetPulse.Core.Records;

namespace NetPulse.Core.Tests;

/// <summary>M10 (릴리스 준비) Core 시험: 경로 표시 정규화 (M9 G4 4라운드 2, M10 설계 D1).</summary>
public sealed class M10Tests
{
    private static readonly char S = Path.DirectorySeparatorChar;

    [Fact]
    public void DisplayCutsTheNormalizedPathSoDotsAndSlashesDoNotShiftIt()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var plain = Path.Combine(local, "NetPulse", "records");
        string expected = "%LOCALAPPDATA%" + S + "NetPulse" + S + "records";
        Assert.Equal(expected, RecordFolderRules.Display(plain));
        // 같은 폴더의 다른 표기: `.`, `..`, `/`, 끝 구분자
        Assert.Equal(expected, RecordFolderRules.Display(local + S + "." + S + "NetPulse" + S + "records"));
        Assert.Equal(expected, RecordFolderRules.Display(Path.Combine(local, "x", "..", "NetPulse", "records")));
        Assert.Equal(expected, RecordFolderRules.Display(plain.Replace(S, '/')));
        Assert.Equal(expected, RecordFolderRules.Display(plain + S));
        // 앞부분의 중간에 `.`이 낀 표기 (4라운드 예: 사용자 폴더 안의 `.`)
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parent = Path.GetDirectoryName(profile)!;
        var withDot = parent + S + "." + S + Path.GetFileName(profile) + S + "Documents" + S + "NetPulse";
        Assert.Equal("%USERPROFILE%" + S + "Documents" + S + "NetPulse", RecordFolderRules.Display(withDot));
    }

    [Fact]
    public void DisplayLeavesOtherPathsAsGiven()
    {
        var other = "D:" + S + "data" + S + "." + S + "records";
        Assert.Equal(other, RecordFolderRules.Display(other));
    }
}
