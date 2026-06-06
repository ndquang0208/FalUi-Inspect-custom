using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUInspect.Core;
using FlaUInspect.Core.Extensions;
using FlaUInspect.Core.Logger;

namespace FlaUInspect.ViewModels;

public enum SearchScope {
    Name,
    AutomationId,
    XPath
}

public enum SearchMode {
    Contains,
    Exact,
    StartsWith,
    EndsWith
}

public class ElementViewModel : ObservableObject {
    private readonly string _guidId;

    private readonly object _lockObject = new ();
    private readonly ILogger? _logger;

    public ElementViewModel(AutomationElement? automationElement, ElementViewModel? parent, int level, ILogger? logger) {
        Level = level;
        _logger = logger;
        AutomationElement = automationElement;
        Parent = parent;

        _guidId = Guid.NewGuid().ToString() + (AutomationElement?.Properties.Name.ValueOrDefault ?? string.Empty).NormalizeString();
        Name = (AutomationElement?.Properties.Name.ValueOrDefault ?? string.Empty).NormalizeString();
        AutomationId = (AutomationElement?.Properties.AutomationId.ValueOrDefault ?? string.Empty).NormalizeString();
        ControlType = AutomationElement != null && AutomationElement.Properties.ControlType.TryGetValue(out ControlType value) ? value : ControlType.Unknown;

    }

    public AutomationElement? AutomationElement { get; }
    public ElementViewModel? Parent { get; }

    public bool IsExpanded {
        get => GetProperty<bool>();
        set => SetProperty(value);
    }

    public bool IsSelected {
        get => GetProperty<bool>();
        set => SetProperty(value);
    }

    public bool IsMatch {
        get => GetProperty<bool>();
        set => SetProperty(value);
    }

    public int Level { get; }

    public string Name { get; }

    public string AutomationId { get; }

    public ControlType ControlType { get; }

    private string? _xpathCache;
    public string XPath {
        get {
            if (_xpathCache != null) return _xpathCache;
            _xpathCache = AutomationElement == null ? string.Empty : Debug.GetXPathToElement(AutomationElement);
            return _xpathCache;
        }
    }

    public override string ToString() {
        return $"{Name} [{ControlType}] : {AutomationId}";
    }

    public bool Matches(string query, SearchScope scope, SearchMode mode = SearchMode.Contains) {
        if (string.IsNullOrEmpty(query)) return false;
        string? target = scope switch {
            SearchScope.Name => Name,
            SearchScope.AutomationId => AutomationId,
            SearchScope.XPath => XPath,
            _ => null
        };
        if (target == null) return false;
        return mode switch {
            SearchMode.Contains => target.Contains(query, StringComparison.OrdinalIgnoreCase),
            SearchMode.Exact => target.Equals(query, StringComparison.OrdinalIgnoreCase),
            SearchMode.StartsWith => target.StartsWith(query, StringComparison.OrdinalIgnoreCase),
            SearchMode.EndsWith => target.EndsWith(query, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public List<ElementViewModel> LoadChildren() {
        {


            try {
                if (AutomationElement != null) {
                    using (CacheRequest.ForceNoCache()) {
                        AutomationElement[] elements = AutomationElement.FindAllChildren();

                        return elements.Select(element => new ElementViewModel(element, this, Level + 1, _logger)).ToList();
                    }
                }
            } catch (Exception ex) {
                _logger?.LogError($"Exception: {ex.Message}");
            }

            return [];
        }
    }
}