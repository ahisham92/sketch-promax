using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Tools;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// A hand-off in the chat: the step only the user can do (what, where in Revit, why) with Continue, Skip this item and
/// Stop, the same as the hand-off window. Saved with the conversation as a "handoff" item (Name: the request id, Text:
/// what, Input: where, Result: why, State: how it ended); redrawn after the request has closed, it shows how it ended.
/// </summary>
internal static class HandoffCard
{
    public static UIElement Build(ChatItem item, HandoffRequest? live)
    {
        var body = new StackPanel();
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, ChatUi.S2) };
        var icon = ChatUi.Glyph(ChatUi.GlyphInfo, Brand.K.Accent, 14);
        icon.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        DockPanel.SetDock(icon, Dock.Left);
        title.Children.Add(icon);
        var count = live is { Count: > 1 } ? $"  ·  step {live.Index} of {live.Count}" : "";
        title.Children.Add(Brand.Text("Over to you" + count, Brand.K.Text, ChatUi.BodySize, bold: true));
        body.Children.Add(title);

        if (live is not null)
        {
            if (live.State == HandoffState.Closed && item.State.Length == 0) item.State = live.Outcome ?? "";
            live.Changed += r => { if (r.State == HandoffState.Closed) item.State = r.Outcome ?? ""; };
            body.Children.Add(new HandoffView(live, compact: true));
        }
        else
        {
            // Saved earlier, or the request has gone: what it asked and how it ended, without buttons.
            body.Children.Add(Brand.Text(item.Text, Brand.K.Text, ChatUi.BodySize, bold: true));
            if (item.Input.Length > 0) body.Children.Add(Brand.Text("Where: " + item.Input, Brand.K.Text, ChatUi.SmallSize + 0.5));
            if (item.Result.Length > 0) body.Children.Add(Brand.Text("Why: " + item.Result, Brand.K.Muted, ChatUi.SmallSize + 0.5));
            var ended = Brand.Text(item.State switch
            {
                "Done" => "Done; the task carried on.",
                "Skipped" => "Skipped.",
                "Stopped" => "Stopped here.",
                { Length: > 0 } other => other,
                _ => "No longer waiting (the task ended or Revit restarted).",
            }, item.State == "Done" ? Brand.K.Text : Brand.K.Muted, ChatUi.SmallSize + 0.5);
            ended.Margin = new Thickness(0, ChatUi.S2, 0, 0);
            body.Children.Add(ended);
        }

        var card = new Border
        {
            Child = body, Padding = new Thickness(ChatUi.S3, ChatUi.S3, ChatUi.S3, ChatUi.S3), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ChatUi.Radius), Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1),
        };
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.AccentSoft);
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.Accent);
        AutomationProperties.SetName(card, "Over to you: " + item.Text);
        AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Assertive);
        return card;
    }

    /// <summary>The conversation item for a new request.</summary>
    public static ChatItem Item(HandoffRequest request) => new()
    {
        Kind = "handoff", Name = request.Id, Text = request.Step.What, Input = request.Step.Where, Result = request.Step.Why,
    };
}
