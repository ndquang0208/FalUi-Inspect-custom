using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUInspect.Core.Logger;
using AutomationElement = FlaUI.Core.AutomationElements.AutomationElement;
using Mouse = FlaUI.Core.Input.Mouse;
using Point = System.Drawing.Point;

namespace FlaUInspect.Core;

public static class HoverManager {
    private const uint GaRoot = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int ErrorAccessDenied = 5;

    private static Func<ElementOverlay?>? _elementOverlayFunc;
    private static ILogger? _logger;
    private static AutomationBase? _automationBase;
    private static AutomationElement? _hoveredElement;
    private static ElementOverlay? _elementOverlay;
    private static int _hoveredProcessId;
    private static IntPtr _hoveredWindowHandle;
    private static IntPtr _hoveredRootWindowHandle;

    private static readonly List<KeyValuePair<IntPtr, Action<AutomationElement?>>> Listeners = [];

    private static readonly HashSet<IntPtr> EnabledListeners = [];

    private static readonly object LockObject = new ();

    static HoverManager() {
        DispatcherTimer timer = new() {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        timer.Tick += (s, e) => Refresh();
        timer.Start();
    }

    public static bool IsInitialized => _automationBase != null && _elementOverlayFunc != null;

    /// <summary>
    ///     Raised on the UI thread when a Ctrl-hover probe lands on a window of this process (the inspector itself or
    ///     one of its overlays). The selection overlay sits on top of the element it marks, so it has to get out of
    ///     the way here, or that element could not be hovered while it is highlighted.
    /// </summary>
    public static event Action<Point>? OwnProcessHit;

    public static void ClearOverlay() {
        _elementOverlay?.Dispose();
        _elementOverlay = null;
        _hoveredElement = null;
        _hoveredProcessId = 0;
        _hoveredWindowHandle = IntPtr.Zero;
        _hoveredRootWindowHandle = IntPtr.Zero;
    }

    public static bool IsProcessAlive(int processId) {
        if (processId <= 0) {
            return true;
        }

        IntPtr hProcess = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (hProcess == IntPtr.Zero) {
            return Marshal.GetLastWin32Error() == ErrorAccessDenied;
        }

        try {
            return GetExitCodeProcess(hProcess, out uint exitCode) && exitCode == StillActive;
        } finally {
            CloseHandle(hProcess);
        }
    }

    public static bool IsWindowOpen(IntPtr hWnd) {
        return hWnd != IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd);
    }

