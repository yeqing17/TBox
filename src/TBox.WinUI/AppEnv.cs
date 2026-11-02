namespace TBox;

/// <summary>Environment switches used by the screenshot pipeline.</summary>
internal static class AppEnv
{
    /// <summary>
    /// TBOX_OPAQUE=1 disables the acrylic backdrop so <c>PrintWindow</c> captures the panel's own
    /// surface instead of sampling whatever is behind the window.
    /// </summary>
    public static bool Opaque { get; } = Environment.GetEnvironmentVariable("TBOX_OPAQUE") == "1";
}
