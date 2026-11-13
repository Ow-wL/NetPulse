using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;

namespace NetPulse.Design.Effects;

/// <summary>OS에서 읽은 상태 (또는 시뮬레이션 값). M2 설계 5.2.</summary>
public sealed record OsState(
    bool TransparencyEnabled,
    bool AnimationsEnabled,
    bool HighContrast,
    bool EnergySaver,
    bool SystemPrefersDark,
    SystemColorSet SystemColors);

public enum MaterialMode { Acrylic, Opaque, HighContrast }

/// <summary>실제로 적용할 모드.</summary>
public sealed record EffectiveMode(
    ThemeKind Theme,
    MaterialMode Material,
    bool LightsVisible,
    bool LightsMoving,
    bool AuroraVisible,
    bool ReduceMotion,
    IReadOnlyList<string> Reasons)
{
    public bool HighContrast => Material == MaterialMode.HighContrast;
}

/// <summary>
/// 적용 모드 계산 (M2 설계 5.2). 우선순위: 고대비 &gt; 효과 줄이기(투명 효과 끔, 배터리 절약, 앱 설정, 저성능 대체) &gt; 평상시.
/// 움직임 줄이기(OS 애니메이션 끔 또는 앱 설정)는 독립이다.
/// </summary>
public static class EffectsPolicy
{
    public static EffectiveMode Compute(OsState os, AppSettings settings, bool lowPerformance)
    {
        var reasons = new List<string>();
        var theme = settings.Theme switch
        {
            ThemePreference.Light => ThemeKind.Light,
            ThemePreference.System => os.SystemPrefersDark ? ThemeKind.Dark : ThemeKind.Light,
            _ => ThemeKind.Dark,
        };

        bool reduceMotion = settings.ReduceMotion || !os.AnimationsEnabled;
        if (!os.AnimationsEnabled) reasons.Add("os-animations-off");
        if (settings.ReduceMotion) reasons.Add("app-reduce-motion");

        if (os.HighContrast)
        {
            reasons.Add("high-contrast");
            return new EffectiveMode(theme, MaterialMode.HighContrast, false, false, false, reduceMotion, reasons);
        }

        bool reduceEffects = false;
        if (!os.TransparencyEnabled) { reduceEffects = true; reasons.Add("os-transparency-off"); }
        if (os.EnergySaver) { reduceEffects = true; reasons.Add("energy-saver"); }
        if (settings.ReduceEffects) { reduceEffects = true; reasons.Add("app-reduce-effects"); }
        if (lowPerformance) { reduceEffects = true; reasons.Add("low-performance"); }

        return reduceEffects
            ? new EffectiveMode(theme, MaterialMode.Opaque, true, false, false, reduceMotion, reasons)
            : new EffectiveMode(theme, MaterialMode.Acrylic, true, !reduceMotion, settings.Aurora, reduceMotion, reasons);
    }
}
