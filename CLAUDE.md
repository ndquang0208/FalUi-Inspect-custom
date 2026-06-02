# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Restore tools first (Cake build system)
dotnet tool restore

# Build
dotnet cake --target "Build"

# Build and run tests (default target)
dotnet cake --target "Test"

# Full package (build + test + pack)
dotnet cake --target "Package"

# Or use dotnet directly
dotnet build src/FlaUInspect.sln
dotnet run --project src/FlaUInspect
```

The project has four build configurations: `Debug`, `Release`, `UIA2`, `UIA3`. The `UIA2`/`UIA3` configs control which Windows UI Automation backend is compiled in via `#if AUTOMATION_UIA3` / `#elif AUTOMATION_UIA2` preprocessor symbols in `App.xaml.cs`. The executable output is at `src/FlaUInspect/bin/Debug/net8.0-windows10.0.19041.0/FlaUInspect.exe`.

There are no test projects — the `Test` Cake target just runs `dotnet test` and will find nothing.

## Code Style

`.editorconfig` enforces: opening braces on same line (`none`), no newline before `else`/`catch`/`finally`. Nullable reference types and implicit usings are enabled.

## Architecture

### Bootstrap

`App.xaml.cs` starts the DI container, loads `appsettings.json` via `JsonSettingsService<FlaUiAppSettings>`, configures `FlaUiAppOptions` (overlay factory delegates), applies the saved theme, then shows `StartupWindow`. Static accessors `App.Services`, `App.FlaUiAppOptions`, and `App.Logger` are used throughout.

### Two-Window Flow

1. **StartupWindow / StartupViewModel** — user picks a running process (list or mouse-click pick). On confirm, creates a `ProcessWindow` with a `ProcessViewModel` scoped to that process.
2. **ProcessWindow / ProcessViewModel** — the main inspector. When the ProcessWindow closes it calls `ClosingCommand` for cleanup then returns to StartupWindow.

### Element Tree (ProcessWindow)

The tree is a flat `ObservableCollection<ElementViewModel>` displayed in a `ListView` with indent-via-margin, **not** a `TreeView`. Expansion inserts children after the parent in the flat list; collapse removes descendants. `ElementViewModel.Level` drives indentation via `IndentConverter`.

`ElementToSelectChanged(AutomationElement, forceExpand)` is the central selection method — it walks from an element to root, expands ancestors in the flat list, then sets `SelectedItem`.

`ElementViewModel.XPath` is lazy-cached in `_xpathCache` (computed once via `Debug.GetXPathToElement`) because filter and locator code reads it many times per element.

### Initialize / Refresh Threading

`ProcessViewModel.Initialize()` runs from `Task.Run` (called by `StartupWindow.OpenProcessWindow` once, and by `RefreshCommand` thereafter). It is split into two phases:
1. **BG phase** — UIA calls (`GetDesktop` / `FromHandle` / `LoadChildren`) plus constructing a fresh `FocusTrackingMode` instance. Wrapped in try/catch; on failure it logs and returns without mutating UI.
2. **UI phase** (`Application.Current.Dispatcher.Invoke`) — stops the old `_focusTrackingMode` to prevent UIA handler accumulation across Refreshes, then swaps `Elements`, `ElementPatterns`, and `SelectedItem`, and fires `OnPropertyChanged`.

Any future addition to Initialize must respect this split — never touch WPF-bound state from the BG phase.

### Tree Search / Filter

Above the element `ListView` sits a search bar: a `ComboBox` bound to `ProcessViewModel.SearchScope` (`SearchScope` enum: `Name`, `AutomationId`, `XPath`) + a `TextBox` bound to `SearchText` + a clear button (`×`).

Filter does **not** run on every keystroke — only when the user presses Enter (`KeyBinding` → `ApplySearchCommand`) or clicks clear (`ClearSearchCommand`). This is intentional: `ApplySearchFilter` synchronously walks the UIA tree (`LoadChildren` recursion up to `maxDepth: 8`) to expand subtrees that contain matches, which would freeze the UI if triggered on every character.

`ApplySearchFilter` does three things:
1. `ExpandTreeForSearch` — recursively probes children via `LoadChildren` up to depth 8, expanding any branch whose subtree contains a match. Modifies the live `Elements` collection.
2. Sets `IsMatch` on every loaded element (drives a `DataTrigger` for bold + orange highlight).
3. Installs an `ICollectionView.Filter` on `Elements` that keeps an element if it matches, has a matching ancestor, or has a matching descendant in the loaded flat list.

`ElementViewModel.Matches(query, scope)` is case-insensitive `Contains` against one of `Name` / `AutomationId` / `XPath` depending on scope.

**Known limitation:** the filter is bound to the current `Elements` instance. `Initialize` replaces `Elements` wholesale, so Refresh clears the filter — `SearchText` survives in the box but the UI shows the full tree until the user presses Enter again.

### Three Selection Modes

All three are mutually exclusive radio buttons. `SetMode()` is called on every mode change:

| Mode | Trigger | Mechanism |
|------|---------|-----------|
| `EnableHoverMode` | Ctrl + mouse over target app | `HoverManager` (300ms timer, `FromPoint`) |
| `EnableHighLightSelectionMode` | Manual tree click | `TrackSelectedItem` shows overlay on selection |
| `EnableFocusTrackingMode` | Tab/focus in target app | `FocusTrackingMode` (UIA `FocusChangedEvent`) |

