using System.Windows;
using System.Windows.Media;
using CDT.Wpf;

namespace SketchProMax.UI;

/// <summary>
/// The CDT SDK's theme (CDT.Wpf, merged into this DLL) under every Sketch ProMax window: its control styles
/// (CDT.PrimaryButton, CDT.TextBox, CDT.ComboBox, CDT.DataGrid, …), its palette and its fonts. <see cref="Brand"/>
/// merges <see cref="Styles"/> into each window it themes and writes the palette of Revit's theme into the window's own
/// resources (<see cref="WritePalette"/>), so one shared copy of the 7,000-line style sheet serves every window and each
/// window still follows Revit's light or dark theme on its own. Anything that fails here leaves the window on Sketch
/// ProMax's own styles: a styling problem never stops a window from opening.
/// </summary>
internal static class CdtTheme
{
    private static ResourceDictionary? _styles;
    private static bool _tried;

    /// <summary>Why the SDK's theme didn't load (shown by the field test), or null when it loaded or wasn't tried yet.</summary>
    public static string? Error { get; private set; }

    /// <summary>The SDK's styles with its light palette (each window overrides the palette), or null when it can't load.</summary>
    public static ResourceDictionary? Styles
    {
        get
        {
            if (_tried) return _styles;
            _tried = true;
            try { _styles = new ThemeDictionary(AppTheme.Light, ThemeBrand.Cdt); }
            catch (Exception ex) { Error = ex.GetType().Name + ": " + ex.Message; _styles = null; }
            return _styles;
        }
    }

    /// <summary>A CDT style by key (CDT.PrimaryButton), or null when the theme or the key is missing.</summary>
    public static Style? Style(string key) => Styles is { } s && s.Contains(key) ? s[key] as Style : null;

    /// <summary>
    /// Writes the SDK palette of the given theme (its CDT.*Color colours and CDT.* brushes) into a window's own
    /// resources, which win over the shared dictionary's palette.
    /// </summary>
    public static void WritePalette(ResourceDictionary target, bool dark)
    {
        if (Styles is null) return;
        try
        {
            var theme = dark ? AppTheme.Dark : AppTheme.Light;
            foreach (System.Collections.DictionaryEntry entry in ThemeManager.GetPalette(theme, ThemeBrand.Cdt))
            {
                if (entry.Value is Color or Brush) target[entry.Key] = entry.Value;
            }
        }
        catch (Exception ex) { Error ??= ex.GetType().Name + ": " + ex.Message; }
    }

    /// <summary>Puts the SDK's current theme (what CdtMessageBox opens in) on Revit's.</summary>
    public static void Follow(bool dark)
    {
        if (Styles is null) return;
        var theme = dark ? AppTheme.Dark : AppTheme.Light;
        try { if (ThemeManager.Current != theme) ThemeManager.Apply(theme, ThemeBrand.Cdt, Styles); }
        catch (Exception ex) { Error ??= ex.GetType().Name + ": " + ex.Message; }
    }

    /// <summary>Colours the window's native title bar to the theme (Windows 10 2004+; ignored elsewhere).</summary>
    public static void TitleBar(Window window, bool dark)
    {
        try { WindowTitleBarStyler.ApplyTitleBar(window, dark ? AppTheme.Dark : AppTheme.Light); }
        catch (Exception) { /* an older Windows: keep the system title bar */ }
    }

    /// <summary>The SDK's focus rules on a window: a click on empty space or Esc in a field clears the field's focus.</summary>
    public static void Focus(Window window)
    {
        try { FocusAssist.SetIsEnabled(window, true); }
        catch (Exception) { }
    }
}

/// <summary>
/// Sketch ProMax's message box: the SDK's themed CdtMessageBox (same overloads as System.Windows.MessageBox, Enter on
/// the default button, Esc for the negative answer). Every window in SketchProMax.UI calls MessageBox.Show; this class,
/// being in that namespace, is the one those calls reach. It falls back to the Windows message box on its own.
/// </summary>
internal static class MessageBox
{
    private static void Sync() => CdtTheme.Follow(Brand.IsDark);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        Sync();
        return CdtMessageBox.Show(text, caption, buttons, icon, defaultResult);
    }

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        Sync();
        return CdtMessageBox.Show(owner, text, caption, buttons, icon, defaultResult);
    }
}
