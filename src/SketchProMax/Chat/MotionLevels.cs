namespace SketchProMax.Chat;

/// <summary>How much the chat pane moves (Settings, "Chat animations").</summary>
public enum Motion { Full, Reduced, Off }

/// <summary>The animation setting's stored names, labels and hints. No WPF here, so the unit tests can check it.</summary>
public static class MotionLevels
{
    public static readonly Motion[] All = [Motion.Full, Motion.Reduced, Motion.Off];

    /// <summary>The level stored in settings.json; empty or unknown (files written before the setting) reads as Full.</summary>
    public static Motion Parse(string? stored) => (stored ?? "").Trim().ToLowerInvariant() switch
    {
        "reduced" => Motion.Reduced,
        "off" => Motion.Off,
        _ => Motion.Full,
    };

    public static string Store(Motion level) => level switch
    {
        Motion.Reduced => "reduced",
        Motion.Off => "off",
        _ => "full",
    };

    public static string Label(Motion level) => level switch
    {
        Motion.Reduced => "Reduced",
        Motion.Off => "Off",
        _ => "Full",
    };

    public static string Hint(Motion level) => level switch
    {
        Motion.Reduced => "Only quick opens and closes and the small progress spinners; nothing else loops, fades in or glows",
        Motion.Off => "Every change shows at once and the taskbar never flashes; only the small progress spinners turn",
        _ => "The thinking mark, new words fading in, steps ticking off, Send and Stop morphing, and a glow when a long task is done",
    };
}
