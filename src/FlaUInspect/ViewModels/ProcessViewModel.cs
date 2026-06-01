using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using FlaUInspect.Core;
using FlaUInspect.Core.Exporters;
using FlaUInspect.Core.Logger;
using FlaUInspect.Models;
using Microsoft.Win32;

namespace FlaUInspect.ViewModels;

public class ProcessViewModel : ObservableObject {

    private readonly AutomationBase _automation;
    private readonly InternalLogger _logger;
    private readonly int _processId;
    private readonly ITreeWalker _treeWalker;
    private readonly IntPtr _windowHandle;
    private ObservableCollection<ElementPatternItem>? _elementPatterns;
    private FocusTrackingMode? _focusTrackingMode;
    private PatternItemsFactory? _patternItemsFactory;
    private AutomationElement? _rootElement;
    private ElementOverlay _trackHighlighterOverlay;

    public ProcessViewModel(AutomationBase automation, int processId, IntPtr mainWindowHandle, InternalLogger logger) {
        _logger = logger;
        _automation = automation;
        _processId = processId;
        _windowHandle = mainWindowHandle;

        _trackHighlighterOverlay = CreateTrackHighlighterOverlay();

        WindowTitle = $"Process: [{processId}] '{(processId != 0
            ? _automation.FromHandle(mainWindowHandle)?.Properties.Name ?? "N/A"
            : "Desktop")}'";

        HoverManager.AddListener(_windowHandle,
                                 x => {
                                     if (EnableHoverMode) {
                                         ElementToSelectChanged(x);
                                     }
                                 });
        HoverManager.Disable(_windowHandle);

        _treeWalker = _automation.TreeWalkerFactory.GetControlViewWalker();

        Elements = [];

        RefreshCommand = new AsyncRelayCommand(async () => await Task.Run(Initialize));
        CaptureSelectedItemCommand = new RelayCommand(_ => {
            if (SelectedItem?.AutomationElement == null) {
                return;
            }
            Bitmap capturedImage = SelectedItem.AutomationElement.Capture();
            SaveFileDialog saveDialog = new () {
                Filter = "Png file (*.png)|*.png"
            };

            if (saveDialog.ShowDialog() == true) {
                capturedImage.Save(saveDialog.FileName, ImageFormat.Png);
            }
            capturedImage.Dispose();
        });

        CurrentElementSaveStateCommand = new RelayCommand(_ => {
            if (SelectedItem?.AutomationElement == null) {
                return;
            }

            try {
                ITreeExporter exporter = new XmlTreeExporter(EnableXPath);
                string exportedTree = exporter.Export(SelectedItem);

                Clipboard.SetText(exportedTree.ToString());
                CopiedNotificationCurrentElementSaveStateRequested?.Invoke();
            } catch (Exception e) {
                _logger?.LogError(e.ToString());
            }
        });

        ClosingCommand = new RelayCommand(_ => {
            HoverManager.RemoveListener(_windowHandle);
            _trackHighlighterOverlay?.Dispose();
            _focusTrackingMode?.Stop();
            _focusTrackingMode = null;
        });

        CopyDetailsToClipboardCommand = new RelayCommand(_ => {
            if (SelectedItem?.AutomationElement == null) {
                return;
            }

            try {
                IElementDetailsExporter detailsExporter = new XmlElementDetailsExporter();
                string details = detailsExporter.Export(ElementPatterns);

                Clipboard.SetText(details);
                CopiedNotificationRequested?.Invoke();
            } catch (Exception e) {
                _logger?.LogError(e.ToString());
            }
        });

        ClearRecordingCommand = new RelayCommand(_ => RecordedSteps.Clear());

        ClearSearchCommand = new RelayCommand(_ => {
            SearchText = string.Empty;
            ApplySearchFilter();
        });

        ApplySearchCommand = new RelayCommand(_ => ApplySearchFilter());

        CopyAllStepsCommand = new RelayCommand(_ => {
            if (RecordedSteps.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (var step in RecordedSteps) {
                sb.AppendLine($"Step {step.StepNumber}  |  {step.ControlTypeStr}  |  \"{step.ElementName}\"");
                sb.AppendLine($"Window  : {step.WindowLocator}");
                sb.AppendLine($"Dialog  : {step.DialogLocator}");
                sb.AppendLine($"[1] AutomationId : {step.AutomationIdLocator}");
                sb.AppendLine($"[2] Name         : {step.NameLocator}");
                sb.AppendLine($"[3] XPath        : {step.XPathLocator}");
                sb.AppendLine();
            }
            Clipboard.SetText(sb.ToString());
        });
    }

    public string? WindowTitle { get; }


    public bool EnableXPath {
        get => GetProperty<bool>();
        set => SetProperty(value);
    }

    public ObservableCollection<ElementViewModel> Elements { get; private set; }
    public ObservableCollection<ElementViewModel>? FlatNodes {
        get => GetProperty<ObservableCollection<ElementViewModel>>();
        private set => SetProperty(value);
    }

    public IEnumerable<ElementPatternItem> ElementPatterns {
        get => _elementPatterns ?? Enumerable.Empty<ElementPatternItem>();
        private set => SetProperty(ref _elementPatterns, value as ObservableCollection<ElementPatternItem>);
    }

    public ElementViewModel? SelectedItem {
        get => GetProperty<ElementViewModel>();
        set {
            if (SetProperty(value)) {
                if (value != null) {
                    if (EnableHighLightSelectionMode) {
                        TrackSelectedItem(value);
                    }
                    Task.Run(() => ReadPatternsForSelectedItem(value.AutomationElement));
                }
            }
        }
    }

    public bool EnableHoverMode {
        get => GetProperty<bool>();
        set {
            SetProperty(value);
            SetMode();
        }
    }

    public bool EnableHighLightSelectionMode {
        get => GetProperty<bool>();
        set {
            SetProperty(value);
            SetMode();
        }
    }

    public ICommand ClosingCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CaptureSelectedItemCommand { get; }
    public ICommand CurrentElementSaveStateCommand { get; }
    public ICommand CopyDetailsToClipboardCommand { get; }
    public ICommand ClearRecordingCommand { get; }
    public ICommand CopyAllStepsCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ApplySearchCommand { get; }

    public string? SearchText {
        get => GetProperty<string>();
        set => SetProperty(value);
    }

    public SearchScope SearchScope {
        get => GetProperty<SearchScope>();
        set => SetProperty(value);
    }

    public IReadOnlyList<SearchScope> SearchScopes { get; } = new[] {
        SearchScope.Name,
        SearchScope.AutomationId,
        SearchScope.XPath
    };

    public bool IsRecording {
        get => GetProperty<bool>();
        set => SetProperty(value);
    }

    public bool IsAutoRecording {
        get => GetProperty<bool>();
        set {
            SetProperty(value);
            SetMode();
        }
    }

    public ObservableCollection<RecordedStep> RecordedSteps { get; } = [];

    public void RecordElement(ElementViewModel element) {
        var step = new RecordedStep(RecordedSteps.Count + 1, element);
        step.DeleteAction = () => {
            RecordedSteps.Remove(step);
            for (int i = 0; i < RecordedSteps.Count; i++)
                RecordedSteps[i].StepNumber = i + 1;
        };
        RecordedSteps.Add(step);
    }

    public void CopyElementTreeToClipboard(ElementViewModel elementViewModel) {
        if (elementViewModel?.AutomationElement == null) return;

        try {
            ElementTreeNode node = BuildElementTree(elementViewModel.AutomationElement);
            string json = JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
            System.Windows.Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(json));
        } catch (Exception e) {
            _logger?.LogError(e.ToString());
        }
    }

    private ElementTreeNode BuildElementTree(AutomationElement element, int depth = 0) {
        if (depth > 50)
            return new ElementTreeNode("", "", "", "", []);

        string ct = element.Properties.ControlType.TryGetValue(out ControlType ctVal) ? ctVal.ToString() : "";
        string aid = element.Properties.AutomationId.ValueOrDefault ?? "";
        string name = element.Properties.Name.ValueOrDefault ?? "";
        string cls = element.Properties.ClassName.ValueOrDefault ?? "";

        var children = new List<ElementTreeNode>();
        try {
            ITreeWalker walker = _automation.TreeWalkerFactory.GetRawViewWalker();
            using (CacheRequest.ForceNoCache()) {
                AutomationElement? child = walker.GetFirstChild(element);
                while (child != null) {
                    children.Add(BuildElementTree(child, depth + 1));
                    child = walker.GetNextSibling(child);
                }
            }
        } catch {
            // ignored
        }

        return new ElementTreeNode(ct, aid, name, cls, children);
    }

    private record ElementTreeNode(
        [property: JsonPropertyName("controlType")] string ControlType,
        [property: JsonPropertyName("automationId")] string AutomationId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("className")] string ClassName,
        [property: JsonPropertyName("children")] List<ElementTreeNode> Children
    );

    public bool EnableFocusTrackingMode {
        get => GetProperty<bool>();
        set {
            SetProperty(value);
            SetMode();
        }
    }

    private static ElementOverlay CreateTrackHighlighterOverlay() {
        return App.FlaUiAppOptions.SelectionOverlay() ?? App.FlaUiAppOptions.DefaultOverlay()!;
    }

    private void TrackSelectedItem(ElementViewModel item) {
        if (item.AutomationElement != null) {
            _trackHighlighterOverlay?.Dispose();
            _trackHighlighterOverlay = CreateTrackHighlighterOverlay();

            try {
                _trackHighlighterOverlay.Show(item.AutomationElement.Properties.BoundingRectangle.Value);
            } catch (Exception e) {
                _trackHighlighterOverlay?.Dispose();
            }
        }
    }

    private void SetMode() {
        HoverManager.Disable(_windowHandle);
        _trackHighlighterOverlay?.Dispose();
        _focusTrackingMode?.Stop();

        // Auto-recording always needs focus tracking, regardless of the radio selection
        if (EnableFocusTrackingMode || IsAutoRecording) {
            _focusTrackingMode?.Start();
        }

        if (new[] { EnableHoverMode, EnableHighLightSelectionMode, EnableFocusTrackingMode }.Count(x => x) == 1) {
            if (EnableHighLightSelectionMode) {
                if (SelectedItem != null) {
                    TrackSelectedItem(SelectedItem);
                }
            } else if (EnableHoverMode) {
                HoverManager.Enable(_windowHandle);
            }
            // EnableFocusTrackingMode already handled above
        }
    }

    public event Action? CopiedNotificationCurrentElementSaveStateRequested;
    public event Action CopiedNotificationRequested;

    public void Initialize() {
        PatternItemsFactory patternItemsFactory = new (_automation);

        AutomationElement? rootElement;
        List<ElementViewModel> topChildren;
        try {
            rootElement = _windowHandle == IntPtr.Zero
                ? _automation.GetDesktop()
                : _automation.FromHandle(_windowHandle);

            ElementViewModel desktopViewModel = new (rootElement, null, 0, _logger);
            topChildren = desktopViewModel.LoadChildren();
        } catch (Exception ex) {
            _logger?.LogError($"Initialize UIA load failed: {ex.Message}");
            return;
        }

        FocusTrackingMode newFocusTracker = new (_automation,
            x => {
                if (EnableFocusTrackingMode || IsAutoRecording) {
                    ElementToSelectChanged(x);
                    if (IsAutoRecording && SelectedItem != null) {
                        RecordElement(SelectedItem);
                    }
                }
            });

        System.Windows.Threading.Dispatcher dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => {
            _patternItemsFactory = patternItemsFactory;
            _rootElement = rootElement;

            _focusTrackingMode?.Stop();
            _focusTrackingMode = newFocusTracker;

            Elements = new ObservableCollection<ElementViewModel>(topChildren);
            EnableHoverMode = false;
            ElementPatterns = GetDefaultPatternList();
            SelectedItem = Elements.Count == 0 ? null : Elements[0];

            OnPropertyChanged(nameof(Elements));
            OnPropertyChanged(nameof(ElementPatterns));
        });
    }

    private void ApplySearchFilter() {
        string? query = SearchText;
        SearchScope scope = SearchScope;
        bool hasQuery = !string.IsNullOrWhiteSpace(query);

        if (hasQuery && query!.Length >= 2) {
            ExpandTreeForSearch(Elements.ToList(), query, scope, maxDepth: 8);
        }

        foreach (ElementViewModel vm in Elements) {
            vm.IsMatch = hasQuery && vm.Matches(query!, scope);
        }

        ICollectionView view = CollectionViewSource.GetDefaultView(Elements);
        if (!hasQuery) {
            view.Filter = null;
        } else {
            view.Filter = o => {
                if (o is not ElementViewModel vm) return false;
                if (vm.Matches(query!, scope)) return true;
                ElementViewModel? p = vm.Parent;
                while (p != null) {
                    if (p.Matches(query!, scope)) return true;
                    p = p.Parent;
                }
                return HasMatchingDescendant(vm, query!, scope);
            };
        }
        view.Refresh();
    }

    private bool HasMatchingDescendant(ElementViewModel parent, string query, SearchScope scope) {
        int idx = Elements.IndexOf(parent);
        if (idx < 0) return false;
        for (int i = idx + 1; i < Elements.Count; i++) {
            ElementViewModel cur = Elements[i];
            if (cur.Level <= parent.Level) break;
            if (cur.Matches(query, scope)) return true;
        }
        return false;
    }

    private void ExpandTreeForSearch(IList<ElementViewModel> snapshot, string query, SearchScope scope, int maxDepth) {
        foreach (ElementViewModel vm in snapshot) {
            ExpandSubtreeForSearch(vm, query, scope, maxDepth, currentDepth: 0);
        }
    }

    private void ExpandSubtreeForSearch(ElementViewModel vm, string query, SearchScope scope, int maxDepth, int currentDepth) {
        if (currentDepth >= maxDepth || vm.AutomationElement == null) return;

        List<ElementViewModel> children;
        try {
            children = vm.LoadChildren();
        } catch {
            return;
        }
        if (children.Count == 0) return;

        bool anyMatchInSubtree = SubtreeContainsMatch(children, query, scope, maxDepth - currentDepth - 1);
        if (!anyMatchInSubtree) return;

        if (!vm.IsExpanded) {
            vm.IsExpanded = true;
            ExpandElement(vm);
        }

        int parentIdx = Elements.IndexOf(vm);
        if (parentIdx < 0) return;
        for (int i = parentIdx + 1; i < Elements.Count; i++) {
            ElementViewModel cur = Elements[i];
            if (cur.Level <= vm.Level) break;
            if (cur.Level == vm.Level + 1) {
                ExpandSubtreeForSearch(cur, query, scope, maxDepth, currentDepth + 1);
            }
        }
    }

    private static bool SubtreeContainsMatch(List<ElementViewModel> children, string query, SearchScope scope, int remainingDepth) {
        if (remainingDepth < 0) return false;
        foreach (ElementViewModel c in children) {
            if (c.Matches(query, scope)) return true;
            if (remainingDepth == 0) continue;
            try {
                List<ElementViewModel> grand = c.LoadChildren();
                if (SubtreeContainsMatch(grand, query, scope, remainingDepth - 1)) return true;
            } catch {
                // ignored
            }
        }
        return false;
    }

    public void ElementToSelectChanged(AutomationElement? obj, bool forceExpand = false) {
        Stack<AutomationElement> pathToRoot = new ();

        while (obj != null && obj.Properties.ProcessId == _processId) {
            // Break on circular relationship (should not happen?)
            if (pathToRoot.Contains(obj) || obj.Equals(_rootElement)) {
                break;
            }

            pathToRoot.Push(obj);

            if (forceExpand) {
                break;
            }

            try {
                obj = _treeWalker.GetParent(obj);
            } catch (Exception ex) {
                _logger?.LogError($"Exception: {ex.Message}");
            }
        }

        IEnumerable<ElementViewModel> viewModels = Elements;
        ElementViewModel? nextElementVm = null;

        while (pathToRoot.Count > 0) {
            AutomationElement elementOnPath = pathToRoot.Pop();
            nextElementVm = FindElement(viewModels, elementOnPath);

            if (nextElementVm != null && (forceExpand || !nextElementVm.IsExpanded)) {
                if (pathToRoot.Count != 0) {
                    nextElementVm.IsExpanded = true;
                }
                ExpandElement(nextElementVm);

                if (forceExpand) {
                    break;
                }
            }
        }

        SelectedItem = nextElementVm;
    }

    private ElementViewModel? FindElement(IEnumerable<ElementViewModel> viewModels, AutomationElement element) {
        return viewModels.FirstOrDefault(el => {
            if (el?.AutomationElement == null) {
                return false;
            }

            try {
                return el.AutomationElement.Equals(element);
            } catch (Exception e) {
                _logger?.LogError(e.ToString());
            }

            return false;
        });
    }

    private ObservableCollection<ElementPatternItem> GetDefaultPatternList() {
        return new ObservableCollection<ElementPatternItem>(new[] {
                                                                    new ElementPatternItem("Identification", PatternItemsFactory.Identification, true, true),
                                                                    new ElementPatternItem("Details", PatternItemsFactory.Details, true, true),
                                                                    new ElementPatternItem("Pattern Support", PatternItemsFactory.PatternSupport, true, true)
                                                                }
                                                                .Concat(
                                                                    (_automation?.PatternLibrary.AllForCurrentFramework ?? [])
                                                                    .Select(x => {
                                                                        ElementPatternItem patternItem = new (x.Name, x.Name) {
                                                                            IsVisible = true
                                                                        };
                                                                        return patternItem;
                                                                    })));
    }


    private void ReadPatternsForSelectedItem(AutomationElement? selectedItemAutomationElement) {
        if (SelectedItem?.AutomationElement == null || selectedItemAutomationElement == null) {
            return;
        }

        if (_patternItemsFactory == null) {
            return;
        }

        try {
            HashSet<PatternId> supportedPatterns = [.. selectedItemAutomationElement.GetSupportedPatterns()];
            IDictionary<string, PatternItem[]> patternItemsForElement = _patternItemsFactory.CreatePatternItemsForElement(selectedItemAutomationElement, supportedPatterns);

            foreach (ElementPatternItem elementPattern in ElementPatterns) {
                elementPattern.IsVisible = elementPattern.PatternIdName == PatternItemsFactory.Identification
                                           || elementPattern.PatternIdName == PatternItemsFactory.Details
                                           || elementPattern.PatternIdName == PatternItemsFactory.PatternSupport
                                           || supportedPatterns.Any(x => x.Name.Equals(elementPattern.PatternIdName));


                elementPattern.Children = patternItemsForElement.TryGetValue(elementPattern.PatternIdName, out PatternItem[]? children)
                    ? new ObservableCollection<PatternItem>(children)
                    : [];

                if (!elementPattern.Children.Any()) {
                    elementPattern.IsVisible = false;
                }
            }
        } catch (Exception e) {
            _logger?.LogError(e.ToString());
        }
    }

    public void ExpandElement(ElementViewModel sender) {
        List<ElementViewModel> children = sender.LoadChildren();
        children.Reverse();

        int senderIndex = Elements.IndexOf(sender);

        if (senderIndex < 0) {
            return;
        }

        foreach (ElementViewModel child in children) {
            Elements.Insert(senderIndex + 1, child);
        }
    }

    public void CollapseElement(ElementViewModel sender) {
        int senderIndex = Elements.IndexOf(sender);

        if (senderIndex < 0) {
            return;
        }

        var removeCount = 0;

        for (int i = senderIndex + 1; i < Elements.Count; i++) {
            if (IsDescendantOf(Elements[i], sender)) {
                removeCount++;
            } else {
                break;
            }
        }

        for (var i = 0; i < removeCount; i++) {
            Elements.RemoveAt(senderIndex + 1);
        }
    }

    private bool IsDescendantOf(ElementViewModel? node, ElementViewModel? parent) {
        if (node == null || parent == null) {
            return false;
        }
        ElementViewModel? p = node.Parent;

        while (p != null) {
            if (p == parent)
                return true;
            p = p.Parent;
        }
        return false;
    }
}