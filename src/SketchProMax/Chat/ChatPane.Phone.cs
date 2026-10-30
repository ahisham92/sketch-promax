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
        PhoneLink.Changed += () => Dispatcher.BeginInvoke(new Action(ShowPhone));
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
        Status(on ? "Phone on: questions, progress and replies go to your phone, and you can write to this chat from it."
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
}
