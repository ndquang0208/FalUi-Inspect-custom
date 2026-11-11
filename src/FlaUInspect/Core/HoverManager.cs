using System.Windows.Input;
using System.Windows.Threading;
using FlaUI.Core;
using FlaUInspect.Core.Logger;
using AutomationElement = FlaUI.Core.AutomationElements.AutomationElement;
using Mouse = FlaUI.Core.Input.Mouse;
using Point = System.Drawing.Point;

namespace FlaUInspect.Core;

public static class HoverManager {
    private static Func<ElementOverlay?>? _elementOverlayFunc;
    private static ILogger? _logger;
    private static AutomationBase? _automationBase;
    private static AutomationElement? _hoveredElement;
    private static ElementOverlay? _elementOverlay;

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

    private static void Refresh() {
        if (EnabledListeners.Count == 0) {
            _elementOverlay?.Dispose();
            _hoveredElement = null;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
            Point screenPos = Mouse.Position;

            try {
                AutomationElement? automationElement = _automationBase?.FromPoint(screenPos);

                if (automationElement == null || automationElement?.Properties.ProcessId == Environment.ProcessId) {
                    _elementOverlay?.Dispose();
                    _hoveredElement = null;

                    if (automationElement != null) {
                        OwnProcessHit?.Invoke(screenPos);
                    }
                    return;
                }

                if (automationElement != null && (_hoveredElement == null || !automationElement.Equals(_hoveredElement))) {
                    _elementOverlay?.Dispose();
                    _hoveredElement = automationElement;

                    foreach (KeyValuePair<IntPtr, Action<AutomationElement?>> keyValuePair in Listeners) {
                        try {
                            keyValuePair.Value?.Invoke(automationElement);
                        } catch {
                            // ignored
                        }
                    }

                    try {
                        if (_elementOverlayFunc != null && EnabledListeners.Count > 0) {
                            ElementOverlay? elementOverlay = _elementOverlayFunc();
                            elementOverlay?.Show(automationElement.Properties.BoundingRectangle.Value);
                            _elementOverlay = elementOverlay;
                        }
                    } catch {
                        // ignored
                    }
                }
            } catch {
                // ignored
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
            KeyValuePair<IntPtr, Action<AutomationElement?>>? pair = Listeners.FirstOrDefault(x => x.Key == id);

            if (pair != null) {
                Listeners.Remove(pair.Value);
            }
        }
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
    }

    public static void Initialize(AutomationBase? automation, Func<ElementOverlay?> elementOverlayFunc, ILogger? logger) {
        _automationBase = automation;
        _logger = logger;
        _elementOverlayFunc = elementOverlayFunc;
    }
}