    public static bool IsWindowAliveAndVisible(IntPtr hWnd) {
        return hWnd != IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd) && !IsIconic(hWnd);
    }

    public static IntPtr GetRootWindow(IntPtr hWnd) {
        return hWnd != IntPtr.Zero ? GetAncestor(hWnd, GaRoot) : IntPtr.Zero;
    }

    private static bool IsHoveredTargetAlive() {
        if (_hoveredProcessId > 0 && !IsProcessAlive(_hoveredProcessId)) {
            return false;
        }

        if (_hoveredWindowHandle != IntPtr.Zero && !IsWindowAliveAndVisible(_hoveredWindowHandle)) {
            return false;
        }

        if (_hoveredRootWindowHandle != IntPtr.Zero && !IsWindowAliveAndVisible(_hoveredRootWindowHandle)) {
            return false;
        }

        lock (LockObject) {
            bool hasSpecificWindowListener = false;
            bool anySpecificWindowAlive = false;
            foreach (IntPtr listenerHwnd in EnabledListeners) {
                if (listenerHwnd != IntPtr.Zero) {
                    hasSpecificWindowListener = true;
                    if (IsWindowAliveAndVisible(listenerHwnd)) {
                        anySpecificWindowAlive = true;
                        break;
                    }
                }
            }

            if (hasSpecificWindowListener && !anySpecificWindowAlive) {
                return false;
            }
        }

        return true;
    }

    private static IntPtr ResolveWindowHandle(Point screenPos, AutomationElement element) {
        try {
            if (element.Properties.NativeWindowHandle.TryGetValue(out IntPtr nativeHwnd) && nativeHwnd != IntPtr.Zero) {
                return nativeHwnd;
            }
        } catch {
            // ignored
        }

        IntPtr hwnd = WindowFromPoint(new POINT { X = screenPos.X, Y = screenPos.Y });
        if (hwnd != IntPtr.Zero) {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != (uint)Environment.ProcessId) {
                return hwnd;
            }
        }

        return IntPtr.Zero;
    }

    public static bool IsLeafInteractiveControl(ControlType controlType) {
        return controlType is ControlType.Button
            or ControlType.SplitButton
            or ControlType.CheckBox
            or ControlType.RadioButton
            or ControlType.Hyperlink
            or ControlType.MenuItem
            or ControlType.Edit
            or ControlType.Slider
            or ControlType.Spinner
            or ControlType.ProgressBar
            or ControlType.Thumb;
    }

    private static bool IsContainerOrListLike(ControlType controlType) {
        return controlType is ControlType.List
            or ControlType.ListItem
            or ControlType.DataGrid
            or ControlType.DataItem
            or ControlType.Table
            or ControlType.Tree
            or ControlType.TreeItem
            or ControlType.Pane
            or ControlType.Group
            or ControlType.Custom
            or ControlType.Window
            or ControlType.Document
            or ControlType.ScrollBar;
    }

    private static ControlType TryGetControlType(AutomationElement element) {
        try {
            if (element.Properties.ControlType.TryGetValue(out ControlType ct)) {
                return ct;
            }
        } catch {
            // ignored
        }
        return ControlType.Unknown;
    }

    private static Rectangle TryGetBoundingRectangle(AutomationElement element) {
        try {
            if (element.Properties.BoundingRectangle.TryGetValue(out Rectangle rect)) {
                return rect;
            }
        } catch {
            // ignored
        }
        return Rectangle.Empty;
    }

    private static bool IsElementOffscreen(AutomationElement element) {
        try {
            if (element.Properties.IsOffscreen.TryGetValue(out bool offscreen)) {
                return offscreen;
            }
        } catch {
            // ignored
        }
        return false;
    }

    private static long Area(Rectangle rect) {
        return rect.Width > 0 && rect.Height > 0 ? (long)rect.Width * rect.Height : long.MaxValue;
    }

    /// <summary>
    ///     Refines the raw element returned by UIA <c>FromPoint</c>:
    ///     1. If <c>FromPoint</c> hit a non-control inner template part (e.g. an icon/path/text inside a Button),
    ///        walk up to the owning leaf interactive control in ControlView.
    ///     2. If <c>FromPoint</c> hit a List / ListItem / container underneath a floating overlay control (such as a
    ///        "scroll to bottom" button overlaid on a list), scan recent sibling controls in reverse Z-order to find
    ///        the floating control covering <paramref name="screenPos" />.
    ///     3. If the element is a container, drill down to the smallest visible child covering <paramref name="screenPos" />.
    /// </summary>
    private static AutomationElement RefineHoveredElement(AutomationElement element, Point screenPos, int processId) {
        if (_automationBase == null) {
            return element;
        }

        try {
            ITreeWalker controlWalker = _automationBase.TreeWalkerFactory.GetControlViewWalker();
            AutomationElement current = NormalizeToControlParentIfInnerPart(element, controlWalker, processId);
            Rectangle currentRect = TryGetBoundingRectangle(current);
            ControlType currentType = TryGetControlType(current);

            if (IsContainerOrListLike(currentType)) {
                AutomationElement? overlaySibling = FindOverlaySiblingAtPoint(current, currentRect, currentType, screenPos, controlWalker, processId);
                if (overlaySibling != null) {
                    current = overlaySibling;
                    currentRect = TryGetBoundingRectangle(current);
                }
            }

            for (int depth = 0; depth < 3; depth++) {
                if (IsLeafInteractiveControl(TryGetControlType(current))) {
                    break;
                }

                AutomationElement? smallerChild = FindSmallestChildAtPoint(current, currentRect, screenPos, controlWalker);
                if (smallerChild == null) {
                    break;
                }

                current = smallerChild;
                currentRect = TryGetBoundingRectangle(current);
            }

            return current;
        } catch {
            return element;
        }
    }

    private static AutomationElement NormalizeToControlParentIfInnerPart(AutomationElement element, ITreeWalker controlWalker, int processId) {
        try {
            if (element.Properties.IsControlElement.TryGetValue(out bool isControl) && !isControl) {
                AutomationElement? controlParent = controlWalker.GetParent(element);
                if (controlParent != null
                    && controlParent.Properties.ProcessId.TryGetValue(out int parentPid)
                    && parentPid == processId
                    && IsLeafInteractiveControl(TryGetControlType(controlParent))) {
                    return controlParent;
                }
            }
        } catch {
            // ignored
        }
        return element;
    }

    private static AutomationElement? FindOverlaySiblingAtPoint(
        AutomationElement current,
        Rectangle currentRect,
        ControlType currentType,
        Point screenPos,
        ITreeWalker walker,
        int processId) {
        long currentArea = Area(currentRect);
        AutomationElement branch = current;

        for (int level = 0; level < 2; level++) {
            AutomationElement? parent;
            try {
                parent = walker.GetParent(branch);
            } catch {
                break;
            }

            if (parent == null
                || !parent.Properties.ProcessId.TryGetValue(out int parentPid)
                || parentPid != processId) {
                break;
            }

            long parentArea = Area(TryGetBoundingRectangle(parent));
            AutomationElement? bestCandidate = null;
            long bestArea = currentArea;

            try {
                AutomationElement? child = walker.GetLastChild(parent);
                int scanned = 0;
                while (child != null && scanned < 20) {
                    scanned++;
                    if (!child.Equals(branch) && !child.Equals(current)) {
                        ControlType childType = TryGetControlType(child);
                        bool isRowItem = childType is ControlType.ListItem or ControlType.DataItem or ControlType.TreeItem;
                        if (!isRowItem && !IsElementOffscreen(child)) {
                            Rectangle childRect = TryGetBoundingRectangle(child);
                            if (childRect.Width > 0 && childRect.Height > 0 && childRect.Contains(screenPos)) {
                                long childArea = Area(childRect);
                                bool isOverlayOnList = (currentType is ControlType.List or ControlType.ListItem or ControlType.DataGrid or ControlType.DataItem or ControlType.Table)
                                                       && childArea < parentArea / 2;
                                if (childArea < bestArea || (bestCandidate == null && isOverlayOnList)) {
                                    bestCandidate = child;
                                    bestArea = childArea;
                                }
                            }
                        }
                    }

                    child = walker.GetPreviousSibling(child);
                }
            } catch {
                // ignored
            }

            if (bestCandidate != null) {
                return bestCandidate;
            }

            branch = parent;
        }

        return null;
    }

    private static AutomationElement? FindSmallestChildAtPoint(
        AutomationElement parent,
        Rectangle parentRect,
        Point screenPos,
        ITreeWalker walker) {
        long parentArea = Area(parentRect);
        AutomationElement? bestChild = null;
        long bestArea = parentArea;

        try {
            AutomationElement? child = walker.GetLastChild(parent);
            int scanned = 0;
            while (child != null && scanned < 25) {
                scanned++;
                if (!IsElementOffscreen(child)) {
                    Rectangle childRect = TryGetBoundingRectangle(child);
                    if (childRect.Width > 0 && childRect.Height > 0 && childRect.Contains(screenPos)) {
                        long childArea = Area(childRect);
                        if (childArea < bestArea || (bestChild == null && childArea <= parentArea)) {
                            bestChild = child;
                            bestArea = childArea;
                        }
                    }
                }

                child = walker.GetPreviousSibling(child);
            }
        } catch {
            // ignored
        }

        return bestChild;
    }

    private static void Refresh() {
        if (EnabledListeners.Count == 0) {
            ClearOverlay();
            return;
        }

        if (_elementOverlay != null && !IsHoveredTargetAlive()) {
            ClearOverlay();
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
            Point screenPos = Mouse.Position;

            try {
                AutomationElement? automationElement = _automationBase?.FromPoint(screenPos);
                int elementProcessId = automationElement?.Properties.ProcessId.ValueOrDefault ?? 0;

                if (automationElement == null || elementProcessId == Environment.ProcessId) {
                    bool hadOverlayAtPoint = _elementOverlay?.Covers(screenPos) == true;
                    ClearOverlay();

                    if (automationElement != null) {
                        OwnProcessHit?.Invoke(screenPos);
                        automationElement = _automationBase?.FromPoint(screenPos);
                        elementProcessId = automationElement?.Properties.ProcessId.ValueOrDefault ?? 0;
                    }

                    if (automationElement == null || elementProcessId == Environment.ProcessId) {
                        return;
                    }
                }

                if (elementProcessId > 0 && !IsProcessAlive(elementProcessId)) {
                    ClearOverlay();
                    return;
                }

                automationElement = RefineHoveredElement(automationElement, screenPos, elementProcessId);

                if (_hoveredElement == null || !automationElement.Equals(_hoveredElement) || _elementOverlay == null) {
                    ClearOverlay();
                    _hoveredElement = automationElement;
                    _hoveredProcessId = elementProcessId;

                    KeyValuePair<IntPtr, Action<AutomationElement?>>[] listenersSnapshot;
                    lock (LockObject) {
                        listenersSnapshot = Listeners.ToArray();
                    }

                    foreach (KeyValuePair<IntPtr, Action<AutomationElement?>> keyValuePair in listenersSnapshot) {
                        try {
                            keyValuePair.Value?.Invoke(automationElement);
                        } catch {
                            // ignored
                        }
                    }

                    _hoveredWindowHandle = ResolveWindowHandle(screenPos, automationElement);
                    _hoveredRootWindowHandle = GetRootWindow(_hoveredWindowHandle);

                    if (!IsHoveredTargetAlive()) {
                        ClearOverlay();
                        return;
                    }

                    try {
                        if (_elementOverlayFunc != null && EnabledListeners.Count > 0) {
                            ElementOverlay? elementOverlay = _elementOverlayFunc();
                            elementOverlay?.Show(automationElement.Properties.BoundingRectangle.Value);
                            _elementOverlay = elementOverlay;
                        }
                    } catch {
                        ClearOverlay();
                    }
                }
            } catch {
                ClearOverlay();
            }
        }
    }

    public static void AddListener(IntPtr id, Action<AutomationElement?> onElementHovered) {
        lock (LockObject) {
            Listeners.Add(new KeyValuePair<IntPtr, Action<AutomationElement?>>(id, onElementHovered));
        }
    }

    public static void RemoveListener(IntPtr id) {
        lock (LockObject) {
            Listeners.RemoveAll(x => x.Key == id);
            EnabledListeners.Remove(id);
        }
        ClearOverlay();
    }

    public static void Enable(IntPtr intPtr) {
        lock (LockObject) {
            EnabledListeners.Add(intPtr);
        }
    }

    public static void Disable(IntPtr intPtr) {
        lock (LockObject) {
            EnabledListeners.Remove(intPtr);
        }
        ClearOverlay();
    }

    public static void Initialize(AutomationBase? automation, Func<ElementOverlay?> elementOverlayFunc, ILogger? logger) {
        _automationBase = automation;
        _logger = logger;
        _elementOverlayFunc = elementOverlayFunc;
    }

    public static void SetAutomation(AutomationBase? automation) {
        if (automation != null) {
            _automationBase = automation;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT {
        public int X;
        public int Y;
    }
}