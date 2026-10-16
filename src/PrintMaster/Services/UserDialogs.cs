using Microsoft.Win32;
using PrintMaster.Models;
using System.Windows;

namespace PrintMaster.Services;

public interface IUserDialogs
{
    string[] PickFiles();
    string? PickPdfOutput();
    string? PickLibreOffice();
    bool ConfirmCropping(int count);
    void Error(string message);
    void ShowOperationError(OperationErrorInfo info)=>Error(info.Summary+"\n"+info.NextAction);
    string? PickProjectToOpen()=>null;
    string? PickProjectToSave(string suggestedName)=>null;
    string? PickMissingSource(MissingProjectSource source)=>null;
    bool ConfirmRecovery(string path)=>false;
    UnsavedProjectChoice ConfirmProjectReplacement()=>UnsavedProjectChoice.Discard;
    string? PickImageOutput(string suggestedName)=>null;
    SheetPaper? PickNewSheet(PrintSettings settings)=>null;
    string[] PickSheetImages()=>[];
    bool ConfirmSheetClipping(int count)=>false;
    bool ReviewPrint(ViewModels.PrintReviewViewModel model)=>false;
    bool EditApplicationSettings(ViewModels.ApplicationSettingsViewModel model)=>false;
    NotebookProfile? EditNotebookProfile(NotebookProfile profile,bool copy)=>null;
    bool ShowPaperGuide(ViewModels.PaperGuideViewModel model)=>false;
    bool ConfirmNotebookOverflow(int pages,int characters)=>false;
    bool ReviewExport(ViewModels.ExportViewModel model)=>false;
    PhotoGridOptions? PickPhotoGrid(PhotoGridRequest request)=>null;
    bool EditMarkdown(ViewModels.MarkdownEditorViewModel model)=>false;
    bool EditPageOptions(ViewModels.PageOptionsEditorViewModel model)=>false;
    string? PickExportOutput(ExportFormat format,string suggested)=>null;
    bool ConfirmExportOverwrite(IReadOnlyList<string> files)=>false;
    MissingRecentProjectChoice ResolveMissingRecentProject(string path)=>MissingRecentProjectChoice.Cancel;
}
public enum UnsavedProjectChoice { Save,Discard,Cancel }
public enum MissingRecentProjectChoice { Locate,Remove,Cancel }

