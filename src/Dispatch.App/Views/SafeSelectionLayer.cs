using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Dispatch.App.Views;

/// <summary>
/// Draws the text selection like AvaloniaEdit's own <c>SelectionLayer</c>, but skips the frame when the
/// <see cref="TextView"/> has no valid visual lines instead of throwing <see cref="VisualLinesInvalidException"/>.
/// <para>
/// The stock layer assumes layout always runs before render. That does not hold for a hidden editor: its
/// TextView is not measured, so <c>Redraw()</c> (a grammar change, a document swap) leaves the visual lines
/// invalid, while a scroll-offset change or a whole-window repaint still marks the layer dirty. The compositor
/// then renders it, the exception escapes from the render loop, the dirty set is never cleared, and every
/// following frame fails the same way: the window stops updating. Skipping is safe because the layer is
/// invalidated again on <see cref="TextView.VisualLinesChanged"/> once the lines exist.
/// </para>
/// </summary>
internal sealed class SafeSelectionLayer : Control
{
    private readonly TextArea _textArea;
    private readonly TextView _textView;

    private SafeSelectionLayer(TextArea textArea)
    {
        _textArea = textArea;
        _textView = textArea.TextView;
        Focusable = false;
        IsHitTestVisible = false;
        _textView.VisualLinesChanged += (_, _) => InvalidateVisual();
        _textView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Replaces the editor's built-in selection layer with this one.</summary>
    public static void Install(TextArea textArea)
    {
        var textView = textArea.TextView;
        // InsertLayer(Replace) records the layer position and slots the layer into Layers below the text,
        // but AvaloniaEdit 11.3 appends the replacement to the visual children instead of reusing the old
        // index, which would paint the selection over the glyphs. Rebuilding the collection re-adds every
        // layer in Layers order, and that path keeps the visual children in step.
        textView.InsertLayer(new SafeSelectionLayer(textArea), KnownLayer.Selection, LayerInsertionPosition.Replace);
        var ordered = textView.Layers.ToList();
        textView.Layers.Clear();
        foreach (var layer in ordered)
            textView.Layers.Add(layer);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_textView.VisualLinesValid)
            return;

        var selection = _textArea.Selection;
        if (selection.IsEmpty)
            return;

        var border = _textArea.SelectionBorder;
        var builder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            BorderThickness = border?.Thickness ?? 0,
            ExtendToFullWidthAtLineEnd = selection.EnableVirtualSpace,
            CornerRadius = _textArea.SelectionCornerRadius,
        };
        foreach (var segment in selection.Segments)
            builder.AddSegment(_textView, segment);

        var geometry = builder.CreateGeometry();
        if (geometry is not null)
            context.DrawGeometry(_textArea.SelectionBrush, border, geometry);
    }
}
