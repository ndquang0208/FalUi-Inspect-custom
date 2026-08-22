using System.Collections.ObjectModel;

namespace FlaUInspect.ViewModels;

/// <summary>
///     A snapshot of one detail/pattern group (Identification, Details, Pattern Support, ValuePattern, ...)
///     taken at the moment an element was recorded. Mirrors what the Details tab shows for the selected item,
///     but frozen in time so a recorded step keeps the values it had when it was captured.
/// </summary>
public class RecordedDetailGroup(string name, IEnumerable<PatternItem> items) {
    public string Name { get; } = name;
    public ObservableCollection<PatternItem> Items { get; } = new (items);
}
