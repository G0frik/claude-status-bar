namespace ClaudeStatusBar;

/// <summary>Which build variant this is (see Directory.Build.props), shown in the menu's Version line.</summary>
internal static class BuildInfo
{
#if OFFLINE
    public const string Variant = "offline";
#else
    public const string Variant = "online";
#endif
}