public sealed class UserDialogs : IUserDialogs
{
    public bool EditPageOptions(ViewModels.PageOptionsEditorViewModel model)=>new Views.PageOptionsDialog(model){Owner=Application.Current.MainWindow}.ShowDialog()==true;
    public bool EditMarkdown(ViewModels.MarkdownEditorViewModel model)
    {
        var dialog=new Views.MarkdownEditorDialog(model){Owner=Application.Current.MainWindow};
        dialog.SaveSourceRequested+=async(_,e)=>
        {
            var picker=new SaveFileDialog{Title="Сохранить исходник Markdown",Filter="Markdown|*.md",DefaultExt=".md",AddExtension=true,FileName=System.IO.Path.ChangeExtension(e.Title,".md")};
            if(picker.ShowDialog(dialog)!=true)return;
            try
            {
                if(!string.Equals(System.IO.Path.GetExtension(picker.FileName),".md",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Для исходника выберите расширение .md.");
                await System.IO.File.WriteAllTextAsync(picker.FileName,e.Source,new System.Text.UTF8Encoding(false));
            }
            catch(Exception ex){MessageBox.Show(dialog,ex.Message,"Не удалось сохранить Markdown",MessageBoxButton.OK,MessageBoxImage.Error);}
        };
        return dialog.ShowDialog()==true;
    }
    public PhotoGridOptions? PickPhotoGrid(PhotoGridRequest request)
    {
        using var model=new ViewModels.PhotoGridViewModel(request);
        var dialog=new Views.PhotoGridDialog(model){Owner=Application.Current.MainWindow};
        return dialog.ShowDialog()==true ? model.Options : null;
    }
    public void ShowOperationError(OperationErrorInfo info)=>new Views.OperationErrorDialog(info)
        { Owner=Application.Current.Windows.OfType<Window>().LastOrDefault(w=>w.IsActive) ?? Application.Current.MainWindow }.ShowDialog();
    public MissingRecentProjectChoice ResolveMissingRecentProject(string path)
    {
        var dialog=new Views.MissingRecentProjectDialog(path) { Owner=Application.Current.MainWindow };
        dialog.ShowDialog(); return dialog.Result;
    }
    public bool ReviewExport(ViewModels.ExportViewModel model)=>new Views.ExportDialog(model) { Owner=Application.Current.MainWindow }.ShowDialog()==true;
    public string? PickExportOutput(ExportFormat format,string suggested)
    {
        var dialog=new SaveFileDialog { Title="Сохранить экспорт листов",Filter=format==ExportFormat.Pdf ? "PDF|*.pdf" : "PNG|*.png",FileName=System.IO.Path.GetFileName(suggested),
            InitialDirectory=System.IO.Path.GetDirectoryName(suggested) ?? "",AddExtension=true,DefaultExt=format==ExportFormat.Pdf ? ".pdf" : ".png",OverwritePrompt=false };
        return dialog.ShowDialog(Application.Current.Windows.OfType<Window>().LastOrDefault(w=>w.IsActive) ?? Application.Current.MainWindow)==true ? dialog.FileName : null;
    }
    public bool ConfirmExportOverwrite(IReadOnlyList<string> files)=>MessageBox.Show(Application.Current.Windows.OfType<Window>().LastOrDefault(w=>w.IsActive) ?? Application.Current.MainWindow,
        "Заменить существующие файлы?\n\n"+string.Join("\n",files.Take(8))+(files.Count>8 ? $"\n… и еще {files.Count-8}" : ""),
        "Замена файлов экспорта",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
    public bool ShowPaperGuide(ViewModels.PaperGuideViewModel model)=>new Views.PaperGuideDialog(model) { Owner=Application.Current.MainWindow }.ShowDialog()==true;
    public bool ConfirmNotebookOverflow(int pages,int characters)=>MessageBox.Show(Application.Current.MainWindow,
        $"На {pages} листах не помещается {characters} символов. Будет напечатана только поместившаяся часть текста. Полный исходный текст останется в проекте.\n\nПечатать поместившуюся часть?",
        "Текст не поместился",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
    public NotebookProfile? EditNotebookProfile(NotebookProfile profile,bool copy)
    {
        var dialog=new Views.NotebookProfileDialog(profile,copy) { Owner=Application.Current.MainWindow };
        return dialog.ShowDialog()==true ? dialog.Result : null;
    }
    public bool ReviewPrint(ViewModels.PrintReviewViewModel model)=>new Views.PrintReviewDialog(model) { Owner=Application.Current.MainWindow }.ShowDialog()==true;
    public bool EditApplicationSettings(ViewModels.ApplicationSettingsViewModel model)=>new Views.ApplicationSettingsDialog(model) { Owner=Application.Current.MainWindow }.ShowDialog()==true;
    public SheetPaper? PickNewSheet(PrintSettings settings)
    {
        var dialog=new Views.NewSheetDialog(settings) { Owner=Application.Current.MainWindow };
        return dialog.ShowDialog()==true ? dialog.Paper : null;
    }
    public string[] PickSheetImages()
    {
        var dialog=new OpenFileDialog { Title="Вставить фотографии на лист",Multiselect=true,Filter="Изображения|*.png;*.jpg;*.jpeg;*.bmp" };
        return dialog.ShowDialog(Application.Current.MainWindow)==true ? dialog.FileNames : [];
    }
    public bool ConfirmSheetClipping(int count)=>MessageBox.Show(Application.Current.MainWindow,
        $"На {count} выбранных листах объекты выходят за безопасную печатную область. Выступающие части будут обрезаны.\n\nПроверьте печатный вид или верните объекты внутрь пунктирной рамки. Продолжить печать?",
        "Объекты за полями",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
    public string? PickProjectToOpen()
    {
        var dialog=new OpenFileDialog { Title="Открыть проект PrintMaster",Filter="Проект PrintMaster|*.printmaster",CheckFileExists=true };
        return dialog.ShowDialog(Application.Current.MainWindow)==true ? dialog.FileName : null;
    }
    public string? PickProjectToSave(string suggestedName)
    {
        var dialog=new SaveFileDialog { Title="Сохранить проект PrintMaster",Filter="Проект PrintMaster|*.printmaster",FileName=suggestedName,AddExtension=true,DefaultExt=".printmaster" };
        return dialog.ShowDialog(Application.Current.MainWindow)==true ? dialog.FileName : null;
    }
    public string? PickMissingSource(MissingProjectSource source)
    {
        var dialog=new OpenFileDialog { Title="Переподключить: "+source.DisplayName,
            Filter=source.Kind==SourceKind.Image ? "Изображения|*.png;*.jpg;*.jpeg;*.bmp" : "Рабочая PDF-копия документа|*.pdf" };
        return dialog.ShowDialog(Application.Current.MainWindow)==true ? dialog.FileName : null;
    }
    public bool ConfirmRecovery(string path)=>MessageBox.Show(Application.Current.MainWindow,
        "Найдена сохраненная рабочая сессия. Восстановить очередь, настройки и правки?", "Восстановление PrintMaster",
        MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.Yes)==MessageBoxResult.Yes;
    public UnsavedProjectChoice ConfirmProjectReplacement()
    {
        var choice=MessageBox.Show(Application.Current.MainWindow,"Текущий проект изменен. Сохранить его перед открытием другого?", "Несохраненные изменения",MessageBoxButton.YesNoCancel,MessageBoxImage.Question,MessageBoxResult.Cancel);
        return choice==MessageBoxResult.Yes ? UnsavedProjectChoice.Save : choice==MessageBoxResult.No ? UnsavedProjectChoice.Discard : UnsavedProjectChoice.Cancel;
    }
    public string? PickImageOutput(string suggestedName)
    {
        var dialog=new SaveFileDialog { Title="Сохранить копию изображения",Filter="PNG|*.png|JPEG|*.jpg|BMP|*.bmp",FileName=suggestedName,AddExtension=true,DefaultExt=".png" };
        return dialog.ShowDialog(Application.Current.MainWindow)==true ? dialog.FileName : null;
    }
    public string[] PickFiles()
    {
        var dialog=new OpenFileDialog { Title="Добавить файлы в PrintMaster",Multiselect=true,
            Filter="Документы и изображения|*.pdf;*.docx;*.md;*.png;*.jpg;*.jpeg;*.bmp|PDF|*.pdf|Word|*.docx|Markdown|*.md|Изображения|*.png;*.jpg;*.jpeg;*.bmp" };
        return dialog.ShowDialog(Application.Current.MainWindow)==true ? dialog.FileNames : [];
    }
    public string? PickPdfOutput()
    {
        var dialog=new SaveFileDialog { Title="Сохранить результат печати",Filter="PDF|*.pdf",FileName="PrintMaster.pdf",AddExtension=true };
        return dialog.ShowDialog(Application.Current.MainWindow)==true ? dialog.FileName : null;
    }
    public string? PickLibreOffice()
    {
        var dialog=new OpenFileDialog { Title="Выберите LibreOffice program/soffice.com",Filter="LibreOffice|soffice.com;soffice.exe" };
        return dialog.ShowDialog(Application.Current.MainWindow)==true ? dialog.FileName : null;
    }
    public bool ConfirmCropping(int count)=>MessageBox.Show(Application.Current.MainWindow,
        $"Режим «Заполнить» обрежет края у {count} выбранных страниц. Продолжить с этими настройками?\n\nЧтобы сохранить весь контент, выберите «Вписать».",
        "Обрезка при печати",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
    public void Error(string message)=>MessageBox.Show(Application.Current.MainWindow,message,"PrintMaster",MessageBoxButton.OK,MessageBoxImage.Error);
}
