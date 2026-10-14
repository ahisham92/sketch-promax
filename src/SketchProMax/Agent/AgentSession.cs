using System.IO;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using SketchProMax.Revit;
using SketchProMax.Settings;
using SketchProMax.Tools;

namespace SketchProMax.Agent;

/// <summary>Callbacks the agent uses to talk to the chat pane.</summary>
public interface IAgentHost
{
    void OnAssistantText(string text);
    void OnToolStarted(string toolName, string inputJson);
    void OnToolFinished(string toolName, ToolResult result);

    /// <summary>Ask the user to approve a model-changing tool call. Returns false if rejected.</summary>
    Task<bool> RequestApprovalAsync(string toolName, string inputJson, CancellationToken ct);
}

/// <summary>
/// One conversation with Claude. Runs the tool-use loop: send the conversation, execute any
/// tool_use blocks on Revit's main thread, return tool_result blocks, repeat until Claude ends its turn.
/// The history is append-only so prompt caching and thinking blocks stay valid across turns.
/// </summary>
public sealed class AgentSession
{
    private readonly AnthropicClient _client;
    private readonly AgentSettings _settings;
    private readonly ToolRegistry _registry;
    private readonly RevitDispatcher _dispatcher;
    private readonly List<BetaMessageParam> _history = [];
    private readonly List<BetaToolUnion> _toolDefs;

    public AgentSession(AgentSettings settings, ToolRegistry registry, RevitDispatcher dispatcher)
    {
        var key = settings.GetApiKey()
            ?? throw new InvalidOperationException("No Anthropic API key is set, so this pane can't reach Claude. To use Sketch ProMax at no extra cost, chat from the Claude desktop app instead (see README). To use this pane, add an API key in Settings.");
        _client = new AnthropicClient { ApiKey = key };
        _settings = settings;
        _registry = registry;
        _dispatcher = dispatcher;
        _toolDefs = registry.All.Select(ToToolDefinition).ToList();
    }

    public void Reset() => _history.Clear();

    public async Task SendAsync(string userText, IAgentHost host, CancellationToken ct, IReadOnlyList<string>? attachments = null)
    {
        _history.Add(new BetaMessageParam { Role = Role.User, Content = UserContent(userText, attachments ?? []) });

        for (int round = 0; round < _settings.MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _settings.Model,
                MaxTokens = _settings.MaxTokens,
                Betas = ["server-side-fallback-2026-07-01"],
                // If a request is declined by a safety classifier, the API re-serves it on a fallback model.
                Fallbacks = new Default(),
                OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_settings.Effort) },
                // Tools render before the system prompt, so this breakpoint caches both.
                System = new List<BetaTextBlockParam>
                {
                    new() { Text = SystemPrompt.Build(_settings.FirmStandards), CacheControl = new BetaCacheControlEphemeral() },
                },
                Tools = _toolDefs,
                Messages = _history,
            }, ct);

            if (response.StopReason == BetaStopReason.Refusal)
            {
                host.OnAssistantText("Claude declined this request.");
                _history.RemoveAt(_history.Count - 1);
                return;
            }

            var assistant = new List<BetaContentBlockParam>();
            var toolUses = new List<BetaToolUseBlock>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text))
                {
                    assistant.Add(new BetaTextBlockParam { Text = text.Text });
                    host.OnAssistantText(text.Text);
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    // Echo thinking blocks back unchanged (signature included).
                    assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickFallback(out var fallback))
                {
                    // Records which model answered after a fallback; keep it in the history.
                    assistant.Add(new BetaFallbackBlockParam
                    {
                        From = new BetaFallbackInfoParam(fallback.From.Model),
                        To = new BetaFallbackInfoParam(fallback.To.Model),
                    });
                }
                else if (block.TryPickToolUse(out var toolUse))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    toolUses.Add(toolUse);
                }
            }
            _history.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });

            if (toolUses.Count == 0)
            {
                if (response.StopReason == BetaStopReason.MaxTokens)
                    host.OnAssistantText("(The reply hit the length limit. Ask me to continue.)");
                return;
            }

            // Every tool_use needs a matching tool_result, all in one user message.
            var results = new List<BetaContentBlockParam>();
            foreach (var call in toolUses)
                results.Add(await RunToolAsync(call, host, ct));
            _history.Add(new BetaMessageParam { Role = Role.User, Content = results });
        }

        host.OnAssistantText($"Stopped after {_settings.MaxToolRounds} tool rounds. Ask me to continue if needed.");
    }

    private async Task<BetaToolResultBlockParam> RunToolAsync(BetaToolUseBlock call, IAgentHost host, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(call.Input);
        var tool = _registry.Find(call.Name);
        ToolResult result;

        if (tool is null)
        {
            result = ToolResult.Error($"Unknown tool '{call.Name}'.");
        }
        else if (tool.ModifiesModel && _settings.RequireApprovalForChanges
                 && !await host.RequestApprovalAsync(call.Name, inputJson, ct))
        {
            result = ToolResult.Error("The user rejected this change. Ask them how they want to proceed.");
        }
        else
        {
            host.OnToolStarted(call.Name, inputJson);
            try
            {
                result = await _dispatcher.InvokeAsync(app => tool.Execute(app, new ToolArgs(call.Input)));
            }
            catch (Exception ex)
            {
                result = ToolResult.Error($"{ex.GetType().Name}: {ex.Message}");
            }
        }
        host.OnToolFinished(call.Name, result);

        if (result.PngImage is { } png)
        {
            return new BetaToolResultBlockParam
            {
                ToolUseID = call.ID,
                Content = new List<Block>
                {
                    new BetaTextBlockParam { Text = result.Text },
                    new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng } },
                },
            };
        }
        return new BetaToolResultBlockParam { ToolUseID = call.ID, Content = result.Text, IsError = result.IsError };
    }

    /// <summary>
    /// The user's text plus any attached pictures or PDFs (product photos, cut sheets, sketches) so Claude can see them.
    /// File paths are listed too, so tools like import_image can use the same files.
    /// </summary>
    private static BetaMessageParamContent UserContent(string text, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return text;
        var blocks = new List<BetaContentBlockParam>();
        foreach (var file in files)
        {
            var data = Convert.ToBase64String(File.ReadAllBytes(file));
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".pdf":
                    blocks.Add(new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = data } });
                    break;
                case ".png":
                    blocks.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = data, MediaType = MediaType.ImagePng } });
                    break;
                case ".gif":
                    blocks.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = data, MediaType = MediaType.ImageGif } });
                    break;
                case ".webp":
                    blocks.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = data, MediaType = MediaType.ImageWebP } });
                    break;
                default:
                    blocks.Add(new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = data, MediaType = MediaType.ImageJpeg } });
                    break;
            }
        }
        blocks.Add(new BetaTextBlockParam
        {
            Text = text + "\n\n(Attached files on this computer: " + string.Join("; ", files) + ")",
        });
        return blocks;
    }

    private static BetaToolUnion ToToolDefinition(IRevitTool tool)
    {
        return new BetaTool
        {
            Name = tool.Name,
            Description = ToolSchema.Description(tool),
            InputSchema = new() { Properties = ToolSchema.Properties(tool), Required = tool.Required },
        };
    }

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.High,
    };
}
