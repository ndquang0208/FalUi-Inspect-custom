using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUInspect.Core;
using FlaUInspect.Core.Extensions;

namespace FlaUInspect.ViewModels;

/// <summary>When — and whether — a recorded step reads the full pattern/detail snapshot.</summary>
public enum StepDetailsMode {
    /// <summary>Never. The card shows locators + HelpText only (WIN).</summary>
    None,

    /// <summary>In the background as soon as the step is recorded (REC / AUTO).</summary>
    Eager,

    /// <summary>The first time the user expands the card's Details expander (WIN-detail).</summary>
    Lazy
}

public class RecordedStep : ObservableObject {
    public const string NoneText = "(none)";

    private bool _detailsRequested;

    public RecordedStep(int stepNumber, ElementViewModel element) {
        _stepNumber = stepNumber;
        Element = element;

        ControlTypeStr = element.ControlType.ToString();
        ElementName = string.IsNullOrEmpty(element.Name) ? NoneText : element.Name;
        Label = element.ControlType == ControlType.Window ? "Window Element" : "Element";
        HelpText = ReadHelpText(element);

        var ancestors = BuildAncestorChain(element);

        WindowLocator = FindLocatorForType(ancestors, ControlType.Window,
            vm => !string.IsNullOrEmpty(vm.AutomationId)
                ? $"Window[@AutomationId='{vm.AutomationId}']"
                : !string.IsNullOrEmpty(vm.Name)
                    ? $"Window[@Name='{vm.Name}']"
                    : "Window");

        // UIA has no separate Dialog control type; treat innermost Window (when >1 exist) as dialog
        DialogLocator = FindDialogLocator(ancestors);

        AutomationIdLocator = string.IsNullOrEmpty(element.AutomationId)
            ? "(none)"
            : $"By.AutomationId(\"{element.AutomationId}\")";

        NameLocator = string.IsNullOrEmpty(element.Name)
            ? "(none)"
            : $"By.Name(\"{element.Name}\")";

        XPathLocator = GenerateXPath(element, ancestors);

        CopyAutomationIdCommand = new RelayCommand(_ => {
            if (AutomationIdLocator != "(none)")
                Clipboard.SetText(AutomationIdLocator);
        });
        CopyNameCommand = new RelayCommand(_ => {
            if (NameLocator != "(none)")
                Clipboard.SetText(NameLocator);
        });
        CopyXPathCommand = new RelayCommand(_ => {
            if (!string.IsNullOrEmpty(XPathLocator) && XPathLocator != "(none)")
                Clipboard.SetText(XPathLocator);
        });
        CopyHelpTextCommand = new RelayCommand(_ => {
            if (HasHelpText)
                Clipboard.SetText(HelpText);
        });
        CopyAllCommand = new RelayCommand(_ => Clipboard.SetText(BuildSummaryText()));
        DeleteCommand = new RelayCommand(_ => DeleteAction?.Invoke());
    }

    private int _stepNumber;
    public int StepNumber {
        get => _stepNumber;
        set => SetProperty(ref _stepNumber, value);
    }

    public ElementViewModel Element { get; }
    public string ControlTypeStr { get; }
    public string ElementName { get; }
    public string Label { get; }
    public string WindowLocator { get; }
    public string DialogLocator { get; }
    public string AutomationIdLocator { get; }
    public string NameLocator { get; }
    public string XPathLocator { get; }

    /// <summary>The UIA HelpText property of the element ("(none)" when the element exposes none).</summary>
    public string HelpText { get; }
    public bool HasHelpText => HelpText != NoneText;

    /// <summary>
    ///     Full detail snapshot (same groups as the Details tab), filled in asynchronously. When it is read — or
    ///     whether it is read at all — depends on <see cref="DetailsMode" />.
    /// </summary>
    public ObservableCollection<RecordedDetailGroup> DetailGroups { get; } = [];

    /// <summary>Assign before the step is added to a bound collection; it has no change notification.</summary>
    public StepDetailsMode DetailsMode { get; set; } = StepDetailsMode.None;

    public bool SupportsDetails => DetailsMode != StepDetailsMode.None;

    public bool HasDetails {
        get => GetProperty<bool>();
        private set => SetProperty(value);
    }

    public bool IsLoadingDetails {
        get => GetProperty<bool>();
        private set => SetProperty(value);
    }

    public bool IsDetailsExpanded {
        get => GetProperty<bool>();
        set {
            if (SetProperty(value) && value) {
                EnsureDetailsLoaded();
            }
        }
    }

    /// <summary>Set by <c>ProcessViewModel.AddStep</c>; kicks off the background pattern read for this step.</summary>
    public Action? LoadDetailsAction { get; set; }

    public Action? DeleteAction { get; set; }

    /// <summary>Triggers the detail read at most once, whether it was requested eagerly or by expanding the card.</summary>
    public void EnsureDetailsLoaded() {
        if (_detailsRequested || DetailsMode == StepDetailsMode.None || LoadDetailsAction == null) {
            return;
        }
        _detailsRequested = true;
        IsLoadingDetails = true;
        LoadDetailsAction.Invoke();
    }

