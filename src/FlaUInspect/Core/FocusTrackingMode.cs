using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.EventHandlers;
using Application = System.Windows.Application;

namespace FlaUInspect.Core;

public class FocusTrackingMode(AutomationBase? automation, Action<AutomationElement> onFocusChangedAction) {
    private AutomationElement? _currentFocusedElement;
    private FocusChangedEventHandlerBase? _eventHandler;

    public void Start() {
        // Might give problems because inspect is registered as well.
        // MS recommends to call UIA commands on a thread outside a UI thread.
        Task.Factory.StartNew(() => _eventHandler = automation?.RegisterFocusChangedEvent(OnFocusChanged));
    }

    public void Stop() {
        FocusChangedEventHandlerBase? handler = Interlocked.Exchange(ref _eventHandler, null);
        if (handler != null) {
            Task.Run(() => {
                try {
                    automation?.UnregisterFocusChangedEvent(handler);
                } catch {
                    // ignored
                }
            });
        }
    }

    private void OnFocusChanged(AutomationElement? automationElement) {
        try {
            // Skip items in the current process
            // Like Inspect itself or the overlay window
            if (automationElement?.Properties.ProcessId == Environment.ProcessId) {
                return;
            }
        } catch {
            return;
        }

        if (!Equals(_currentFocusedElement, automationElement)) {
            _currentFocusedElement = automationElement;

            if (automationElement != null) {
                Application.Current.Dispatcher.Invoke(() => {
                    onFocusChangedAction(automationElement);
                });
            }
        }
    }
}