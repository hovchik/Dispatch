using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;

namespace Dispatch.App.ViewModels;

public sealed partial class EnvironmentsViewModel(
    IEnvironmentRepository repository,
    ISettingsRepository settings) : ObservableObject
{
    /// <summary>Sentinel shown in the environment picker for "no variables".</summary>
    public static readonly EnvironmentItemViewModel None = EnvironmentItemViewModel.CreateNone();

    /// <summary>Environments that can be edited in the sidebar.</summary>
    public ObservableCollection<EnvironmentItemViewModel> Items { get; } = [];

    /// <summary>Items for the toolbar picker: "No Environment" followed by every environment.</summary>
    public ObservableCollection<EnvironmentItemViewModel> Choices { get; } = [None];

    [ObservableProperty] private EnvironmentItemViewModel? _selected;
    [ObservableProperty] private EnvironmentItemViewModel _active = None;

    public ApiEnvironment? ActiveModel => Active.IsNone ? null : Active.ToModel();

    public async Task LoadAsync()
    {
        var envs = await repository.GetAllAsync();
        Items.Clear();
        foreach (var e in envs)
            Items.Add(new EnvironmentItemViewModel(this, e));
        RebuildChoices();

        var activeId = await settings.GetAsync(SettingKeys.ActiveEnvironmentId);
        Active = Items.FirstOrDefault(i => i.Id.ToString() == activeId) ?? None;
    }

    partial void OnActiveChanged(EnvironmentItemViewModel value)
    {
        // Picker can transiently push null while its items are rebuilt.
        if (value is null)
        {
            Active = None;
            // The picker ignores the correction while it is still updating; re-announce afterwards.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Active)));
            return;
        }
        _ = settings.SetAsync(SettingKeys.ActiveEnvironmentId, value.IsNone ? null : value.Id.ToString());
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var model = new ApiEnvironment { Name = $"Environment {Items.Count + 1}" };
        await repository.SaveAsync(model);
        var item = new EnvironmentItemViewModel(this, model);
        Items.Add(item);
        RebuildChoices();
        Selected = item;
    }

    [ObservableProperty] private string? _error;

    /// <summary>Writes variables set by extraction rules / scripts into the active environment and saves it.</summary>
    public async Task ApplyUpdatesAsync(IReadOnlyDictionary<string, string> updates)
    {
        if (updates.Count == 0 || Active.IsNone)
            return;
        var model = Active.ToModel();
        var changed = false;
        foreach (var (name, value) in updates)
            changed |= model.SetVariable(name, value);
        if (!changed)
            return;
        Active.Variables.Load(model.Variables);
        await Guard(async () =>
        {
            await repository.SaveAsync(model);
            Active.IsDirty = false;
        });
    }

    internal Task SaveAsync(EnvironmentItemViewModel item) => Guard(async () =>
    {
        await repository.SaveAsync(item.ToModel());
        item.IsDirty = false;
    });

    internal Task DeleteAsync(EnvironmentItemViewModel item) => Guard(async () =>
    {
        await repository.DeleteAsync(item.Id);
        Items.Remove(item);
        if (Active == item)
            Active = None;
        if (Selected == item)
            Selected = null;
        RebuildChoices();
    });

    private async Task Guard(Func<Task> action)
    {
        try
        {
            Error = null;
            await action();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>
    /// Syncs Choices with Items in place: clearing the list would make the picker drop its selection
    /// (pushing null back into Active mid-update, where the correction gets lost).
    /// </summary>
    private void RebuildChoices()
    {
        var wanted = Items.Prepend(None).ToList();
        for (var i = Choices.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Choices[i]))
                Choices.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var index = Choices.IndexOf(wanted[i]);
            if (index < 0)
                Choices.Insert(i, wanted[i]);
            else if (index != i)
                Choices.Move(index, i);
        }
        if (!Choices.Contains(Active))
            Active = None;
    }
}

public sealed partial class EnvironmentItemViewModel : ObservableObject
{
    private readonly EnvironmentsViewModel? _owner;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isDirty;

    private EnvironmentItemViewModel(string name)
    {
        _name = name;
        IsNone = true;
        Variables = new KeyValueListViewModel();
    }

    public EnvironmentItemViewModel(EnvironmentsViewModel owner, ApiEnvironment model)
    {
        _owner = owner;
        Id = model.Id;
        _name = model.Name;
        Variables = new KeyValueListViewModel("Variable", "Value", supportsSecret: true);
        Variables.Load(model.Variables);
        Variables.Changed += (_, _) => IsDirty = true;
    }

    internal static EnvironmentItemViewModel CreateNone() => new("No Environment");

    public Guid Id { get; }
    public bool IsNone { get; }
    public KeyValueListViewModel Variables { get; }

    partial void OnNameChanged(string value) => IsDirty = true;

    public ApiEnvironment ToModel() => new() { Id = Id, Name = Name.Trim(), Variables = Variables.ToItems() };

    [RelayCommand]
    private Task SaveAsync() => _owner?.SaveAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task DeleteAsync() => _owner?.DeleteAsync(this) ?? Task.CompletedTask;
}
