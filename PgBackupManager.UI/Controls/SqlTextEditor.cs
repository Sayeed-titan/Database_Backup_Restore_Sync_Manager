using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI.Controls;

public interface ISqlCompletionSource
{
    // qualifier = the identifier before a '.', or null for a plain word.
    IEnumerable<(string Text, string Kind)> GetCompletions(string? qualifier);
}

// AvalonEdit with a two-way bindable Code property, the universal SQL
// highlighting, theme-aware colours and keyword/object/column autocomplete.
public class SqlTextEditor : TextEditor
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(SqlTextEditor),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SqlTextEditor)d).OnCodeChanged((string?)e.NewValue)));

    public static readonly DependencyProperty CompletionSourceProperty = DependencyProperty.Register(
        nameof(CompletionSource), typeof(ISqlCompletionSource), typeof(SqlTextEditor));

    public string? Code { get => (string?)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public ISqlCompletionSource? CompletionSource { get => (ISqlCompletionSource?)GetValue(CompletionSourceProperty); set => SetValue(CompletionSourceProperty, value); }

    private bool _syncing;
    private CompletionWindow? _completion;

    public SqlTextEditor()
    {
        SyntaxHighlighting = SqlHighlighting.Definition;
        FontFamily = new FontFamily("Cascadia Mono, Consolas");
        FontSize = new PgBackupManager.Core.Services.SettingsStore().Load().EditorFontSize;
        ShowLineNumbers = true;
        WordWrap = false;
        Padding = new Thickness(6, 4, 4, 4);
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 4;
        Options.HighlightCurrentLine = true;
        Options.EnableRectangularSelection = true;
        Options.AllowScrollBelowDocument = true;
        SetResourceReference(BackgroundProperty, "CardBg");
        SetResourceReference(ForegroundProperty, "Body");
        SetResourceReference(LineNumbersForegroundProperty, "Muted");
        ApplyThemeBrushes();
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(() => { ApplyThemeBrushes(); TextArea.TextView.Redraw(); });

        TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            Code = Text;
            _syncing = false;
        };
        TextArea.TextEntered += OnTextEntered;
        TextArea.TextEntering += OnTextEntering;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void ApplyThemeBrushes()
    {
        var sel = TryFindResource("SelectionBg") as Brush ?? Brushes.LightBlue;
        TextArea.SelectionBrush = sel;
        TextArea.SelectionBorder = null;
        TextArea.SelectionForeground = null;
        TextArea.TextView.CurrentLineBackground = TryFindResource("CardSubtle") as Brush;
        TextArea.TextView.CurrentLineBorder = new Pen(TryFindResource("BorderLight") as Brush ?? Brushes.Transparent, 1);
        TextArea.Caret.CaretBrush = TryFindResource("Teal") as Brush;
    }

    private void OnCodeChanged(string? value)
    {
        if (_syncing) return;
        _syncing = true;
        if (Text != (value ?? "")) Text = value ?? "";
        _syncing = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowCompletion(QualifierBeforeCaret(), CurrentWordStart());
            e.Handled = true;
        }
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (CompletionSource == null || _completion != null || e.Text.Length != 1) return;
        var ch = e.Text[0];
        if (ch == '.') { ShowCompletion(QualifierBeforeCaret(), CaretOffset); return; }
        if (char.IsLetter(ch) || ch == '_')
        {
            var start = CurrentWordStart();
            if (CaretOffset - start == 2 && QualifierBeforeOffset(start) is var q) ShowCompletion(q, start);
        }
    }

    private void OnTextEntering(object sender, TextCompositionEventArgs e)
    {
        if (_completion != null && e.Text.Length > 0 && !char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_')
            _completion.CompletionList.RequestInsertion(e);
    }

    private int CurrentWordStart()
    {
        int i = CaretOffset;
        while (i > 0 && IsIdentChar(Document.GetCharAt(i - 1))) i--;
        return i;
    }

    private string? QualifierBeforeCaret() => QualifierBeforeOffset(CurrentWordStart());

    // "schema.tab|" -> "schema" ; "t.|" -> "t"
    private string? QualifierBeforeOffset(int wordStart)
    {
        if (wordStart < 2 || Document.GetCharAt(wordStart - 1) != '.') return null;
        int end = wordStart - 1, i = end;
        while (i > 0 && IsIdentChar(Document.GetCharAt(i - 1))) i--;
        return i < end ? Document.GetText(i, end - i).Trim('"', '[', ']', '`') : null;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '"';

    private void ShowCompletion(string? qualifier, int wordStart)
    {
        if (CompletionSource == null) return;
        var items = CompletionSource.GetCompletions(qualifier).Take(800).ToList();
        if (items.Count == 0) return;
        _completion = new CompletionWindow(TextArea) { StartOffset = wordStart, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 240 };
        _completion.SetResourceReference(BackgroundProperty, "CardBg");
        _completion.SetResourceReference(ForegroundProperty, "Body");
        _completion.CompletionList.SetResourceReference(BackgroundProperty, "CardBg");
        var list = _completion.CompletionList.CompletionData;
        foreach (var (text, kind) in items) list.Add(new SqlCompletion(text, kind));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
        var typed = Document.GetText(wordStart, CaretOffset - wordStart);
        if (typed.Length > 0) _completion.CompletionList.SelectItem(typed);
    }

    private sealed class SqlCompletion : ICompletionData
    {
        public SqlCompletion(string text, string kind) { Text = text; Kind = kind; }
        public string Kind { get; }
        public ImageSource? Image => null;
        public string Text { get; }
        public object Content => $"{Text}    ·  {Kind}";
        public object Description => Kind;
        public double Priority => Kind switch { "column" => 3, "table" or "view" => 2, "keyword" => 0, _ => 1 };
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }
}
