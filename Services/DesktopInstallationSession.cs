namespace Grimmcair.Desktop.Services;

/// <summary>Installer presence signal only; it never waits, kills processes, or limits application instances.</summary>
public static class DesktopInstallationSession
{
    private static readonly object Gate = new();
    private static Mutex? _presence;

    public static void HoldForCurrentProcess()
    {
        lock (Gate) _presence ??= CreatePresence(DesktopDataPaths.Current);
        // Retained by this static field until process exit, including any final shutdown work.
    }

    internal static string NameForEdition(string edition) => edition is "Community" or "Dedicated"
        ? "Local\\Grimmcair.Running." + edition : throw new ArgumentException("应用版本标识无效。", nameof(edition));

    internal static Mutex? CreatePresence(DesktopDataLocation location, string? syntheticName = null) =>
        location.IsInstalled && !location.IsIsolated ? new Mutex(false, syntheticName ?? NameForEdition(location.Edition)) : null;
}
