using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SketchProMax.Phone;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The Phone switch under the message box: on when the user leaves the PC (questions, progress and replies go to the
/// phone, and what they write there reaches this chat), off while they work here (nothing reaches the phone). One
/// click, instant, also while the chat works: nothing waits on the network. With no phone connected it opens Connect
/// your phone.
/// </summary>
internal sealed partial class ChatPane
{
    private readonly TextBlock _phoneLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _phoneDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private Button? _phonePill;

    private Button PhonePill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_phoneDot);
        row.Children.Add(_phoneLabel);
        var pill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(-ChatUi.S1, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) => PhoneClicked();
        PhoneLink.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowPhone();
            // Switched on (or connected): the questions waiting in the Questions tab go to the phone now.
            if (!PhoneLink.Active || _instance != this) return;
            foreach (var s in Sessions.ToList())
                try { In(s, () => SyncPhoneQuestions(again: true)); }
                catch (Exception) { /* the tab still shows them */ }
        }));
        _phonePill = pill;
        ShowPhone();
        return pill;
    }

    private void PhoneClicked()
    {
        if (!PhoneLink.Connected)
        {
            var window = new PhoneConnectWindow(PhoneLink.Site);
            window.Show();
            return;
        }
        var on = !PhoneLink.On;
        PhoneLink.SetOn(on);
        if (on) AskUser.Listen();
        Status(on ? "Phone on: questions (also those waiting in Questions), progress and replies go to your phone, and you can write to this chat from it."
                  : "Phone off: nothing goes to your phone while you work here.", spin: false);
    }

    private void ShowPhone()
    {
        if (_phonePill is null) return;
        var connected = PhoneLink.Connected;
        var on = connected && PhoneLink.On;
        _phoneLabel.Text = !connected ? "Phone: connect" : on ? "Phone: on" : "Phone: off";
        Brand.Fg(_phoneLabel, on ? Brand.K.Text : Brand.K.Muted);
        _phoneDot.SetResourceReference(Shape.FillProperty, on ? Brand.K.SuccessText : Brand.K.Muted);
        var tip = !connected ? "Connect your phone to get Autopilot questions and replies there, and write to this chat from it."
            : on ? "On: questions, progress and replies go to your phone. Click when you're back at the PC to stop them."
            : "Off: nothing goes to your phone. Click before you leave the PC.";
        _phonePill.ToolTip = tip;
        AutomationProperties.SetName(_phonePill, _phoneLabel.Text);
        AutomationProperties.SetHelpText(_phonePill, tip);
    }

    /// <summary>A captured view as the phone gets it: at most 1600 px, JPEG, as a data URL (null when it can't be read).</summary>
    private static string? PhoneJpeg(byte[] png)
    {
        try
        {
            using var input = new System.IO.MemoryStream(png);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(input, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            System.Windows.Media.Imaging.BitmapSource image = frame;
            var scale = Math.Min(1.0, 1600.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
            if (scale < 1) image = new System.Windows.Media.Imaging.TransformedBitmap(frame, new ScaleTransform(scale, scale));
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = new System.IO.MemoryStream();
            encoder.Save(output);
            return Phone.PhonePictures.DataUrl(output.ToArray(), "image/jpeg");
        }
        catch (Exception) { return null; }
    }
}
