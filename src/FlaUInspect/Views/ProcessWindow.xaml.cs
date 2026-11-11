using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlaUI.Core.AutomationElements;
using FlaUInspect.Core.Logger;
using FlaUInspect.ViewModels;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using Window = System.Windows.Window;

namespace FlaUInspect.Views;

public partial class ProcessWindow : Window {
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new (-1);

    public ProcessWindow() {
        InitializeComponent();
        Closed += ProcessWindow_Closed;
        Loaded += MainWindow_Loaded;
        Activated += (_, _) => ReassertTopmost();
        Deactivated += async (_, _) => {
            // Wait for the window that took focus to finish raising itself, otherwise it ends up above us.
            await Task.Delay(150);
            ReassertTopmost();
        };
    }

    // WPF applies Topmost only when the property changes. A target app that is itself topmost (kiosk / POS apps)
    // is raised above us when it is Alt+Tabbed to, and nothing puts us back, so re-assert it on focus changes.
    private void ReassertTopmost() {
        if (DataContext is not ProcessViewModel { IsAlwaysOnTop: true } || WindowState == WindowState.Minimized) {
            return;
        }

        IntPtr handle = new WindowInteropHelper(this).Handle;

        if (handle != IntPtr.Zero) {
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int flags);

    private void MainWindow_Loaded(object sender, EventArgs e) {
        if (DataContext is ProcessViewModel processViewModel) {
            processViewModel.CopiedNotificationRequested += ShowCopiedNotification;
            processViewModel.CopiedNotificationCurrentElementSaveStateRequested += ShowCopiedNotificationCurrentElementSaveStateRequested;
        }
    }

    private async void ShowCopiedNotification() {
        CopiedNotificationGrid.Visibility = Visibility.Visible;
        DoubleAnimation animation = new (1, 0, TimeSpan.FromSeconds(1));
        CopiedNotificationGrid.BeginAnimation(OpacityProperty, animation);
        await Task.Delay(1000);
        CopiedNotificationGrid.Visibility = Visibility.Collapsed;
    }

    private async void ShowCopiedNotificationCurrentElementSaveStateRequested() {
        CopiedNotificationCurrentElementSaveStateGrid.Visibility = Visibility.Visible;
        DoubleAnimation animation = new (1, 0, TimeSpan.FromSeconds(1));
        CopiedNotificationCurrentElementSaveStateGrid.BeginAnimation(OpacityProperty, animation);
        await Task.Delay(1000);
        CopiedNotificationCurrentElementSaveStateGrid.Visibility = Visibility.Collapsed;
    }

    private void ProcessWindow_Closed(object? sender, EventArgs e) {
        if (Application.Current.Windows.Count == 1 && Application.Current.MainWindow is StartupWindow startupWindow) {
            if (DataContext is ProcessViewModel processViewModel) {
                if (processViewModel.ClosingCommand.CanExecute(DataContext)) {
                    processViewModel.ClosingCommand.Execute(DataContext);
                }
            }
            
            startupWindow.Show();
        }
    }

    private void SelectWindowClick(object sender, RoutedEventArgs e) {
        App.Logger.LogInformation("Folder button: show the process list");
        (Application.Current.MainWindow as StartupWindow)?.Show();
    }

    private void TreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) {
        if (DataContext is ProcessViewModel processViewModel) {
            processViewModel.SelectedItem = e.NewValue as ElementViewModel;
        }
    }

    private void InvokePatternActionHandler(object sender, RoutedEventArgs e) {
        PatternItem? vm = (sender as Button)?.DataContext as PatternItem;

        if (vm?.Action != null) {
            Task.Run(() => {
                vm.Action();
            });
        }
    }

    private void TreeOnSelectionChanged(object sender, SelectionChangedEventArgs e) {
        object? selected = TreeViewControl.SelectedItem;
        if (selected == null) return;
        TreeViewControl.ScrollIntoView(selected);
        Dispatcher.BeginInvoke(new Action(() => {
            if (TreeViewControl.ItemContainerGenerator.ContainerFromItem(selected) is ListViewItem container) {
                container.BringIntoView();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) {
        ToggleButton? expandToggleButton = sender as ToggleButton;

        if (expandToggleButton == null) {
            return;
        }

        ProcessViewModel? processViewModel = DataContext as ProcessViewModel;
        ElementViewModel? elementViewModel = expandToggleButton.DataContext as ElementViewModel;

        if (processViewModel == null) {
            return;
        }

        if (elementViewModel == null) {
            return;
        }

        if (expandToggleButton.IsChecked == true) {
            AutomationElement? automationElement = elementViewModel?.AutomationElement;
            processViewModel?.ElementToSelectChanged(automationElement, true);
        } else if (expandToggleButton.IsChecked == false) {
            processViewModel.CollapseElement(elementViewModel);
        }
    }

    private void TreeViewControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (DataContext is not ProcessViewModel processViewModel) return;
        bool capturesWindow = processViewModel.IsDialogCapturing || processViewModel.IsDialogCapturingWithDetails;
        if (!processViewModel.IsRecording && !capturesWindow) return;

        ElementViewModel? element = FindElementViewModelAtPoint(e.GetPosition(TreeViewControl));
        if (element == null) return;

        if (capturesWindow) {
            processViewModel.CaptureDialogTree(element);
        } else {
            processViewModel.RecordElement(element);
        }
    }

    private ElementViewModel? FindElementViewModelAtPoint(Point point) {
        HitTestResult? hitResult = VisualTreeHelper.HitTest(TreeViewControl, point);
        if (hitResult == null) return null;

        DependencyObject? dep = hitResult.VisualHit;
        while (dep != null) {
            if (dep is ListViewItem item)
                return item.DataContext as ElementViewModel;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    private void CopyElementTree_Click(object sender, RoutedEventArgs e) {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: ElementViewModel elementViewModel } } }
            && DataContext is ProcessViewModel processViewModel) {
            Task.Run(() => processViewModel.CopyElementTreeToClipboard(elementViewModel));
        }
    }

    private void ProcessWindowOnClosed(object? sender, EventArgs e) {
        if (DataContext is ProcessViewModel processViewModel) {
            if (processViewModel.ClosingCommand.CanExecute(DataContext)) {
                processViewModel.ClosingCommand.Execute(DataContext);
            }
        }
    }
}