namespace Grimmcair.Desktop.Services;

/// <summary>The edition is selected at build time; connection settings cannot change it.</summary>
public static class DesktopEdition
{
    public static bool IsCommunity
    {
        get
        {
#if DESKTOP_COMMUNITY
            return true;
#else
            return false;
#endif
        }
    }

    public static bool SupportsCloud => !IsCommunity;
    public static string Name => IsCommunity ? "社区版" : "专用版";
    public static string WindowTitle => "格林姆迦尔的妙妙工具箱·" + Name;
    public static string SidebarTitle => "妙妙工具箱·" + Name;
}