`IsAutoRecording` (AUTO button) starts focus tracking regardless of the radio selection and auto-appends every focused element to `RecordedSteps`.

### Recording Modes (REC / AUTO / WIN) — mutually exclusive

> ⚠️ **Important:** `IsRecording` (REC), `IsAutoRecording` (AUTO), and `IsDialogCapturing` (WIN, was DLG) are **mutually exclusive**. Their setters in `ProcessViewModel` force the other two to `false` when one is enabled. Reason: enabling AUTO while REC was on previously caused REC's `Ctrl+Click` to appear broken because AUTO's focus-driven auto-record fired in parallel and the user couldn't tell whose step was being added.

The toolbar button **CLR** was removed — use the **Clear All Steps** button inside the Locator/Window tabs instead. The **DLG** toggle was renamed to **WIN** (and the "Dialog" tab header → "Window") because there is no separate UIA Dialog control type; it captures any window-root subtree.

### Toggle button visual state

> ⚠️ The default WPF `ToggleButton` template has no checked-state visual. `Controls/ToggleButtons.xaml` now defines `Button.Checked.Background/Border/Foreground` (uses `AccentColor`, white text) and an `IsChecked=True` trigger on the template so REC / AUTO / WIN / TOP visibly highlight when active. Any new ribbon toggle must use `RibbonToggleButtonStyle` to inherit this.

### Hover scroll-into-view

`ProcessWindow.TreeOnSelectionChanged` first calls `ListView.ScrollIntoView(selected)` (virtualization-safe), then queues a background-priority `BringIntoView` once the container is realized. The earlier `ContainerFromItem` + `BringIntoView` alone failed silently when the selected element was outside the realized viewport — important for hover/focus tracking that selects deep elements off-screen.

### Recording & Locator Panel

`RecordedSteps` (`ObservableCollection<RecordedStep>`) is populated by:
- **Manual (REC):** `Ctrl+Click` on a tree item → `ProcessViewModel.RecordElement()`
- **Auto (AUTO):** every focus-change callback → `RecordElement(SelectedItem)`
- **Window capture (WIN):** `Ctrl+Click` a window root → `CaptureDialogTree()` enumerates the subtree into `DialogCapturedSteps` (the "Window" tab).

`RecordedStep` computes all locators once in its constructor from the element's `Parent` chain (available because tree expansion sets `ElementViewModel.Parent`). It generates a custom XPath walking from the root `Window` down: `ControlType[@AutomationId='x']` if AutomationId present, else `ControlType[N]` using sibling index via `FindAllChildren()`.

The card header is **`Label  |  ControlType  |  "Name"`** where `Label` = `"Window Element"` when `ControlType == Window`, else `"Element"`. The numeric `StepNumber` is still tracked internally (for delete re-indexing) but no longer shown — bulk copy (`CopyAllStepsCommand` / per-step `CopyAllCommand`) uses `Label` too.

### MVVM Base

`ObservableObject` backs all properties via an internal `Dictionary<string, object?>` — no explicit backing fields needed. Use `GetProperty<T>()` / `SetProperty(value)` in property getters/setters. `SetProperty` returns `bool` (changed or not) for side-effect chaining.

Commands are `RelayCommand(Action<object?>)` or `AsyncRelayCommand`.

### Settings & Themes

`FlaUiAppSettings` (saved to `appsettings.json`) holds `Theme` and three `OverlaySettings`. The edit dialog pattern uses `Editable<T>` — an `Original`/`Current` wrapper with `IsDirty`, `Apply()`, and `Reset()`. `App.SetTheme()` swaps `LightTheme.xaml` / `DarkTheme.xaml` into `Application.Resources.MergedDictionaries` at runtime. All colors/brushes in XAML use `{DynamicResource ...Brush}` keys defined in those theme files.

### Overlays

`ElementOverlay` renders a highlight rectangle over a target element using FlaUI's native overlay forms. Three overlay instances exist: hover, selection, pick. Configured via `OverlaySettings` (size, margin, color, Fill/Border mode). Always `Dispose()` before re-creating.

### Patterns / Details Panel

`PatternItemsFactory` reflects all UIA patterns supported by the selected element and produces `PatternItem[]` grouped by `ElementPatternItem`. Special groups `Identification`, `Details`, and `PatternSupport` are always shown; UIA pattern groups are shown only when supported. `PatternItem` can hold an `Action` for executable patterns (invoked off the UI thread from `InvokePatternActionHandler`).

### Copy Element Tree (Feature 3)

Right-click a tree item → "Copy Element Tree". Traverses the subtree via `TreeWalkerFactory.GetRawViewWalker()` (depth-capped at 50) and serializes to indented JSON with `System.Text.Json`. Runs on a background `Task`; clipboard write is marshalled back via `Dispatcher.Invoke`.

### Icon Resources

Icons are path-based SVG-like XAML in `Resources/Icons.xaml`, `RibbonIcons.xaml`, `DetailsIcons.xaml`. Reference them as `{StaticResource SomeIconName}` inside a `<Viewbox><ContentControl Content="..."/></Viewbox>`. New ribbon buttons should follow the existing `RibbonButtonStyle` / `RibbonToggleButtonStyle` pattern.
