using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using Dispatch.Application.Formatting;
using TextMateSharp.Grammars;

namespace Dispatch.App.Views;

/// <summary>
/// A monospace text editor that syntax-highlights its text according to <see cref="Format"/>
/// (derived from the Content-Type), following the app's light/dark theme.
/// </summary>
public sealed class CodeEditor : UserControl
{
    // Tokenizing very large bodies is slow; past this size the text is shown uncolored.
    private const int MaxHighlightedLength = 1_000_000;

    private static readonly RegistryOptions Registry = new(ThemeName.DarkPlus);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<BodyFormat> FormatProperty =
        AvaloniaProperty.Register<CodeEditor, BodyFormat>(nameof(Format), BodyFormat.Text);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(IsReadOnly));

    private readonly TextEditor _editor;
    private readonly TextMate.Installation _textMate;
    private bool _syncing;

    public CodeEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 4),
        };
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 2;
        _editor.Classes.Add("code");
        _editor.TextChanged += OnEditorTextChanged;

        _textMate = _editor.InstallTextMate(Registry);
        ActualThemeVariantChanged += (_, _) => UpdateTheme();
        Content = _editor;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public BodyFormat Format
    {
        get => GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            var text = change.GetNewValue<string?>() ?? string.Empty;
            if (!_syncing && _editor.Document.Text != text)
            {
                _syncing = true;
                _editor.Document.Text = text;
                _editor.ScrollToHome();
                _syncing = false;
            }
            UpdateGrammar();
        }
        else if (change.Property == FormatProperty)
        {
            UpdateGrammar();
        }
        else if (change.Property == IsReadOnlyProperty)
        {
            _editor.IsReadOnly = change.GetNewValue<bool>();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTheme();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing)
            return;
        _syncing = true;
        SetCurrentValue(TextProperty, _editor.Document.Text);
        _syncing = false;
    }

    private void UpdateTheme() =>
        _textMate.SetTheme(Registry.LoadTheme(ActualThemeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus));

    private string? _scope;

    private void UpdateGrammar()
    {
        var scope = _editor.Document.TextLength > MaxHighlightedLength ? null : ScopeFor(Format);
        if (scope == _scope)
            return;
        _scope = scope;
        _textMate.SetGrammar(scope);
    }

    private static string? ScopeFor(BodyFormat format) => format switch
    {
        BodyFormat.Json => Registry.GetScopeByLanguageId("json"),
        BodyFormat.Xml => Registry.GetScopeByLanguageId("xml"),
        BodyFormat.Html => Registry.GetScopeByLanguageId("html"),
        BodyFormat.JavaScript => Registry.GetScopeByLanguageId("javascript"),
        _ => null
    };
}
