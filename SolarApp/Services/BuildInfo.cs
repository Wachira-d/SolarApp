using System.Reflection;

namespace SolarApp.Services;

/// <summary>
/// Which build is running. Two screenshots from the same site recently
/// showed messages from two different versions, and there was no way to
/// tell from the page which one a server had. The .NET SDK stamps the git
/// commit into the informational version ("1.0.0+e8c965a…") when it builds
/// from a repository; the DLL's write time is the build time.
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly Asm = typeof(BuildInfo).Assembly;

    public static string Commit { get; } = ReadCommit();
    public static DateTime BuiltAtUtc { get; } = ReadBuildTime();

    /// <summary>Short label, e.g. "e8c965a · 30/10 14:05".</summary>
    public static string Label =>
        $"{(Commit.Length > 0 ? Commit : "dev")} · {SiteTime.ToSiteTime(BuiltAtUtc):dd/MM HH:mm}";

    private static string ReadCommit()
    {
        var info = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        var sha = plus >= 0 ? info[(plus + 1)..] : "";
        return sha.Length > 7 ? sha[..7] : sha;
    }

    private static DateTime ReadBuildTime()
    {
        try { return File.GetLastWriteTimeUtc(Asm.Location); }
        catch { return DateTime.UtcNow; }
    }
}
