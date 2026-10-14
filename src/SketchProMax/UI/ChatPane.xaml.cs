using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using SketchProMax.Agent;
using SketchProMax.Settings;
using SketchProMax.Tools;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

public partial class ChatPane : Page, IDockablePaneProvider, IAgentHost
{
    private AgentSession? _session;
    private CancellationTokenSource? _cts;

    public ChatPane()
    {
        InitializeComponent();
        AddBubble("Hi, I'm Sketch ProMax. Ask me about the open model, or ask me to tag, schedule, audit or edit it.", isUser: false);
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = this;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        var text = Input.Text.Trim();
        if (text.Length == 0 || _cts != null) return;
        Input.Clear();
        AddBubble(text, isUser: true);

        try
        {
            _session ??= new AgentSession(AgentSettings.Load(), ToolRegistry.CreateDefault(), App.Dispatcher);
        }
        catch (InvalidOperationException ex)
        {
            AddBubble(ex.Message, isUser: false);
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true, "Thinking…");
        try
        {
            // Run the loop off the UI thread; host callbacks marshal back via Dispatcher.
            var session = _session;
            var ct = _cts.Token;
            await Task.Run(() => session.SendAsync(text, this, ct), ct);
        }
        catch (OperationCanceledException)
        {
            AddBubble("Stopped.", isUser: false);
            _session = null; // the history may end mid tool round; start clean
        }
        catch (Exception ex)
        {
            AddBubble($"Something went wrong: {ex.Message}", isUser: false);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false, "");
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnNewChat(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _session = null;
        Transcript.Children.Clear();
        AddBubble("New conversation.", isUser: false);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        _session = null; // pick up new settings on next message
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            OnSend(sender, e);
        }
    }

    // ---- IAgentHost (called from the agent's background thread) ----

    public void OnAssistantText(string text) => Ui(() => AddBubble(text, isUser: false));

    public void OnToolStarted(string toolName, string inputJson) => Ui(() => StatusText.Text = $"Running {toolName}…");

    public void OnToolFinished(string toolName, ToolResult result) => Ui(() =>
    {
        StatusText.Text = "Thinking…";
        var line = new TextBlock
        {
            Text = (result.IsError ? "✗ " : "✓ ") + toolName + (result.IsError ? $": {Truncate(result.Text, 200)}" : ""),
            Foreground = result.IsError ? Brushes.Firebrick : Brushes.SeaGreen,
            FontSize = 11,
            Margin = new Thickness(4, 2, 4, 2),
            TextWrapping = TextWrapping.Wrap,
        };
        Transcript.Children.Add(line);
        Scroller.ScrollToEnd();
    });

    public Task<bool> RequestApprovalAsync(string toolName, string inputJson, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        Ui(() =>
        {
            var card = new Border
            {
                BorderBrush = Brushes.DarkOrange, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8), Margin = new Thickness(4),
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"Approve change: {toolName}", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBox
            {
                Text = Pretty(inputJson), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11,
                MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4),
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var approve = new Button { Content = "Approve", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
            var reject = new Button { Content = "Reject", Padding = new Thickness(10, 2, 10, 2) };
            approve.Click += (_, _) => { tcs.TrySetResult(true); panel.IsEnabled = false; };
            reject.Click += (_, _) => { tcs.TrySetResult(false); panel.IsEnabled = false; };
            buttons.Children.Add(approve);
            buttons.Children.Add(reject);
            panel.Children.Add(buttons);
            card.Child = panel;
            Transcript.Children.Add(card);
            Scroller.ScrollToEnd();
            StatusText.Text = "Waiting for your approval…";
        });
        return tcs.Task;
    }

    // ---- helpers ----

    private void Ui(Action action) => Dispatcher.BeginInvoke(action);

    private void SetBusy(bool busy, string status)
    {
        SendButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        NewChatButton.IsEnabled = !busy;
        StatusText.Text = status;
    }

    private void AddBubble(string text, bool isUser)
    {
        var bubble = new Border
        {
            Background = isUser ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)) : new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = isUser ? new Thickness(40, 4, 4, 4) : new Thickness(4, 4, 40, 4),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new TextBox
            {
                Text = text, TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            },
        };
        Transcript.Children.Add(bubble);
        Scroller.ScrollToEnd();
    }

    private static string Pretty(string json)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
        catch (JsonException) { return json; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
