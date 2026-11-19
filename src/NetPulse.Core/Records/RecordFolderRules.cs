using System.Text.RegularExpressions;

namespace NetPulse.Core.Records;

/// <summary>옮기기 종류 (M9 설계 B3 [2-2]).</summary>
public enum MoveKind { New, Retry, Default }

/// <summary>앱이 아는 위치 (모두 전체 경로).</summary>
/// <param name="Current">지금 기본 위치 (설정 folder 또는 기본)</param>
/// <param name="Default">기본 위치 (사용자 폴더의 records)</param>
public sealed record KnownLocations(string Current, IReadOnlyList<string> Extra, string Fallback, string UserData, string Default);

/// <summary>
/// 기록 저장 위치 검사와 경로 표시 (M9 설계 B3 [1-9], B2·C1 [1-13]).
/// </summary>
public static partial class RecordFolderRules
{
    /// <summary>긴 경로를 켜지 않은 PC의 한도 (MAX_PATH).</summary>
    public const int PathLimit = 260;

    /// <summary>
    /// 목적지 검사. 맞으면 null, 아니면 사용자에게 보일 이유 한 문장.
    /// <paramref name="resolve"/>는 실제 경로(짧은 이름·정션을 푼 것)를 돌려준다 (Platform의 최종 경로, 없으면 전체 경로).
    /// </summary>
    public static string? Check(string destination, MoveKind kind, KnownLocations known, Func<string, string?> resolve, int longestRelative)
    {
        string full;
        try { full = Trim(Path.GetFullPath(destination)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "폴더 경로를 읽지 못했습니다."; }
        if (!Directory.Exists(full)) return "폴더를 찾을 수 없습니다.";
        if (HasReparsePoint(full)) return "바로 가기 연결(정션·심볼릭 링크)이 걸린 폴더는 쓸 수 없습니다.";
        string real = Trim(resolve(full) ?? full);
        string R(string p) => Trim(resolve(p) ?? SafeFull(p));
        string current = R(known.Current), def = R(known.Default), fallback = R(known.Fallback), userData = R(known.UserData);
        bool isCurrent = Same(real, current), isDefault = Same(real, def);

        if (kind == MoveKind.New && isCurrent) return "지금 쓰고 있는 위치입니다.";
        if (kind == MoveKind.Retry && !isCurrent) return "다시 옮기기의 목적지는 지금 위치여야 합니다.";
        if (kind == MoveKind.Default && !isDefault) return "기본 위치가 아닙니다.";

        // 모든 알려진 위치와 겹치면 거부 (같음·안·품음). 종류별 예외만 허용 (B3 옮기기 종류 표)
        var others = new List<(string Path, string Name)> { (fallback, "임시 대체 위치"), (userData, "앱 데이터 폴더") };
        if (!(kind == MoveKind.Retry && isCurrent)) others.Add((current, "지금 기록 위치"));
        foreach (var e in known.Extra)
        {
            var x = R(e);
            if (kind == MoveKind.Default && Same(x, def)) continue;   // 기본 위치가 옛 위치로 남아 있으면 목적지가 된다
            others.Add((x, "옛 기록 위치"));
        }
        foreach (var (p, name) in others)
        {
            if (p.Length == 0) continue;
            // 기본 위치는 사용자 폴더 안이 정상: 기본 위치로 되돌리기와, 기본 위치로 되돌리다 끊긴 뒤의 다시 옮기기 (G4 M9 1라운드 a5)
            if ((kind is MoveKind.Default or MoveKind.Retry) && isDefault && Same(p, userData)) continue;
            if (kind == MoveKind.Retry && isCurrent && Same(p, current)) continue;
            if (Same(real, p)) return $"{name}와(과) 같은 폴더입니다.";
            if (Inside(real, p)) return $"{name} 안의 폴더는 쓸 수 없습니다.";
            if (Inside(p, real)) return $"{name}를 품은 폴더는 쓸 수 없습니다.";
        }
        if (real.Length + 1 + longestRelative >= PathLimit) return "경로가 너무 깁니다. 더 짧은 위치를 고르세요.";
        if (!Writable(real)) return "이 폴더에 쓸 수 없습니다.";
        return null;
    }

    /// <summary>경로나 그 위 폴더가 재분석 지점(정션·심볼릭 링크)인가.</summary>
    public static bool HasReparsePoint(string full)
    {
        try
        {
            for (var d = new DirectoryInfo(full); d is not null && d.Parent is not null; d = d.Parent)
                if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
    }

    private static bool Writable(string folder)
    {
        var probe = Path.Combine(folder, ".netpulse-write-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// 같은 폴더인가: 전체 경로로 바꿔(<c>.</c>·<c>..</c>·구분자 표기를 푼다) 대소문자·끝 구분자를 무시하고 비교한다
    /// (G4 M9 3라운드 a1: <c>C:\x\records\.</c> 같은 다른 표기가 같은 폴더로 보이지 않아 자기 기록을 "이미 옮겨짐"으로 지울 수 있었다).
    /// 짧은 이름·정션은 부르는 쪽이 실제 경로(resolve)로 푼 뒤 비교한다.
    /// </summary>
    public static bool Same(string a, string b) => string.Equals(Trim(SafeFull(a)), Trim(SafeFull(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="child"/>가 <paramref name="parent"/>의 아래인가 (경로 구성요소 경계).</summary>
    public static bool Inside(string child, string parent)
    {
        child = Trim(SafeFull(child)); parent = Trim(SafeFull(parent));
        return child.Length > parent.Length && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            && (child[parent.Length] == Path.DirectorySeparatorChar || parent.EndsWith(Path.DirectorySeparatorChar));
    }

    private static string Trim(string p) => p.Length > 3 ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;

    private static string SafeFull(string p)
    {
        try { return Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    // ---------------- 표시 ----------------

    private static readonly (string Name, Func<string> Path)[] Prefixes =
    [
        ("%LOCALAPPDATA%", () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        ("%TEMP%", () => System.IO.Path.GetTempPath()),
        ("%USERPROFILE%", () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
    ];

    /// <summary>
    /// 로컬 화면 표시용 (B2): 로컬 앱 데이터·임시·사용자 프로필 앞부분을 이름으로 줄인다. 그 밖의 경로는 그대로 (사용자 자신의 화면).
    /// 기록·진단 값·공개 산출물에는 경로를 쓰지 않는다.
    /// </summary>
    public static string Display(string path)
    {
        foreach (var (name, get) in Prefixes.OrderByDescending(p => p.Path().Length))   // 긴 앞부분부터 (임시 폴더는 로컬 앱 데이터 안)
        {
            var prefix = Trim(get());
            if (prefix.Length > 3 && (Same(path, prefix) || Inside(path, prefix))) return name + path[prefix.Length..];
        }
        return path;
    }

    /// <summary>
    /// 로그 글의 경로 줄이기 (C1 [1-13]): 알려진 앞부분은 이름으로, 그 밖의 드라이브 절대 경로·UNC는 <c>&lt;path&gt;</c>로.
    /// 예외 메시지·스택 줄에 쓴다.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;
        foreach (var (name, get) in Prefixes.OrderByDescending(p => p.Path().Length))   // 긴 앞부분부터 (임시 폴더는 로컬 앱 데이터 안)
        {
            var prefix = Trim(get());
            if (prefix.Length > 3) s = s.Replace(prefix, name, StringComparison.OrdinalIgnoreCase);
        }
        s = AbsolutePath().Replace(s, "<path>");
        return s;
    }

    [GeneratedRegex(@"(?<![%\w])(?:[A-Za-z]:\\|\\\\[^\\\s""'<>|]+\\)[^\s""'<>|:*?]*")]
    private static partial Regex AbsolutePath();
}
