using System.Net;
using System.Text.Json;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.UiTests;

/// <summary>
/// R1 대상·프로필 편집의 저장 증거 (M11 설계 B2). 조작마다 기대 결과를 정하고, 저장 → 앱을 다시 시작해 프로필 파일과 편집기를,
/// 이어서 고른 프로필로 측정해 기록 머리의 대상 구성(순서·이름·주소·역할·방식·사용)을 본다. 시험 주소는 문서용 범위(198.51.100.0/24)만 쓴다.
/// 삭제는 "이미 저장된" 대상·프로필을 다음 실행에서 지워야 저장 증거가 된다 (M11 G4 1라운드): 첫 실행에서 저장해 두고, 두 번째 실행에서 지운다.
/// </summary>
public sealed class M11EditorTests
{
    private static JsonElement Sd(JsonElement d) => d.GetProperty("screenDiag");

    private static string Screen(JsonElement d) => M5Fixtures.Str(d, "screen");

    private static int Targets(AppDriver app) => app.WaitDiag(x => Sd(x).GetProperty("targets").GetInt32(), _ => true, "대상 수");

    private static void WaitTargets(AppDriver app, int n, string what) => app.WaitDiag(x => Sd(x).GetProperty("targets").GetInt32(), c => c == n, what);

    private static void WaitProfiles(AppDriver app, int n, string what) => app.WaitDiag(x => Sd(x).GetProperty("profiles").GetInt32(), c => c == n, what);

