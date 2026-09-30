using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels.Editors;

/// <summary>
/// Binds a plain settings object to the UI: properties read and write the wrapped model directly and raise
/// <see cref="Changed"/> so the owning tab can mark itself dirty.
/// </summary>
public abstract class ModelEditor<T> : ObservableObject where T : class
{
    private bool _loading;

    protected ModelEditor(T model) => Model = model;

    protected T Model { get; private set; }

    public event EventHandler? Changed;

    public void Load(T model)
    {
        _loading = true;
        try
        {
            Model = DeepCopy.Of(model);
            OnLoaded();
            OnPropertyChanged(string.Empty); // refresh every binding
        }
        finally
        {
            _loading = false;
        }
    }

    public T ToModel() => DeepCopy.Of(Model);

    /// <summary>Called after a new model was loaded, to refresh derived state.</summary>
    protected virtual void OnLoaded()
    {
    }

    protected bool Set<TValue>(TValue current, TValue value, Action<T, TValue> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(current, value))
            return false;
        assign(Model, value);
        OnPropertyChanged(name);
        return true;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && !string.IsNullOrEmpty(e.PropertyName))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raises <see cref="Changed"/> for edits made outside simple properties (lists, child editors).</summary>
    protected void RaiseChanged()
    {
        if (!_loading)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    protected bool IsLoading => _loading;
}
