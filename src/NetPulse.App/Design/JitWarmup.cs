using System.Reflection;
using System.Runtime.CompilerServices;

namespace NetPulse.App.Design;

/// <summary>
/// 시작 직후 한가할 때 기록 화면이 처음 쓰는 메서드를 백그라운드 스레드에서 미리 컴파일한다 (실행하지 않음).
/// 12시간 기록 상세를 처음 열 때 첫 컴파일 시간이 타임라인·아래 카드 그리기에 더해져 프레임 예산(50 ms)을 넘었다 (M7 UI 시험 1차 bench:
/// 같은 그리기가 두 번째에는 3분의 1). 화면 스레드는 기다리지 않고, 실패한 메서드는 건너뛴다.
/// </summary>
internal static class JitWarmup
{
    public static Task Start(params Type[] roots) => Task.Run(() =>
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        foreach (var root in roots)
            foreach (var type in WithNested(root))
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() is null) continue;
                    try { RuntimeHelpers.PrepareMethod(m.MethodHandle); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or BadImageFormatException) { }
                }
    });

    private static IEnumerable<Type> WithNested(Type t)
    {
        if (t.ContainsGenericParameters) yield break;
        yield return t;
        foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var x in WithNested(n)) yield return x;
    }
}