    public ICommand CopyAutomationIdCommand { get; }
    public ICommand CopyNameCommand { get; }
    public ICommand CopyXPathCommand { get; }
    public ICommand CopyHelpTextCommand { get; }
    public ICommand CopyAllCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>Replaces the detail snapshot. Must be called on the UI thread.</summary>
    public void SetDetailGroups(IEnumerable<RecordedDetailGroup> groups) {
        DetailGroups.Clear();
        foreach (RecordedDetailGroup group in groups) {
            DetailGroups.Add(group);
        }
        HasDetails = DetailGroups.Count > 0;
        IsLoadingDetails = false;
    }

    /// <summary>Single source of truth for the clipboard format, used by both per-step and bulk copy.</summary>
    public string BuildSummaryText() {
        var sb = new StringBuilder();
        sb.AppendLine($"{Label}  |  {ControlTypeStr}  |  \"{ElementName}\"");
        sb.AppendLine($"Window  : {WindowLocator}");
        sb.AppendLine($"Dialog  : {DialogLocator}");
        sb.AppendLine($"HelpText: {HelpText}");
        sb.AppendLine($"[1] AutomationId : {AutomationIdLocator}");
        sb.AppendLine($"[2] Name         : {NameLocator}");
        sb.AppendLine($"[3] XPath        : {XPathLocator}");

        if (DetailGroups.Count > 0) {
            sb.AppendLine("--- Details ---");
            foreach (RecordedDetailGroup group in DetailGroups) {
                sb.AppendLine($"[{group.Name}]");
                foreach (PatternItem item in group.Items) {
                    sb.AppendLine($"  {item.Key,-28}: {item.Value}");
                }
            }
        }

        return sb.ToString();
    }

    private static string ReadHelpText(ElementViewModel element) {
        if (element.AutomationElement == null) {
            return NoneText;
        }

        try {
            using (CacheRequest.ForceNoCache()) {
                return element.AutomationElement.Properties.HelpText.TryGetValue(out string? help) && !string.IsNullOrWhiteSpace(help)
                    ? help.NormalizeString().Trim()
                    : NoneText;
            }
        } catch {
            return NoneText;
        }
    }

    private static List<ElementViewModel> BuildAncestorChain(ElementViewModel element) {
        var ancestors = new List<ElementViewModel>();
        var current = element.Parent;
        while (current != null) {
            ancestors.Insert(0, current);
            current = current.Parent;
        }
        return ancestors;
    }

    private static string FindLocatorForType(List<ElementViewModel> ancestors, ControlType type, Func<ElementViewModel, string> formatter) {
        var found = ancestors.FirstOrDefault(a => a.ControlType == type);
        return found != null ? formatter(found) : "(none)";
    }

    private static string FindDialogLocator(List<ElementViewModel> ancestors) {
        var windows = ancestors.Where(a => a.ControlType == ControlType.Window).ToList();
        if (windows.Count < 2) return "(none)";
        var dialog = windows[windows.Count - 1];
        return !string.IsNullOrEmpty(dialog.Name)
            ? $"Dialog[@Name='{dialog.Name}']"
            : !string.IsNullOrEmpty(dialog.AutomationId)
                ? $"Dialog[@AutomationId='{dialog.AutomationId}']"
                : "(none)";
    }

    private static string GenerateXPath(ElementViewModel element, List<ElementViewModel> ancestors) {
        var path = new List<ElementViewModel>(ancestors) { element };

        int windowIdx = path.FindIndex(vm => vm.ControlType == ControlType.Window);
        if (windowIdx >= 0)
            path = path.Skip(windowIdx).ToList();

        if (path.Count == 0)
            return "(none)";

        var sb = new StringBuilder();
        foreach (var node in path) {
            sb.Append('/');
            string ctName = node.ControlType.ToString();

            if (!string.IsNullOrEmpty(node.AutomationId)) {
                sb.Append($"{ctName}[@AutomationId='{node.AutomationId}']");
            } else {
                int idx = GetSiblingIndex(node);
                sb.Append(idx > 1 ? $"{ctName}[{idx}]" : ctName);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "(none)";
    }

    private static int GetSiblingIndex(ElementViewModel element) {
        if (element.Parent?.AutomationElement == null || element.AutomationElement == null)
            return 1;

        try {
            using (CacheRequest.ForceNoCache()) {
                var siblings = element.Parent.AutomationElement.FindAllChildren();
                int index = 1;
                foreach (var sibling in siblings) {
                    bool isSameType = sibling.Properties.ControlType.TryGetValue(out ControlType ct)
                                      && ct == element.ControlType;
                    if (sibling.Equals(element.AutomationElement))
                        return index;
                    if (isSameType)
                        index++;
                }
            }
        } catch {
            // ignored
        }

        return 1;
    }
}