    private static string Value(AppDriver app, string id) => ((System.Windows.Automation.ValuePattern)app.Find(id).GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)).Current.Value;

    private static void Save(AppDriver app)
    {
        app.WaitDiag(x => Sd(x).GetProperty("saveEnabled").GetBoolean(), b => b, "저장 켜짐");
        app.Invoke("profile.save");
        app.WaitDiag(x => Sd(x).GetProperty("dirty").GetBoolean(), b => !b, "저장됨");
    }

    private sealed record Expect(string Name, string Address, TargetRole Role, ProbeMethod Method, bool Enabled);

    private static Expect[] Of(IEnumerable<Target> ts) => ts.Select(t => new Expect(t.Name, t.Address.ToString(), t.Role, t.Method, t.Enabled)).ToArray();

    [Fact]
    public void EditorChangesAreSavedSurviveARestartAndReachTheRecordHeader()
    {
        var userData = AppDriver.NewUserData();
        var original = M5Fixtures.SaveProfile(userData, "steady");   // 데모 탐지 제안: [로컬 공유기, 외부 A, 외부 B]
        Assert.Equal(3, original.Targets.Count);
        Assert.Equal(TargetRole.Local, original.Targets[0].Role);
        var o = original.Targets;
        try
        {
            // ---- 첫 실행: 편집하고, 다음 실행에서 지울 대상·프로필까지 저장해 둔다 ----
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "profile"))
            {
                app.WaitDiag(Screen, s => s == "profile", "측정 구성");
                WaitTargets(app, 3, "처음 3개");

                // 다시 탐지: 하나 지운 뒤 다시 채우면 탐지 제안 그대로 3개로 돌아온다
                app.Invoke("profile.target.2.remove");
                WaitTargets(app, 2, "대상 삭제 → 2개");
                app.Invoke("profile.redetect");
                WaitTargets(app, 3, "다시 탐지 → 3개");
                for (int i = 0; i < 3; i++) Assert.Equal(o[i].Name, Value(app, $"profile.target.{i}.name"));

                // 대상 추가 (외부가 기본: 로컬이 이미 있음) → 이름·주소 → 위로 한 칸
                app.Invoke("profile.target.add");
                WaitTargets(app, 4, "대상 추가 → 4개");
                app.SetText("profile.target.3.name", "시험 대상");
                app.SetText("profile.target.3.address", "198.51.100.77");   // 데모 외부 대상과 겹치지 않는 문서용 주소
                app.Invoke("profile.target.3.up");
                app.WaitDiag(_ => Value(app, "profile.target.2.name"), n => n == "시험 대상", "위로 → 세 번째");
                Assert.Equal(o[2].Name, Value(app, "profile.target.3.name"));

                // 역할: 시험 대상을 로컬로. 사용: 외부 A를 끔
                app.Select("profile.target.2.role.local");
                app.Toggle("profile.target.1.enabled");
                Assert.False(app.IsOn("profile.target.1.enabled"));

                // 다음 실행에서 지울 대상 (저장해 둔다)
                app.Invoke("profile.target.add");
                WaitTargets(app, 5, "하나 더 → 5개");
                app.SetText("profile.target.4.name", "지울 대상");
                app.SetText("profile.target.4.address", "198.51.100.78");

                // 프로필: 복사(현재가 됨) → "두 번째", 또 복사 → "지울 프로필" (다음 실행에서 지운다)
                app.Invoke("profile.add");
                WaitProfiles(app, 2, "프로필 복사 → 2개");
                app.SetText("profile.name", "두 번째");
                app.Invoke("profile.add");
                WaitProfiles(app, 3, "또 복사 → 3개");
                app.SetText("profile.name", "지울 프로필");
                Save(app);
                Assert.Equal(0, app.Close());
            }

            Expect firstRun(int i) => i switch
            {
                0 => new(o[0].Name, o[0].Address.ToString(), TargetRole.Local, o[0].Method, true),
                1 => new(o[1].Name, o[1].Address.ToString(), o[1].Role, o[1].Method, false),
                2 => new("시험 대상", "198.51.100.77", TargetRole.Local, ProbeMethod.Icmp, true),
                3 => new(o[2].Name, o[2].Address.ToString(), o[2].Role, o[2].Method, true),
                _ => new("지울 대상", "198.51.100.78", TargetRole.External, ProbeMethod.Icmp, true),
            };
            Expect[] saved1 = [.. Enumerable.Range(0, 5).Select(firstRun)];

            var (set1, status1, _) = ProfileStore.Load(userData, DateTimeOffset.Now);
            Assert.Equal(ProfileLoadStatus.Loaded, status1);
            Assert.Equal(3, set1!.Profiles.Count);
            var doomedProfile = Assert.Single(set1.Profiles, p => p.Name == "지울 프로필");
            var secondSaved = Assert.Single(set1.Profiles, p => p.Name == "두 번째");
            Assert.Equal(doomedProfile.Id, set1.LastUsedId);
            foreach (var p in set1.Profiles) Assert.Equal(saved1, Of(p.Targets));   // 셋 다 첫 프로필 편집본의 복사
            string doomedTarget = secondSaved.Targets.Single(t => t.Name == "지울 대상").Id;

            // ---- 두 번째 실행: 저장된 프로필·대상을 지우고, 아래로 옮기고, 방식을 TCP로 ----
            using (var app = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "profile"))
            {
                app.WaitDiag(Screen, s => s == "profile", "다시 연 측정 구성");
                Assert.Equal("지울 프로필", Value(app, "profile.name"));   // 마지막에 쓴 프로필
                WaitProfiles(app, 3, "저장된 프로필 3개");
                app.Invoke("profile.remove");
                WaitProfiles(app, 2, "저장된 프로필 삭제 → 2개");
                app.Invoke("profile.select.1");
                app.WaitDiag(_ => Value(app, "profile.name"), n => n == "두 번째", "두 번째 고름");

                WaitTargets(app, 5, "저장된 대상 5개");
                Assert.Equal("지울 대상", Value(app, "profile.target.4.name"));
                app.Invoke("profile.target.4.remove");
                WaitTargets(app, 4, "저장된 대상 삭제 → 4개");
                app.Invoke("profile.target.2.down");
                app.WaitDiag(_ => Value(app, "profile.target.3.name"), n => n == "시험 대상", "아래로 → 네 번째");
                Assert.Equal(o[2].Name, Value(app, "profile.target.2.name"));
                app.Select("profile.target.3.method.tcp");
                app.WaitDiag(_ => app.Exists("profile.target.3.port"), b => b, "포트 칸");
                Assert.Equal("443", Value(app, "profile.target.3.port"));
                Save(app);
                Assert.Equal(0, app.Close());
            }

            Expect[] expected =
            [
                firstRun(0),
                firstRun(1),
                firstRun(3),
                new("시험 대상", "198.51.100.77", TargetRole.Local, ProbeMethod.Tcp(443), true),
            ];

            // 프로필 파일: 지운 프로필·대상의 ID가 없고, 두 번째가 편집 결과·마지막 사용. 첫 프로필은 첫 실행 저장본 그대로
            var (set, status, _) = ProfileStore.Load(userData, DateTimeOffset.Now);
            Assert.Equal(ProfileLoadStatus.Loaded, status);
            Assert.Equal(2, set!.Profiles.Count);
            Assert.DoesNotContain(set.Profiles, p => p.Id == doomedProfile.Id || p.Name == "지울 프로필");
            var second = Assert.Single(set.Profiles, p => p.Name == "두 번째");
            Assert.Equal(secondSaved.Id, second.Id);
            Assert.Equal(second.Id, set.LastUsedId);
            Assert.DoesNotContain(second.Targets, t => t.Id == doomedTarget);
            Assert.Equal(expected, Of(second.Targets));
            Assert.Equal(saved1, Of(set.Profiles.Single(p => p.Id != second.Id).Targets));

            // 다시 시작: 편집기가 저장된 것을 그대로 보인다
            using (var again = AppDriver.LaunchWith(userData, "--demo", "steady", "--screen", "profile"))
            {
                again.WaitDiag(Screen, s => s == "profile", "세 번째로 연 측정 구성");
                Assert.Equal("두 번째", Value(again, "profile.name"));
                WaitProfiles(again, 2, "프로필 2개");
                Assert.Equal(4, Targets(again));
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.Equal(expected[i].Name, Value(again, $"profile.target.{i}.name"));
                    Assert.Equal(expected[i].Address, Value(again, $"profile.target.{i}.address"));
                    Assert.Equal(expected[i].Enabled, again.IsOn($"profile.target.{i}.enabled"));
                    Assert.Equal(expected[i].Method.Kind == ProbeMethodKind.Tcp, again.Exists($"profile.target.{i}.port"));
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
