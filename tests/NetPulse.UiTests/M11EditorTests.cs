using System.Net;
using System.Text.Json;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.UiTests;

/// <summary>
/// R1 대상·프로필 편집의 저장 증거 (M11 설계 B2). 조작마다 기대 결과를 정하고, 저장 → 앱을 다시 시작해 프로필 파일과 편집기를,
/// 이어서 고른 프로필로 측정해 기록 머리의 대상 구성(순서·이름·주소·역할·사용)을 본다. 시험 주소는 문서용 범위(198.51.100.0/24)만 쓴다.
/// </summary>
public sealed class M11EditorTests
{
    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");

    private static string Screen(JsonElement d) => M5Fixtures.Str(d, "screen");

    private static int Targets(AppDriver app) => app.WaitDiag(x => Sd(x).GetProperty("targets").GetInt32(), _ => true, "대상 수");

    private static void WaitTargets(AppDriver app, int n, string what) => app.WaitDiag(x => Sd(x).GetProperty("targets").GetInt32(), c => c == n, what);

    private static string Value(AppDriver app, string id) => ((System.Windows.Automation.ValuePattern)app.Find(id).GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value;

    private sealed record Expect(string Name, string Address, TargetRole Role, bool Enabled);

    [Fact]
    public void EditorChangesAreSavedSurviveARestartAndReachTheRecordHeader()
    {
        var userData = AppDriver.NewUserData();
        var original = M5Fixtures.SaveProfile(userData, "steady");   // 데모 탐지 제안: [로컬 공유기, 외부 A, 외부 B]
        Assert.Equal(3, original.Targets.Count);
        Assert.Equal(TargetRole.Local, original.Targets[0].Role);
        try
        {
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "profile"))
            {
                app.WaitDiag(Screen, s => s == "profile", "측정 구성");
                WaitTargets(app, 3, "처음 3개");

                // 다시 탐지: 하나 지운 뒤 다시 채우면 탐지 제안 그대로 3개로 돌아온다
                app.Invoke("profile.target.2.remove");
                WaitTargets(app, 2, "대상 삭제 → 2개");
                app.Invoke("profile.redetect");
                WaitTargets(app, 3, "다시 탐지 → 3개");
                for (int i = 0; i < 3; i++) Assert.Equal(original.Targets[i].Name, Value(app, $"profile.target.{i}.name"));

                // 대상 추가 (외부가 기본: 로컬이 이미 있음) → 이름·주소 → 위로 한 칸
                app.Invoke("profile.target.add");
                WaitTargets(app, 4, "대상 추가 → 4개");
                app.SetText("profile.target.3.name", "시험 대상");
                app.SetText("profile.target.3.address", "198.51.100.77");   // 데모 외부 대상과 겹치지 않는 문서용 주소
                app.Invoke("profile.target.3.up");
                app.WaitDiag(_ => Value(app, "profile.target.2.name"), n => n == "시험 대상", "위로 → 세 번째");
                Assert.Equal(original.Targets[2].Name, Value(app, "profile.target.3.name"));

                // 역할: 시험 대상을 로컬로. 사용: 외부 A를 끔
                app.Select("profile.target.2.role.local");
                app.Toggle("profile.target.1.enabled");
                Assert.False(app.IsOn("profile.target.1.enabled"));

                // 지울 대상을 하나 더 넣고 지운다 (삭제가 저장까지 가는지)
                app.Invoke("profile.target.add");
                WaitTargets(app, 5, "하나 더 → 5개");
                app.SetText("profile.target.4.name", "지울 대상");
                app.SetText("profile.target.4.address", "198.51.100.78");
                app.Invoke("profile.target.4.remove");
                WaitTargets(app, 4, "삭제 → 4개");

                // 프로필: 복사(현재가 됨) → 이름 바꾸기, 또 복사 → 삭제(첫 프로필이 현재가 됨) → 두 번째를 고른다
                app.Invoke("profile.add");
                app.WaitDiag(x => Sd(x).GetProperty("profiles").GetInt32(), n => n == 2, "프로필 복사 → 2개");
                app.SetText("profile.name", "두 번째");
                app.Invoke("profile.add");
                app.WaitDiag(x => Sd(x).GetProperty("profiles").GetInt32(), n => n == 3, "또 복사 → 3개");
                app.Invoke("profile.remove");
                app.WaitDiag(x => Sd(x).GetProperty("profiles").GetInt32(), n => n == 2, "프로필 삭제 → 2개");
                app.Invoke("profile.select.1");
                app.WaitDiag(_ => Value(app, "profile.name"), n => n == "두 번째", "두 번째 고름");

                app.WaitDiag(x => Sd(x).GetProperty("saveEnabled").GetBoolean(), b => b, "저장 켜짐");
                app.Invoke("profile.save");
                app.WaitDiag(x => Sd(x).GetProperty("dirty").GetBoolean(), b => !b, "저장됨");
                Assert.Equal(0, app.Close());
            }

            Expect[] expected =
            [
                new(original.Targets[0].Name, original.Targets[0].Address.ToString(), TargetRole.Local, true),
                new(original.Targets[1].Name, original.Targets[1].Address.ToString(), original.Targets[1].Role, false),
                new("시험 대상", "198.51.100.77", TargetRole.Local, true),
                new(original.Targets[2].Name, original.Targets[2].Address.ToString(), original.Targets[2].Role, true),
            ];
            static Expect[] Of(IEnumerable<Target> ts) => ts.Select(t => new Expect(t.Name, t.Address.ToString(), t.Role, t.Enabled)).ToArray();

            // 프로필 파일: 두 프로필, 마지막 사용 = 두 번째, 두 프로필 모두 편집 결과 (두 번째는 첫째의 편집본을 복사)
            var (set, status, _) = ProfileStore.Load(userData, DateTimeOffset.Now);
            Assert.Equal(ProfileLoadStatus.Loaded, status);
            Assert.Equal(2, set!.Profiles.Count);
            var second = Assert.Single(set.Profiles, p => p.Name == "두 번째");
            Assert.Equal(second.Id, set.LastUsedId);
            Assert.Equal(expected, Of(second.Targets));
            Assert.Equal(expected, Of(set.Profiles.Single(p => p.Id != second.Id).Targets));

            // 다시 시작: 편집기가 저장된 것을 그대로 보인다
            using (var again = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "profile"))
            {
                again.WaitDiag(Screen, s => s == "profile", "다시 연 측정 구성");
                Assert.Equal("두 번째", Value(again, "profile.name"));
                Assert.Equal(4, Targets(again));
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.Equal(expected[i].Name, Value(again, $"profile.target.{i}.name"));
                    Assert.Equal(expected[i].Address, Value(again, $"profile.target.{i}.address"));
                    Assert.Equal(expected[i].Enabled, again.IsOn($"profile.target.{i}.enabled"));
                }
                Assert.False(again.Diag().GetProperty("screenDiag").GetProperty("dirty").GetBoolean());
                Assert.Equal(0, again.Close());
            }

            // 측정: 기록 머리의 대상 구성이 고른 프로필의 켠 대상과 같다 (기록 머리는 켠 대상만 담는다: Profile.ActiveTargets)
            using (var run = AppDriver.LaunchWith(userData, "--demo", "steady", "--demo-speed", "60", "--start"))
            {
                run.WaitDiag(x => x.GetProperty("rounds").GetInt32(), r => r >= 3, "3회차");
                var session = M5Fixtures.Str(run.Diag(), "session");
                run.Invoke("measure.stop");
                run.WaitDiag(x => x.GetProperty("finished").ValueKind == JsonValueKind.Object, b => b, "멈춤");
                Assert.Equal(0, run.Close());
                var record = RecordReader.Read(RecordReader.FindSegments(session, M5Fixtures.RecordRoots(userData)));
                Assert.Equal(expected.Where(e => e.Enabled).ToArray(), Of(record.Header!.Targets));
                Assert.Equal(second.Targets.Where(t => t.Enabled).Select(t => t.Id), record.Header.Targets.Select(t => t.Id));
            }
        }
        finally { AppDriver.DeleteUserData(userData); }
    }
}
