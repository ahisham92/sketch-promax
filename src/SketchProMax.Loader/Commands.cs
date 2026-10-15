using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SketchProMax.Loader;

/// <summary>
/// Every ribbon button is one of these: it forwards to whichever core is running. The forwarding goes through a delegate
/// stored on the AppDomain, so even if Revit ever loaded a second copy of this assembly for a button, it would still
/// reach the one host that owns the running core.
/// </summary>
public abstract class ForwardCommand : IExternalCommand
{
    protected abstract string Name { get; }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (AppDomain.CurrentDomain.GetData(Host.DispatchKey) is not Func<string, UIApplication, int> dispatch)
        {
            message = "Sketch ProMax has not started. Restart Revit.";
            return Result.Failed;
        }
        try
        {
            return dispatch(Name, commandData.Application) switch
            {
                0 => Result.Succeeded,
                1 => Result.Cancelled,
                _ => Result.Failed,
            };
        }
        catch (Exception ex)
        {
            if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
            TaskDialog.Show("Sketch ProMax", $"That didn't work: {ex.Message}\n\nIf it keeps happening, send this to Claude: {ex.GetType().Name} in {Name}.");
            return Result.Failed;
        }
    }
}

/// <summary>Buttons that also work with no model open.</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

/// <summary>Buttons that act on a model: available once one is open.</summary>
public sealed class ModelAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
        applicationData.ActiveUIDocument?.Document is { IsFamilyDocument: false };
}

[Transaction(TransactionMode.Manual)] public sealed class StatusCommand : ForwardCommand { protected override string Name => "status"; }
[Transaction(TransactionMode.Manual)] public sealed class CopyPromptCommand : ForwardCommand { protected override string Name => "copy-prompt"; }
[Transaction(TransactionMode.Manual)] public sealed class OpenClaudeCommand : ForwardCommand { protected override string Name => "open-claude"; }
[Transaction(TransactionMode.Manual)] public sealed class JobCommand : ForwardCommand { protected override string Name => "job"; }
[Transaction(TransactionMode.Manual)] public sealed class CancelJobCommand : ForwardCommand { protected override string Name => "cancel-job"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickRefreshCommand : ForwardCommand { protected override string Name => "quick-refresh"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickFolderCommand : ForwardCommand { protected override string Name => "quick-folder"; }
[Transaction(TransactionMode.Manual)] public sealed class ModelCheckCommand : ForwardCommand { protected override string Name => "model-check"; }
[Transaction(TransactionMode.Manual)] public sealed class SelfTestCommand : ForwardCommand { protected override string Name => "self-test"; }
[Transaction(TransactionMode.Manual)] public sealed class UpdateInfoCommand : ForwardCommand { protected override string Name => "update-info"; }
[Transaction(TransactionMode.Manual)] public sealed class UpdateCheckCommand : ForwardCommand { protected override string Name => "update-check"; }
[Transaction(TransactionMode.Manual)] public sealed class WhatsNewCommand : ForwardCommand { protected override string Name => "whats-new"; }
[Transaction(TransactionMode.Manual)] public sealed class SettingsCommand : ForwardCommand { protected override string Name => "settings"; }
[Transaction(TransactionMode.Manual)] public sealed class HelpCommand : ForwardCommand { protected override string Name => "help"; }

// Quick tools: a pulldown item needs its own command class, so there is a fixed set of slots, given a recipe or a learned
// tool each time the list is refreshed.
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot00 : ForwardCommand { protected override string Name => "quick:0"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot01 : ForwardCommand { protected override string Name => "quick:1"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot02 : ForwardCommand { protected override string Name => "quick:2"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot03 : ForwardCommand { protected override string Name => "quick:3"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot04 : ForwardCommand { protected override string Name => "quick:4"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot05 : ForwardCommand { protected override string Name => "quick:5"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot06 : ForwardCommand { protected override string Name => "quick:6"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot07 : ForwardCommand { protected override string Name => "quick:7"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot08 : ForwardCommand { protected override string Name => "quick:8"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot09 : ForwardCommand { protected override string Name => "quick:9"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot10 : ForwardCommand { protected override string Name => "quick:10"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot11 : ForwardCommand { protected override string Name => "quick:11"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot12 : ForwardCommand { protected override string Name => "quick:12"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot13 : ForwardCommand { protected override string Name => "quick:13"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot14 : ForwardCommand { protected override string Name => "quick:14"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot15 : ForwardCommand { protected override string Name => "quick:15"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot16 : ForwardCommand { protected override string Name => "quick:16"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot17 : ForwardCommand { protected override string Name => "quick:17"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot18 : ForwardCommand { protected override string Name => "quick:18"; }
[Transaction(TransactionMode.Manual)] public sealed class QuickSlot19 : ForwardCommand { protected override string Name => "quick:19"; }
