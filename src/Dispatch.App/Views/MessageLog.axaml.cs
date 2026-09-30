using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Dispatch.App.Views;

/// <summary>A scrolling list of stream messages that follows new messages while scrolled to the bottom.</summary>
public partial class MessageLog : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<MessageLog, IEnumerable?>(nameof(Items));

    public MessageLog() => InitializeComponent();

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ItemsProperty)
            return;
        if (change.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= OnCollectionChanged;
        if (change.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += OnCollectionChanged;
        List.ItemsSource = change.NewValue as IEnumerable;
        UpdateEmpty();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmpty();
        var atBottom = Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 40;
        if (atBottom)
            Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void UpdateEmpty() =>
        EmptyText.IsVisible = Items is null || !Items.GetEnumerator().MoveNext();
}
