using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartDock.Core.Widgets;

namespace SmartDock.App.ViewModels;

public sealed class WidgetCatalogItem : ObservableObject
{
    private readonly DockViewModel _dock;

    public WidgetCatalogItem(WidgetDescriptor descriptor, DockViewModel dock)
    {
        Descriptor = descriptor;
        _dock = dock;
        AddCommand = new AsyncRelayCommand(() => _dock.AddWidgetAsync(descriptor.TypeId), () => _dock.CanAdd(descriptor.TypeId));
        RemoveCommand = new AsyncRelayCommand(RemoveLastAsync, () => Count > 0);
    }

    public WidgetDescriptor Descriptor { get; }

    public string Name => Descriptor.Name;

    public string Description => Descriptor.Description;

    public string Category => Descriptor.Category.ToString();

    public string Glyph => Descriptor.IconGlyph;

    public string TypeId => Descriptor.TypeId;

    public int Count => _dock.CountOf(Descriptor.TypeId);

    public string StatusText => Count switch
    {
        0 => "Not in dock",
        1 => "In dock",
        _ => $"In dock ×{Count}",
    };

    public AsyncRelayCommand AddCommand { get; }

    public AsyncRelayCommand RemoveCommand { get; }

    public void Update()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(StatusText));
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    private async Task RemoveLastAsync()
    {
        var last = _dock.CenterWidgets.Concat(_dock.RightWidgets).LastOrDefault(w => w.Descriptor.TypeId == Descriptor.TypeId);
        if (last is not null)
        {
            await _dock.RemoveWidgetAsync(last.InstanceId);
        }
    }
}

/// <summary>Searchable, filterable catalogue of widget types; changes apply to the dock immediately.</summary>
public sealed class WidgetManagerViewModel : ObservableObject, IDisposable
{
    public const string AllCategories = "All";

    private readonly DockViewModel _dock;
    private string _search = string.Empty;
    private string _category = AllCategories;

    public WidgetManagerViewModel(IWidgetRegistry registry, DockViewModel dock)
    {
        _dock = dock;
        Items = registry.All.Select(d => new WidgetCatalogItem(d, dock)).ToList();
        Categories = [AllCategories, .. registry.All.Select(d => d.Category.ToString()).Distinct()];
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Matches;
        ResetLayoutCommand = new AsyncRelayCommand(dock.ResetLayoutAsync);

        dock.CenterWidgets.CollectionChanged += OnDockChanged;
        dock.RightWidgets.CollectionChanged += OnDockChanged;
    }

    public IReadOnlyList<WidgetCatalogItem> Items { get; }

    public ICollectionView View { get; }

    public IReadOnlyList<string> Categories { get; }

    public AsyncRelayCommand ResetLayoutCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? string.Empty))
            {
                View.Refresh();
            }
        }
    }

    public string SelectedCategory
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, value ?? AllCategories))
            {
                View.Refresh();
            }
        }
    }

    public void Dispose()
    {
        _dock.CenterWidgets.CollectionChanged -= OnDockChanged;
        _dock.RightWidgets.CollectionChanged -= OnDockChanged;
    }

    private bool Matches(object obj)
    {
        if (obj is not WidgetCatalogItem item)
        {
            return false;
        }

        if (_category != AllCategories && item.Category != _category)
        {
            return false;
        }

        return _search.Length == 0
            || item.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
            || item.Description.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
            || item.Category.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnDockChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.Update();
        }
    }
}
