using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SketchProMax.Update;

namespace SketchProMax.UI;

/// <summary>
/// Shows an update as it happens: checking, downloading (percent and MB), verifying, unpacking and applying, then a
/// clear final state. Modeless and owned by Revit, so Revit stays usable throughout. The updater reports from a
/// background thread; every change is marshalled to this window's Dispatcher.
/// </summary>
internal sealed class UpdateWindow : Window
{
    private static UpdateWindow? _open;

    private static readonly (UpdateStage Stage, string Label)[] Steps =
    [
        (UpdateStage.Checking, "Checking for a new version"),
        (UpdateStage.Downloading, "Downloading"),
        (UpdateStage.Verifying, "Verifying"),
        (UpdateStage.Unpacking, "Unpacking"),
        (UpdateStage.Applying, "Applying"),
    ];

    private readonly UpdateProgress? _progress;
    private readonly List<(TextBlock Mark, TextBlock Label)> _rows = [];
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 14, 0, 6) };
    private readonly TextBlock _detail = Brand.Text("", Brand.K.Muted);
    private readonly TextBlock _final = Brand.Text("", Brand.K.Text, 14, bold: true);
    private readonly TextBlock _next = Brand.Text("", Brand.K.Muted);
    // Stopping a download is destructive for the run: danger style, never the Enter key's target.
    private readonly Button _cancel = Brand.Button("Cancel update", Brand.Weight.Danger);
    private readonly Button _retry = Brand.Button("Try again", Brand.Weight.Primary);
    private readonly Button _hide = Brand.Button("Hide", Brand.Weight.Primary);
    private readonly DispatcherTimer? _autoClose;

    private UpdateWindow(UpdateProgress? progress, TimeSpan? autoClose)
    {
        _progress = progress;
        Title = "Sketch ProMax update";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = false;
        Brand.Prepare(this);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        foreach (var (_, label) in Steps)
        {
            var mark = Brand.Fg(new TextBlock { Width = 22, FontSize = 14, Text = "○" }, Brand.K.Muted);
            var text = Brand.Fg(new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap }, Brand.K.Muted);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(mark);
            row.Children.Add(text);
            body.Children.Add(row);
            _rows.Add((mark, text));
        }
        AutomationProperties.SetName(_bar, "Update progress");
        body.Children.Add(_bar);
        body.Children.Add(_detail);
        _final.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_final, AutomationLiveSetting.Polite);
        body.Children.Add(_final);
        _next.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(_next);

        _cancel.Click += (_, _) =>
        {
            if (Brand.IsBusy(_cancel)) return;
            try { _progress?.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            Brand.SetBusy(_cancel, true, "Cancelling…");
        };
        _retry.Click += (_, _) =>
        {
            Brand.SetBusy(_retry, true, "Starting…");
            App.CheckForUpdatesInteractive(); // opens a fresh run in this window's place
        };
        _retry.Visibility = Visibility.Collapsed;
        // Hide (Esc / Enter) never stops the update; it keeps running and the Version button brings this back.
        _hide.IsDefault = true;
        _hide.IsCancel = true;
        _hide.Click += (_, _) => { if (_isFinal) Close(); else Hide(); };
        body.Children.Add(Brand.ButtonRow(_cancel, _hide, _retry));

        var root = new DockPanel();
        var header = Brand.Header("Sketch ProMax update", $"Installed: {Updater.CurrentText}. Revit stays usable while this runs.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;

        if (progress is not null) progress.Changed += OnChanged;
        Closed += (_, _) =>
        {
            if (_progress is not null) _progress.Changed -= OnChanged;
            _autoClose?.Stop();
            if (_open == this) _open = null;
        };
        if (autoClose is { } after)
        {
            _autoClose = new DispatcherTimer { Interval = after };
            _autoClose.Tick += (_, _) => { _autoClose.Stop(); Close(); };
        }
    }

    /// <summary>True while the window is on screen (not hidden or closed).</summary>
    public static bool IsShowing => _open is { IsVisible: true };

    /// <summary>Opens (or brings back) the window for an update run. Call on the UI thread.</summary>
    public static void ShowFor(UpdateProgress progress)
    {
        if (_open is not null && _open._progress != progress)
        {
            _open.Close();
            _open = null;
        }
        _open ??= new UpdateWindow(progress, null);
        _open.Render(progress.Snapshot);
        if (!_open.IsVisible) _open.Show();
        _open.Activate();
    }

    /// <summary>Brings back the window of the current run, if any. Returns false when there is none to show.</summary>
    public static bool ShowCurrent()
    {
        if (UpdateProgress.Current is not { } p || p.Snapshot.Stage == UpdateStage.Idle) return false;
        ShowFor(p);
        return true;
    }

    /// <summary>A short notice in the same window, for an update this Revit window followed from another one.</summary>
    public static void Announce(UpdateSnapshot final)
    {
        _open?.Close();
        _open = new UpdateWindow(null, TimeSpan.FromSeconds(15));
        _open.Render(final);
        _open.Show();
        // A restart note stays until it is closed; a plain "updated" closes by itself.
        if (final.Detail is null) _open._autoClose?.Start();
    }

    private void OnChanged(UpdateSnapshot s)
    {
        // Raised on the updater's thread.
        if (Dispatcher.CheckAccess()) Render(s);
        else Dispatcher.BeginInvoke(new Action(() => Render(s)));
    }

    private void Render(UpdateSnapshot s)
    {
        var current = Array.FindIndex(Steps, x => x.Stage == s.Stage);
        var finalOk = s.Stage is UpdateStage.Updated or UpdateStage.UpToDate;
        var failedAt = s.Stage is UpdateStage.Failed or UpdateStage.Cancelled ? LastActive : -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            var (mark, label) = _rows[i];
            string glyph, color;
            if (finalOk && (s.Stage == UpdateStage.Updated || i == 0)) { glyph = "✓"; color = Brand.K.SuccessText; }
            else if (s.Stage == UpdateStage.RestartNeeded) { glyph = i == _rows.Count - 1 ? "!" : "✓"; color = i == _rows.Count - 1 ? Brand.K.WarningText : Brand.K.SuccessText; }
            else if (failedAt >= 0) { glyph = i < failedAt ? "✓" : i == failedAt ? "✕" : "○"; color = i < failedAt ? Brand.K.SuccessText : i == failedAt ? Brand.K.DangerText : Brand.K.Muted; }
            else if (current >= 0 && i < current) { glyph = "✓"; color = Brand.K.SuccessText; }
            else if (i == current) { glyph = "●"; color = Brand.K.Link; }
            else { glyph = "○"; color = Brand.K.Muted; }
            mark.Text = glyph;
            Brand.Fg(mark, color);
            Brand.Fg(label, color == Brand.K.Muted ? Brand.K.Muted : Brand.K.Text);
            label.FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal;
            if (i == 1 && s.Stage == UpdateStage.Downloading)
                label.Text = s.Percent is { } p
                    ? $"Downloading {s.Version}: {p}% ({UpdateSnapshot.Megabytes(s.Bytes)} of {UpdateSnapshot.Megabytes(s.Total ?? 0)})"
                    : $"Downloading {s.Version}: {UpdateSnapshot.Megabytes(s.Bytes)}";
            else if (i == 1) label.Text = s.Version is { } v && current > 1 ? $"Downloaded {v}" : "Downloading";
        }
        if (current >= 0) LastActive = current;

        _bar.IsIndeterminate = s.Stage is UpdateStage.Checking or UpdateStage.Verifying or UpdateStage.Unpacking or UpdateStage.Applying
                               || s.Stage == UpdateStage.Downloading && s.Percent is null;
        _bar.Value = s.Stage == UpdateStage.Downloading ? s.Percent ?? 0 : s.IsFinal ? 100 : 0;
        _bar.SetResourceReference(ForegroundProperty, s.Stage is UpdateStage.Failed or UpdateStage.Cancelled ? Brand.K.Danger : s.IsFinal ? Brand.K.BadgeOk : Brand.K.Accent);
        _bar.Visibility = s.Stage == UpdateStage.UpToDate ? Visibility.Collapsed : Visibility.Visible;

        _detail.Text = s.IsFinal ? "" : s.Detail ?? "";
        _final.Text = s.Stage switch
        {
            // A detail on "updated" is the restart note: what of the new version shows only after Revit restarts.
            UpdateStage.Updated => s.Detail is { } note ? $"Updated to {s.Version}. {note}" : $"Updated to {s.Version}. You can keep working; no restart needed.",
            UpdateStage.UpToDate => s.Detail ?? $"Sketch ProMax {Updater.CurrentText} is up to date.",
            UpdateStage.RestartNeeded => s.Detail ?? "Restart Revit to finish (your work is not interrupted until then).",
            UpdateStage.Failed => s.Detail ?? "The update didn't finish. Nothing changed; it is tried again later.",
            UpdateStage.Cancelled => s.Detail ?? "The update was cancelled.",
            _ => "",
        };
        Brand.Fg(_final, s.Stage switch
        {
            UpdateStage.Updated when s.Detail is not null => Brand.K.WarningText,
            UpdateStage.Updated or UpdateStage.UpToDate => Brand.K.SuccessText,
            UpdateStage.Failed => Brand.K.DangerText,
            UpdateStage.RestartNeeded or UpdateStage.Cancelled => Brand.K.WarningText,
            _ => Brand.K.Text,
        });
        _final.Visibility = _final.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // What to do next, for every state that needs the user.
        var canRetry = _progress is not null && s.Stage is UpdateStage.Failed or UpdateStage.Cancelled;
        _next.Text = s.Stage switch
        {
            UpdateStage.Failed => canRetry
                ? "Check the internet connection, then press Try again. If it keeps failing, open What's new in the Updates panel " +
                  "and install the release by hand."
                : "Use Check now in the Updates panel to try again.",
            UpdateStage.Cancelled => canRetry ? "Press Try again to update now, or Close to keep this version." : "",
            UpdateStage.RestartNeeded => "Save your work, then close and reopen Revit when it suits you.",
            UpdateStage.Updated when s.Detail is not null => "You can keep working now. Save your work and restart Revit when it suits you.",
            UpdateStage.Downloading or UpdateStage.Checking when _progress is not null =>
                "You can keep working. Hide keeps the update running; the Version button brings this window back.",
            _ => "",
        };
        _next.Visibility = _next.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var cancellable = _progress is not null && s.Stage is UpdateStage.Checking or UpdateStage.Downloading;
        _cancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        if (!cancellable) Brand.SetBusy(_cancel, false);
        _isFinal = s.IsFinal;
        _hide.Content = s.IsFinal ? "Close" : "Hide";

        // One primary action: Try again after a failure (Close becomes secondary), Hide/Close otherwise.
        _retry.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        _retry.IsDefault = canRetry;
        _hide.IsDefault = !canRetry;
        _hide.SetResourceReference(StyleProperty, canRetry ? "SpmSecondaryButton" : "SpmPrimaryButton");
    }

    private bool _isFinal;

    private int LastActive { get; set; }
}
