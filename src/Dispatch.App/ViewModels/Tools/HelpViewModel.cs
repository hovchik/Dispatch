using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Services;
using Dispatch.Application.Help;

namespace Dispatch.App.ViewModels.Tools;

/// <summary>What the Help window can ask the main window to do.</summary>
public interface IHelpActions
{
    /// <summary>Opens a request of the example collection in a tab, creating the collection first if needed.</summary>
    Task OpenSampleAsync(string requestName);

    /// <summary>Adds the example collection to the sidebar (or reveals it when it already exists).</summary>
    Task LoadSamplesAsync();

    void ShowWelcome();
}

/// <summary>The built-in user guide: searchable topics with steps, copyable examples and links to try them.</summary>
public sealed partial class HelpViewModel : ObservableObject, ITool
{
    private readonly IHelpActions _actions;
    private readonly IClipboardService _clipboard;

    public HelpViewModel(IHelpActions actions, IClipboardService clipboard, string? topicId)
    {
        _actions = actions;
        _clipboard = clipboard;
        Refresh();
        Show(topicId ?? HelpCatalog.GettingStarted);
    }

    public string Title => "Help";
    public double Width => 1000;
    public double Height => 700;

    [ObservableProperty] private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected), nameof(RelatedTopics), nameof(HasRelated), nameof(HasTryIt))]
    private HelpTopicItem? _selectedItem;

    [ObservableProperty] private string? _feedback;

    /// <summary>Topics in catalog order; the first topic of each category carries the category heading.</summary>
    public ObservableCollection<HelpTopicItem> Topics { get; } = [];
    public bool HasResults => Topics.Count > 0;
    public HelpTopic? Selected => SelectedItem?.Topic;

    public IReadOnlyList<HelpTopic> RelatedTopics =>
        Selected?.Related?.Select(HelpCatalog.Find).OfType<HelpTopic>().ToList() ?? [];

    public bool HasRelated => RelatedTopics.Count > 0;
    public bool HasTryIt => Selected?.TryIt is not null;

    /// <summary>Shows a topic (also used when the window is already open and another help link is followed).</summary>
    public void Show(string? topicId)
    {
        if (HelpCatalog.Find(topicId) is null)
            return;
        if (Topics.All(t => t.Topic.Id != topicId))
            Query = "";
        SelectedItem = Topics.FirstOrDefault(t => t.Topic.Id == topicId);
    }

    partial void OnQueryChanged(string value)
    {
        var current = Selected?.Id;
        Refresh();
        SelectedItem = Topics.FirstOrDefault(t => t.Topic.Id == current) ?? Topics.FirstOrDefault();
    }

    private void Refresh()
    {
        Topics.Clear();
        string? category = null;
        foreach (var topic in HelpCatalog.Search(Query).OrderBy(t => CategoryOrder(t.Category)))
        {
            Topics.Add(new HelpTopicItem(topic, topic.Category == category ? null : topic.Category));
            category = topic.Category;
        }
        OnPropertyChanged(nameof(HasResults));
    }

    private static int CategoryOrder(string category) =>
        HelpCatalog.All.Select(t => t.Category).Distinct().ToList().IndexOf(category);

    [RelayCommand]
    private void Open(HelpTopic? topic)
    {
        if (topic is not null)
            Show(topic.Id);
    }

    [RelayCommand]
    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        await _clipboard.SetTextAsync(text);
        await FlashAsync("Copied to the clipboard");
    }

    [RelayCommand]
    private async Task TryItAsync()
    {
        if (Selected?.TryIt is { } name)
        {
            await _actions.OpenSampleAsync(name);
            await FlashAsync($"Opened \"{name}\" in a new tab");
        }
    }

    [RelayCommand]
    private async Task LoadSamplesAsync()
    {
        await _actions.LoadSamplesAsync();
        await FlashAsync($"\"{SampleCollection.Name}\" is in the Collections sidebar");
    }

    [RelayCommand]
    private void ShowWelcome() => _actions.ShowWelcome();

    private async Task FlashAsync(string message)
    {
        Feedback = message;
        await Task.Delay(2500);
        if (Feedback == message)
            Feedback = null;
    }
}

/// <summary>A row of the topic list; <see cref="Heading"/> is set on the first topic of a category.</summary>
public sealed record HelpTopicItem(HelpTopic Topic, string? Heading)
{
    public bool HasHeading => Heading is not null;
}
