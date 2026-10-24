namespace Autodesk.Revit.UI
{
    public enum Result { Failed = -1, Succeeded = 0, Cancelled = 1 }
    public enum UITheme { Light, Dark }
    public static class UIThemeManager { public static UITheme CurrentTheme => SketchProMax.Smoke.Dark ? UITheme.Dark : UITheme.Light; }
}
namespace SketchProMax
{
    internal static class App { public static IntPtr MainWindow => IntPtr.Zero; }
}
namespace SketchProMax.Settings
{
    public sealed class AgentSettings { public string ChatAnimations { get; set; } = "full"; public static AgentSettings Load() => new(); }
}
namespace SketchProMax.UI
{
    internal static class SplitPdfWindow { public const int DefaultMb = 18; }
}
namespace SketchProMax.Tools
{
    internal static class BerthStatusMath { public static (byte, byte, byte) RingColor(double? percent) => (0, 128, 0); }
}
