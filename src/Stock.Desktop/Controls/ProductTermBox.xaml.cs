using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Stock.Core;

namespace Stock.Desktop.Controls;

/// <summary>A free-text field with optional, explicitly selected database suggestions.</summary>
public partial class ProductTermBox : UserControl
{
    public static readonly RoutedEvent TermSelectedEvent = EventManager.RegisterRoutedEvent(nameof(TermSelected), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ProductTermBox));
    public event RoutedEventHandler TermSelected { add => AddHandler(TermSelectedEvent, value); remove => RemoveHandler(TermSelectedEvent, value); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ProductTermBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TextChanged));
    public static readonly DependencyProperty ServiceProperty = DependencyProperty.Register(nameof(Service), typeof(StockService), typeof(ProductTermBox),
        new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty TermFieldProperty = DependencyProperty.Register(nameof(TermField), typeof(string), typeof(ProductTermBox),
        new PropertyMetadata("Name", SourceChanged));
    public static readonly DependencyProperty MaxLengthProperty = DependencyProperty.Register(nameof(MaxLength), typeof(int), typeof(ProductTermBox), new PropertyMetadata(120));
    public static readonly DependencyProperty AcceptsReturnProperty = DependencyProperty.Register(nameof(AcceptsReturn), typeof(bool), typeof(ProductTermBox), new PropertyMetadata(false));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public StockService? Service { get => (StockService?)GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }
    public string TermField { get => (string)GetValue(TermFieldProperty); set => SetValue(TermFieldProperty, value); }
    public int MaxLength { get => (int)GetValue(MaxLengthProperty); set => SetValue(MaxLengthProperty, value); }
    public bool AcceptsReturn { get => (bool)GetValue(AcceptsReturnProperty); set => SetValue(AcceptsReturnProperty, value); }
    private CancellationTokenSource? pending;
    private long generation;
    private bool applying, composing;

    public ProductTermBox()
    {
        InitializeComponent();
        Unloaded += (_, _) => CloseSuggestions();
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CloseSuggestions(); };
        TextCompositionManager.AddPreviewTextInputStartHandler(Editor, (_, _) => { composing = true; CloseSuggestions(); });
        TextCompositionManager.AddPreviewTextInputHandler(Editor, (_, _) => { composing = false; Refresh(true); });
    }

    private static void TextChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var box = (ProductTermBox)source;
        if (!box.applying && !box.composing && box.IsLoaded && box.Editor.IsKeyboardFocused) box.Refresh(true);
    }
    private static void SourceChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var box = (ProductTermBox)source;
        if (box.IsLoaded && box.Editor.IsKeyboardFocused) box.Refresh(false);
    }
    private async void Refresh(bool debounce) => await RefreshSuggestionsAsync(debounce);

    internal async Task RefreshSuggestionsAsync(bool debounce = false)
    {
        pending?.Cancel();
        using var request = new CancellationTokenSource();
        pending = request;
        var current = ++generation;
        var token = request.Token;
        var text = Text ?? "";
        var service = Service;
        var field = TermField;
        Suggestions.ItemsSource = null;
        try
        {
            if (service is null || composing) return;
            if (debounce) await Task.Delay(150, token);
            var terms = await Task.Run(() => service.ProductTerms(field, text, token: token), token);
            if (current != generation || token.IsCancellationRequested || !IsLoaded || !Editor.IsKeyboardFocused) return;
            Suggestions.ItemsSource = terms;
            Suggestions.SelectedIndex = -1;
            SuggestionHint.Text = terms.Count == 0 ? "暂无匹配词条，可直接填写新值。" :
                terms.Count == 50 ? "显示前50个词条，输入关键词缩小结果；也可填写新值。" : "选择已有词条，或继续填写新值。";
            PopupBorder.Width = Math.Max(ActualWidth, 220);
            SuggestionsPopup.IsOpen = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (current != generation || !IsLoaded || !Editor.IsKeyboardFocused) return;
            Suggestions.ItemsSource = null;
            SuggestionHint.Text = "暂时无法读取词条，仍可直接填写。";
            PopupBorder.Width = Math.Max(ActualWidth, 220);
            SuggestionsPopup.IsOpen = true;
        }
        finally { if (ReferenceEquals(pending, request)) pending = null; }
    }

    internal void CloseSuggestions()
    {
        generation++;
        pending?.Cancel();
        SuggestionsPopup.IsOpen = false;
        Suggestions.SelectedIndex = -1;
    }
    private void PopupClosed(object? sender, EventArgs args) => CloseSuggestions();
    private void EditorFocused(object sender, KeyboardFocusChangedEventArgs args) => Refresh(false);
    private void EditorUnfocused(object sender, KeyboardFocusChangedEventArgs args) { composing = false; CloseSuggestions(); }
    private async void ToggleSuggestions(object sender, RoutedEventArgs args)
    {
        if (SuggestionsPopup.IsOpen) { CloseSuggestions(); return; }
        Editor.Focus();
        await RefreshSuggestionsAsync();
    }
    private async void EditorKeyDown(object sender, KeyEventArgs args)
    {
        if (composing || args.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        if (args.Key == Key.Escape) { CloseSuggestions(); args.Handled = true; return; }
        if (args.Key == Key.Tab) { CloseSuggestions(); return; }
        if (args.Key == Key.Enter && SuggestionsPopup.IsOpen && Suggestions.SelectedItem is string selected)
        { AcceptSuggestion(selected); args.Handled = true; return; }
        if (args.Key is not (Key.Down or Key.Up)) return;
        if (args.Key == Key.Up && !SuggestionsPopup.IsOpen) return;
        args.Handled = true;
        if (!SuggestionsPopup.IsOpen) await RefreshSuggestionsAsync();
        if (!SuggestionsPopup.IsOpen || Suggestions.Items.Count == 0) return;
        Suggestions.SelectedIndex = args.Key == Key.Down ? Math.Min(Suggestions.SelectedIndex + 1, Suggestions.Items.Count - 1) : Math.Max(0, Suggestions.SelectedIndex - 1);
        Suggestions.ScrollIntoView(Suggestions.SelectedItem);
    }
    private void SuggestionClicked(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(Suggestions, element) is ListBoxItem { Content: string selected })
        { AcceptSuggestion(selected); args.Handled = true; }
    }
    private void AcceptSuggestion(string selected)
    {
        applying = true;
        try
        {
            // Keep the caller's two-way binding and change this field only.
            SetCurrentValue(TextProperty, selected);
            GetBindingExpression(TextProperty)?.UpdateSource();
            Editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Editor.CaretIndex = Editor.Text.Length;
        }
        finally { applying = false; CloseSuggestions(); }
        RaiseEvent(new RoutedEventArgs(TermSelectedEvent, this));
    }
}
