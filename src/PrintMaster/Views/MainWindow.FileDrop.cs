using PrintMaster.Models;
using PrintMaster.Services;
using System.Windows;
using System.Windows.Controls;

namespace PrintMaster.Views;

public partial class MainWindow
{
    private bool IsCanvasDropTarget(DependencyObject? source)
    {
        while(source!=null) { if(source==CanvasScroll) return true; source=GetParentNode(source); }
        return false;
    }
    private PointD? FileDropAnchor(DragEventArgs e)
    {
        if(!ViewModel.HasSelection) return null;
        var editor=ViewModel.IsSheetSelected ? (FrameworkElement)SheetEditor : Editor;
        var point=e.GetPosition(editor); return new(point.X/SheetEditing.DipPerMm,point.Y/SheetEditing.DipPerMm);
    }
    internal void ShowFileDropFeedback(string[] files,bool onCanvas,PointD? anchor=null)
    {
        ClearFileDropFeedback();
        if(files.Length==0 || !ViewModel.IsIdle) return;
        var insert=ViewModel.FileDropDestination(files,onCanvas)==PasteDestination.Auto;
        DropHintText.Text=insert ? "Вставить фото на текущий лист — отпустите в выделенной области"
            : onCanvas && ViewModel.IsDocumentSelected ? "Документ доступен для просмотра — добавить файлы отдельными страницами"
            : onCanvas && !ViewModel.HasSelection ? "Нет открытого листа — добавить файлы отдельными страницами"
            : onCanvas && files.Any(p=>!PrintMaster.ViewModels.PrintSessionViewModel.IsImageFile(p)) ? "Документы и смешанный набор файлов — добавить отдельными страницами"
            : "Добавить файлы в очередь отдельными страницами";
        DropHintBorder.Visibility=Visibility.Visible;
        if(!insert) return;
        var area=PrintMaster.ViewModels.PrintSessionViewModel.ImageInsertionArea(ViewModel.SafeAreaMm,anchor);
        var editor=ViewModel.IsSheetSelected ? (FrameworkElement)SheetEditor : Editor;
        var top=editor.TranslatePoint(new(area.X*SheetEditing.DipPerMm,area.Y*SheetEditing.DipPerMm),PaperStage);
        var bottom=editor.TranslatePoint(new(area.Right*SheetEditing.DipPerMm,area.Bottom*SheetEditing.DipPerMm),PaperStage);
        Canvas.SetLeft(DropRectangle,top.X); Canvas.SetTop(DropRectangle,top.Y);
        DropRectangle.Width=Math.Max(1,bottom.X-top.X); DropRectangle.Height=Math.Max(1,bottom.Y-top.Y); DropRectangle.Visibility=Visibility.Visible;
    }
    internal void ClearFileDropFeedback() { DropHintBorder.Visibility=Visibility.Collapsed; DropRectangle.Visibility=Visibility.Collapsed; }
    private void FilesDragLeave(object sender,DragEventArgs e)=>ClearFileDropFeedback();
}
