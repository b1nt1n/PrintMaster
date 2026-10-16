using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace PrintMaster.Views;

public sealed class MarkdownSourceSaveEventArgs(string source,string title) : EventArgs
{
    public string Source {get;}=source;
    public string Title {get;}=title;
}

public partial class MarkdownEditorDialog : Window
{
    internal MarkdownEditorViewModel ViewModel {get;}
    public event EventHandler<MarkdownSourceSaveEventArgs>? SaveSourceRequested;
    public MarkdownEditorDialog(MarkdownEditorViewModel model)
    {
        InitializeComponent();DataContext=ViewModel=model;WindowWorkArea.Attach(this);
        SizeChanged+=(_,_)=>AdaptPanels();Loaded+=(_,_)=>{AdaptPanels();SourceEditor.Focus();};Closed+=(_,_)=>ViewModel.Dispose();
    }
    private void ApplyClicked(object sender,RoutedEventArgs e)
    {if(DialogInputValidation.CommitAndCheck(this) && ViewModel.CanApply) DialogResult=true;}
    private void SaveSourceClicked(object sender,RoutedEventArgs e)
        =>SaveSourceRequested?.Invoke(this,new(ViewModel.Source,ViewModel.Title));
    private void GoToErrorClicked(object sender,RoutedEventArgs e)
    {
        if(ViewModel.ErrorLine is not {} line || line<=0) return;
        SourceEditor.Focus();var index=Math.Clamp(line-1,0,Math.Max(0,SourceEditor.LineCount-1));
        var character=SourceEditor.GetCharacterIndexFromLineIndex(index);if(character>=0) SourceEditor.Select(character,0);SourceEditor.ScrollToLine(index);
    }
    private void AdaptPanels()
    {
        var narrow=ActualWidth<850;
        EditorBody.ColumnDefinitions[0].Width=new(1,GridUnitType.Star);
        EditorBody.ColumnDefinitions[1].Width=narrow ? new(0) : new(8);
        EditorBody.ColumnDefinitions[2].Width=narrow ? new(0) : new(1,GridUnitType.Star);
        EditorBody.RowDefinitions[0].Height=new(1,GridUnitType.Star);
        EditorBody.RowDefinitions[1].Height=narrow ? new(8) : new(0);
        EditorBody.RowDefinitions[2].Height=narrow ? new(1,GridUnitType.Star) : new(0);
        Grid.SetColumn(PreviewPanel,narrow ? 0 : 2);Grid.SetRow(PreviewPanel,narrow ? 2 : 0);
        Grid.SetColumn(EditorSplitter,narrow ? 0 : 1);Grid.SetRow(EditorSplitter,narrow ? 1 : 0);
        EditorSplitter.ResizeDirection=narrow ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        EditorSplitter.Width=narrow ? double.NaN : 6;EditorSplitter.Height=narrow ? 6 : double.NaN;
        EditorSplitter.HorizontalAlignment=narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        EditorSplitter.VerticalAlignment=narrow ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        FormattingScroll.MaxHeight=Math.Clamp(ActualHeight*.22,80,180);
    }
}
