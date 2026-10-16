using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintMaster.Controls;

public partial class InlineTextEditor : UserControl
{
    private PrintSessionViewModel? _model;
    private bool _syncing;
    private Models.TextStyle? _appliedStyle;
    public InlineTextEditor()
    {
        InitializeComponent(); DataContextChanged+=ContextChanged;
        DataObject.AddPastingHandler(Input,PlainTextPaste);
    }
    private void ContextChanged(object sender,DependencyPropertyChangedEventArgs e)
    {
        if(_model!=null) _model.PropertyChanged-=ModelChanged;
        _model=e.NewValue as PrintSessionViewModel;
        if(_model!=null) _model.PropertyChanged+=ModelChanged;
        Refresh(true);
    }
    private void ModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(PrintSessionViewModel.IsEditingText)) { Refresh(true); if(_model?.IsEditingText==true) _=Dispatcher.BeginInvoke(()=>{ Input.BringIntoView(new Rect(0,0,Math.Max(1,ActualWidth),Math.Min(80,Math.Max(1,ActualHeight)))); Input.Focus(); Input.CaretPosition=Input.Document.ContentEnd; }); }
        else if(e.PropertyName is nameof(PrintSessionViewModel.EditingText) or nameof(PrintSessionViewModel.TextFontFamily) or nameof(PrintSessionViewModel.TextFontSizePt)
            or nameof(PrintSessionViewModel.TextBold) or nameof(PrintSessionViewModel.TextItalic) or nameof(PrintSessionViewModel.TextColor)
            or nameof(PrintSessionViewModel.TextAlignment) or nameof(PrintSessionViewModel.TextLineSpacing)) Refresh(false);
    }
    private static string Normalize(string text)=>text.Replace("\r\n","\n").Replace('\r','\n');
    private string ReadText()
    {
        var value=new TextRange(Input.Document.ContentStart,Input.Document.ContentEnd).Text;
        if(value.EndsWith("\r\n",StringComparison.Ordinal)) value=value[..^2];
        return Normalize(value);
    }
    private void Refresh(bool reset)
    {
        if(_model?.IsEditingText!=true || _syncing || _model.SelectedText is not {} selected) return;
        _syncing=true;
        try
        {
            if(reset || ReadText()!=Normalize(_model.EditingText))
            {
                Input.Document=new(new Paragraph(new Run(Normalize(_model.EditingText))) { Margin=new(0) }) { PagePadding=new(0),ColumnWidth=double.PositiveInfinity };
                _appliedStyle=null;
            }
            if(_appliedStyle!=selected.Style)
            {
                var style=selected.Style; var metrics=TextLayoutService.Layout(selected);
                Input.Document.FontFamily=new(metrics.FontFamily); Input.Document.FontSize=style.FontSizePt*96/72;
                Input.Document.FontWeight=style.Bold ? FontWeights.Bold : FontWeights.Normal; Input.Document.FontStyle=style.Italic ? FontStyles.Italic : FontStyles.Normal;
                Input.Document.Foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Color));
                Input.Document.TextAlignment=style.Alignment switch { Models.TextAlignment.Center=>System.Windows.TextAlignment.Center,Models.TextAlignment.Right=>System.Windows.TextAlignment.Right,_=>System.Windows.TextAlignment.Left };
                Input.Document.LineHeight=metrics.LineHeightMm*SheetEditing.DipPerMm;
                Input.Document.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
                foreach(var paragraph in Input.Document.Blocks.OfType<Paragraph>()) paragraph.Margin=new(0);
                _appliedStyle=style;
            }
        }
        finally { _syncing=false; }
    }
    private void InputChanged(object sender,TextChangedEventArgs e)
    {
        if(!_syncing && _model?.IsEditingText==true) _model.EditingText=ReadText();
    }
    private void InputKeyDown(object sender,KeyEventArgs e)
    {
        if(HandleEditingKey(e.Key,Keyboard.Modifiers)) e.Handled=true;
    }
    internal bool HandleEditingKey(Key key,ModifierKeys modifiers)
    {
        if(_model?.IsEditingText!=true) return false;
        if(key==Key.Escape) { _model.CancelTextEditCommand.Execute(null); return true; }
        if(modifiers.HasFlag(ModifierKeys.Control))
        {
            if(key==Key.Enter) { _model.FinishTextEditCommand.Execute(null); return true; }
            if(key==Key.B) { _model.TextBold=!_model.TextBold; return true; }
            if(key==Key.I) { _model.TextItalic=!_model.TextItalic; return true; }
            if(key==Key.U) return true;
        }
        return false;
    }
    private void PlainTextPaste(object sender,DataObjectPastingEventArgs e)
    {
        if(!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) { e.CancelCommand(); return; }
        var text=e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "";
        if(!FitsPlainTextPaste(ReadText(),Input.Selection.Text,text)) { e.CancelCommand(); return; }
        e.DataObject=new DataObject(DataFormats.UnicodeText,text); e.FormatToApply=DataFormats.UnicodeText;
    }
    internal static bool FitsPlainTextPaste(string currentText,string selectedText,string pastedText)
    {
        var current=Normalize(currentText).Length;
        // RichTextBox SelectAll also includes its final paragraph terminator,
        // which ReadText deliberately omits from the stored source.
        var selected=Math.Min(current,Normalize(selectedText).Length);
        return (long)current-selected+Normalize(pastedText).Length<=Models.TextLimits.MaxCharacters;
    }
    internal RichTextBox TextInputBox=>Input;
}
