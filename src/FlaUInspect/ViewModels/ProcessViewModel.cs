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
    private readonly List<ElementViewModel> _matches = [];
    private int _matchIndex = -1;

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

        ApplySearchCommand = new AsyncRelayCommand(async () => {
            if (IsSearching) return;
            IsSearching = true;
            try {
                await Task.Run(RunSearchOnBackground);
            } finally {
                IsSearching = false;
            }
        });

        NextMatchCommand = new RelayCommand(_ => GoToMatch(+1));
        PreviousMatchCommand = new RelayCommand(_ => GoToMatch(-1));

        CopyAllStepsCommand = new RelayCommand(_ => CopyStepsToClipboard(RecordedSteps));

        ClearDialogCaptureCommand = new RelayCommand(_ => DialogCapturedSteps.Clear());
        CopyAllDialogStepsCommand = new RelayCommand(_ => CopyStepsToClipboard(DialogCapturedSteps));
    }

    private static void CopyStepsToClipboard(IEnumerable<RecordedStep> steps) {
        var list = steps.ToList();
        if (list.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        foreach (var step in list) {
            sb.AppendLine($"{step.Label}  |  {step.ControlTypeStr}  |  \"{step.ElementName}\"");
            sb.AppendLine($"Window  : {step.WindowLocator}");
            sb.AppendLine($"Dialog  : {step.DialogLocator}");
            sb.AppendLine($"[1] AutomationId : {step.AutomationIdLocator}");
            sb.AppendLine($"[2] Name         : {step.NameLocator}");
            sb.AppendLine($"[3] XPath        : {step.XPathLocator}");
            sb.AppendLine();
        }
        Clipboard.SetText(sb.ToString());
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
                    if (EnableHighLightSelectionMode || EnableFocusTrackingMode || IsAutoRecording) {
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
    public ICommand ClearDialogCaptureCommand { get; }
    public ICommand CopyAllDialogStepsCommand { get; }
    public ICommand ApplySearchCommand { get; }
    public ICommand NextMatchCommand { get; }
    public ICommand PreviousMatchCommand { get; }

    public string MatchCountText {
        get => GetProperty<string>() ?? string.Empty;
        private set => SetProperty(value);
    }

    public bool HasMatches {
        get => GetProperty<bool>();
        private set => SetProperty(value);
    }

    public bool IsSearching {
        get => GetProperty<bool>();
        set => SetProperty(value);
    }

    public bool IsAlwaysOnTop {
        get => GetProperty<bool>();
        set => SetProperty(value);
    }

    public string? SearchText {
        get => GetProperty<string>();
        set => SetProperty(value);
    }

    public SearchScope SearchScope {
        get => GetProperty<SearchScope>();
        set => SetProperty(value);
    }

    public SearchMode SearchMode {
        get => GetProperty<SearchMode>();
        set => SetProperty(value);
    }

    public IReadOnlyList<SearchMode> SearchModes { get; } = new[] {
        SearchMode.Contains,
        SearchMode.Exact,
        SearchMode.StartsWith,
        SearchMode.EndsWith
    };

    public IReadOnlyList<SearchScope> SearchScopes { get; } = new[] {
        SearchScope.Name,
        SearchScope.AutomationId,
        SearchScope.XPath
    };

    public bool IsRecording {
        get => GetProperty<bool>();
        set {
            if (SetProperty(value) && value) {
                IsAutoRecording = false;
                IsDialogCapturing = false;
            }
        }
    }

    public bool IsAutoRecording {
        get => GetProperty<bool>();
        set {
            bool changed = SetProperty(value);
            if (changed && value) {
                IsRecording = false;
                IsDialogCapturing = false;
            }
            if (changed) {
                SetMode();
            }
        }
    }

    public bool IsDialogCapturing {
        get => GetProperty<bool>();
        set {
            if (SetProperty(value) && value) {
                IsRecording = false;
                IsAutoRecording = false;
            }
        }
    }

    public bool IsCapturingDialog {
        get => GetProperty<bool>();
        private set => SetProperty(value);
    }

    public ObservableCollection<RecordedStep> RecordedSteps { get; } = [];
    public ObservableCollection<RecordedStep> DialogCapturedSteps { get; } = [];

    public void RecordElement(ElementViewModel element) {
        AddStep(RecordedSteps, element);
    }

    private static void AddStep(ObservableCollection<RecordedStep> target, ElementViewModel element) {
        var step = new RecordedStep(target.Count + 1, element);
        step.DeleteAction = () => {
            target.Remove(step);
            for (int i = 0; i < target.Count; i++)
                target[i].StepNumber = i + 1;
        };
        target.Add(step);
    }

    public void CaptureDialogTree(ElementViewModel root) {
        if (root?.AutomationElement == null || IsCapturingDialog) return;
        IsCapturingDialog = true;
        Task.Run(() => {
            try {
                var collected = new List<ElementViewModel>();
                CollectSubtree(root, collected, depth: 0, maxDepth: 50);

                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    foreach (ElementViewModel elem in collected) {
                        AddStep(DialogCapturedSteps, elem);
                    }
                });
            } catch (Exception ex) {
                _logger?.LogError($"CaptureDialogTree failed: {ex.Message}");
            } finally {
                System.Windows.Application.Current.Dispatcher.Invoke(() => IsCapturingDialog = false);
            }
        });
    }

    private static void CollectSubtree(ElementViewModel node, List<ElementViewModel> result, int depth, int maxDepth) {
        if (node == null || depth > maxDepth) return;
        result.Add(node);
        List<ElementViewModel> children;
        try {
            children = node.LoadChildren();
        } catch {
            return;
        }
        foreach (ElementViewModel c in children) {
            CollectSubtree(c, result, depth + 1, maxDepth);
        }
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
            if (EnableHighLightSelectionMode || EnableFocusTrackingMode) {
                if (SelectedItem != null) {
                    TrackSelectedItem(SelectedItem);
                }
            } else if (EnableHoverMode) {
                HoverManager.Enable(_windowHandle);
            }
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

    private void RunSearchOnBackground() {
        string? query = SearchText;
        SearchScope scope = SearchScope;
        SearchMode mode = SearchMode;
        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        System.Windows.Threading.Dispatcher dispatcher = System.Windows.Application.Current.Dispatcher;

        if (hasQuery && query!.Length >= 2) {
            List<ElementViewModel> snapshot = dispatcher.Invoke(() => Elements.ToList());
            foreach (ElementViewModel vm in snapshot) {
                ExpandSubtreeForSearchBg(vm, query, scope, mode, maxDepth: 8, currentDepth: 0, dispatcher);
            }
        }

        dispatcher.Invoke(() => {
            _matches.Clear();
            foreach (ElementViewModel vm in Elements) {
                vm.IsMatch = hasQuery && vm.Matches(query!, scope, mode);
                if (vm.IsMatch) _matches.Add(vm);
            }
            if (hasQuery && _matches.Count > 0) {
                EnableHighLightSelectionMode = true;
                _matchIndex = 0;
                SelectedItem = _matches[0];
                HasMatches = true;
                MatchCountText = $"1 / {_matches.Count}";
            } else {
                _matchIndex = -1;
                HasMatches = false;
                MatchCountText = hasQuery ? "0 / 0" : string.Empty;
            }
        });
    }

    private void GoToMatch(int direction) {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + direction + _matches.Count) % _matches.Count;
        SelectedItem = _matches[_matchIndex];
        MatchCountText = $"{_matchIndex + 1} / {_matches.Count}";
    }

    private void ExpandSubtreeForSearchBg(ElementViewModel vm, string query, SearchScope scope, SearchMode mode, int maxDepth, int currentDepth, System.Windows.Threading.Dispatcher dispatcher) {
        if (currentDepth >= maxDepth || vm.AutomationElement == null) return;

        List<ElementViewModel> children;
        try {
            children = vm.LoadChildren();
        } catch {
            return;
        }
        if (children.Count == 0) return;

        if (!SubtreeContainsMatch(children, query, scope, mode, maxDepth - currentDepth - 1)) return;

        dispatcher.Invoke(() => {
            if (!vm.IsExpanded) {
                vm.IsExpanded = true;
                ExpandElement(vm);
            }
        });

        List<ElementViewModel> directChildren = dispatcher.Invoke(() => {
            List<ElementViewModel> result = [];
            int parentIdx = Elements.IndexOf(vm);
            if (parentIdx < 0) return result;
            for (int i = parentIdx + 1; i < Elements.Count; i++) {
                ElementViewModel cur = Elements[i];
                if (cur.Level <= vm.Level) break;
                if (cur.Level == vm.Level + 1) result.Add(cur);
            }
            return result;
        });

        foreach (ElementViewModel c in directChildren) {
            ExpandSubtreeForSearchBg(c, query, scope, mode, maxDepth, currentDepth + 1, dispatcher);
        }
    }

    private static bool SubtreeContainsMatch(List<ElementViewModel> children, string query, SearchScope scope, SearchMode mode, int remainingDepth) {
        if (remainingDepth < 0) return false;
        foreach (ElementViewModel c in children) {
            if (c.Matches(query, scope, mode)) return true;
            if (remainingDepth == 0) continue;
            try {
                List<ElementViewModel> grand = c.LoadChildren();
                if (SubtreeContainsMatch(grand, query, scope, mode, remainingDepth - 1)) return true;
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