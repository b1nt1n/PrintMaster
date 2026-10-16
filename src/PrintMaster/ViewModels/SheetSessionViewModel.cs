using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    [ObservableProperty] private Guid? selectedSheetObjectId;
    public bool IsSheetSelected=>SelectedPage?.IsSheetLayout==true;
    public bool CanInsertOnCurrentPage=>SelectedPage is {} page && (page.IsBlank || page.IsImage) && !page.HasMissingSources;
    public bool IsSourceEditorVisible=>HasSelection && !IsSheetSelected;
    public bool CanCropImage=>IsImageSelected && SelectedPage?.IsComposed!=true;
    public bool CanCropSelection=>CanEdit && SelectedPage is {} page && (IsSheetSelected ? SelectedSheetObject : page.Document.PrimaryContent) is ContentObject {Locked:false,Visible:true} content
        && page.Document.Snapshot().Assets.Any(a=>a.Id==content.AssetId && a.Kind==SourceKind.Image);
    public string CropActionAvailability=>CanCropSelection ? "Кадрировать выбранное фото… (C)" : !CanEdit ? EditorActionAvailability : "Выберите одно доступное фото на листе для кадрирования.";
    public bool ShowDrawingSettings=>ShowEditorTools;
    public SheetObject? SelectedSheetObject=>IsSheetSelected && _selectedObjectIds.Length<=1 ? SelectedPage?.Document.Objects.FirstOrDefault(o=>o.Id==SelectedSheetObjectId) : null;
    public bool HasSheetObject=>SelectedSheetObject!=null;
    private bool CanModifySheetObject()=>IsIdle && HasSheetObject && !ShowPrintPreview && SelectedSheetObject?.Locked!=true;
    private bool CanInsertSheetImage()=>IsIdle && CanInsertOnCurrentPage && !ShowPrintPreview;
    public double SheetTextSizePt { get=>TextFontSizePt; set=>TextFontSizePt=value; }
    public double SheetStrokeWidthMm { get=>StrokeThickness/SheetEditing.DipPerMm; set=>StrokeThickness=value*SheetEditing.DipPerMm; }
    public double ObjectX { get=>SelectedSheetObject?.Placement.BoundsMm.X ?? 0; set=>SetObjectBounds("x",value); }
    public double ObjectY { get=>SelectedSheetObject?.Placement.BoundsMm.Y ?? 0; set=>SetObjectBounds("y",value); }
    public double ObjectWidth { get=>SelectedSheetObject?.Placement.BoundsMm.Width ?? 0; set=>SetObjectBounds("width",value); }
    public double ObjectHeight { get=>SelectedSheetObject?.Placement.BoundsMm.Height ?? 0; set=>SetObjectBounds("height",value); }
    public bool CanSetPhotoAspectRatio=>CanEdit && SelectedSheetObject is ContentObject { Locked:false,Visible:true } content
        && SelectedPage!.Document.Snapshot().Assets.Any(a=>a.Id==content.AssetId && a.Kind==SourceKind.Image);
    public bool KeepPhotoAspectRatio
    {
        get=>SelectedSheetObject is not ContentObject { KeepAspectRatio:false };
        set
        {
            if(!CanSetPhotoAspectRatio) { NotifySheetSelection(); return; }
            FinishTextEditing();
            if(!CanSetPhotoAspectRatio) { NotifySheetSelection(); return; }
            var content=(ContentObject)SelectedSheetObject!;
            if(content.KeepAspectRatio==value) return;
            SelectedPage!.Document.ReplaceObject(content with { KeepAspectRatio=value },"Пропорции фото");
        }
    }
    public string SheetObjectSummary=>_selectedObjectIds.Length>1 ? $"Выделено объектов: {_selectedObjectIds.Length}" : SelectedSheetObject switch { TextObject=>"Текст",ContentObject=>"Изображение",ShapeObject=>"Фигура",InkObject=>"Рисунок",_=>"Выберите объект на листе" };
    public RectD SafeAreaMm=>new(CanvasSafeArea.X/SheetEditing.DipPerMm,CanvasSafeArea.Y/SheetEditing.DipPerMm,CanvasSafeArea.Width/SheetEditing.DipPerMm,CanvasSafeArea.Height/SheetEditing.DipPerMm);
    public bool HasSheetClipping=>IsSheetSelected && SheetEditing.HasClipping(SelectedPage!.Snapshot(),SafeAreaMm);
    public bool HasSheetPaperMismatch=>Pages.Where(p=>p.IsIncluded && p.IsSheetLayout).Select(p=>p.Document.Paper.OrientedSizeMm).Distinct().Skip(1).Any();
    public string SheetPaperWarning=>HasSheetPaperMismatch ? "Выбраны разные размеры бумаги. При печати потребуется подтверждать ее смену." : "";

    partial void OnSelectedSheetObjectIdChanged(Guid? value)
    {
        if(!_settingObjectSelection && (value==null || !_selectedObjectIds.Contains(value.Value))) SetObjectSelection(value==null ? [] : [value.Value]);
        NotifySheetSelection();
    }
    partial void OnInkColorChanged(string value) { }
    partial void OnStrokeThicknessChanged(double value)=>OnPropertyChanged(nameof(SheetStrokeWidthMm));
    partial void OnTextSizeChanged(double value)=>OnPropertyChanged(nameof(SheetTextSizePt));
    private void NotifySheetSelection()
    {
        OnPropertyChanged(nameof(CanCropSelection)); OnPropertyChanged(nameof(CropActionAvailability));
        foreach(var name in new[] { nameof(SelectedSheetObject),nameof(HasSheetObject),nameof(SheetObjectSummary),nameof(ObjectX),nameof(ObjectY),nameof(ObjectWidth),nameof(ObjectHeight),nameof(KeepPhotoAspectRatio),nameof(CanSetPhotoAspectRatio),nameof(ObjectAngle),nameof(CanSetObjectAngle),nameof(CanPlaceSourceImage),nameof(HasSheetClipping) }) OnPropertyChanged(name);
        PlaceSourceImageCommand.NotifyCanExecuteChanged();
        AlignSheetObjectCommand.NotifyCanExecuteChanged(); RemoveSheetObjectCommand.NotifyCanExecuteChanged();
        AlignSheetSelectionCommand.NotifyCanExecuteChanged(); OrderSheetSelectionCommand.NotifyCanExecuteChanged();
        ApplyPlacementAlignmentCommand.NotifyCanExecuteChanged();
        RotateCommand.NotifyCanExecuteChanged();
        InsertSheetImagesCommand.NotifyCanExecuteChanged();
        NotifyTextProperties(); NotifyFlowHistory(); NotifyInspector();
        CopySelectionCommand.NotifyCanExecuteChanged();
    }
    private void NotifySheetMode()
    {
        foreach(var name in new[] { nameof(IsSheetSelected),nameof(CanInsertOnCurrentPage),nameof(IsSourceEditorVisible),nameof(CanCropImage),nameof(ShowDrawingSettings),nameof(CanvasWidth),nameof(CanvasHeight),nameof(PaperSummary),nameof(CanvasSafeArea),nameof(HasSheetClipping),nameof(HasSheetPaperMismatch),nameof(SheetPaperWarning) }) OnPropertyChanged(name);
        NotifySheetSelection(); PrintCommand.NotifyCanExecuteChanged();
    }
    private void SetObjectBounds(string dimension,double value)
    {
        if(!CanModifySheetObject() || IsFlowText || !double.IsFinite(value) || Math.Abs(value)>10000 || ((dimension is "width" or "height") && value<=0)) { NotifySheetSelection(); return; }
        var item=SelectedSheetObject!; var b=item.Placement.BoundsMm;
        var next=dimension switch { "x"=>b with { X=value },"y"=>b with { Y=value },"width"=>b with { Width=value },_=>b with { Height=value } };
        if(item is ContentObject { KeepAspectRatio:true })
        {
            if(dimension=="width") next=next with { Height=b.Height*next.Width/b.Width };
            if(dimension=="height") next=next with { Width=b.Width*next.Height/b.Height };
        }
        if(item is TextObject text)
        {
            var needed=SheetEditing.MeasureTextHeight(text.Text,text.Style,next.Width);
            if(dimension=="width") next=next with { Height=needed };
            if(dimension=="height" && next.Height<needed) { Status="Высоты не хватает для всего текста. Увеличьте ширину блока или сократите текст."; NotifySheetSelection(); return; }
        }
        if(!double.IsFinite(next.Width) || !double.IsFinite(next.Height) || next.Width>10000 || next.Height>10000)
        { Status="Размер объекта с учетом пропорций должен быть не больше 10000 мм."; NotifySheetSelection(); return; }
        SelectedPage!.Document.SetPlacement(item.Id,item.Placement with { BoundsMm=next });
    }
    [RelayCommand(CanExecute=nameof(IsIdle))]
    private async Task CreateSheetAsync()
    {
        FinishTextEditing();
        var defaults=new PrintSettings { PrinterName=_preferences.PrinterName,AvailableNotebookProfiles=_preferences.NotebookProfiles }; var paper=_dialogs.PickNewSheet(defaults); if(paper==null) return;
        PrintPageItem page;
        using(var history=BeginHistoryEdit("Создать лист"))
        { page=PrintPageItem.CreateBlank(paper); ShowPrintPreview=false; AddPage(page); SelectedPage=page; }
        // Comfortable defaults for physical text, retained in the saved editor preferences.
        SheetTextSizePt=14; SheetStrokeWidthMm=0.5; Tool=EditorTool.Select;
        _=EnsureThumbnailAsync(page); Status="Новый лист создан. Добавьте текст, вставьте фото или нарисуйте фигуру.";
        if(paper.Notebook is {} profile) await RememberNotebookProfileAsync(profile);
    }
    [RelayCommand(CanExecute=nameof(CanInsertSheetImage))]
    private async Task InsertSheetImagesAsync()=>await InsertSheetImagesFromAsync(_dialogs.PickSheetImages());
    internal async Task InsertSheetImagesFromAsync(IEnumerable<string> paths,PointD? anchor=null)
    {
        if(!CanInsertSheetImage()) return; FinishTextEditing(); var input=paths.ToArray(); if(input.Length==0) return;
        using var history=BeginHistoryEdit("Вставить фотографии");
        var target=CaptureInsertionTarget(SelectedPage!); var errors=new List<string>(); var inserted=0; var completedFiles=0; BeginOperation(SessionOperationKind.InsertImages,total:input.Length);
        try
        {
            foreach(var path in input)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                try
                {
                    if(System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp")) throw new ArgumentException("На лист можно вставить PNG, JPG или BMP.");
                    var imported=(await _import.ImportAsync(path,_operation.Token,ImportProgressFor(path,completedFiles,input.Length))).Single(); var source=imported.Document.PrimaryAsset!;
                    var area=ImageInsertionArea(target.Safe,anchor);
                    var bounds=PageLayout.Place(source.LogicalSize,area,ScaleMode.Fit);
                    var content=new ContentObject(Guid.NewGuid(),new(bounds),source.Id,ContentEdits.Empty(source.LogicalSize));
                    InsertObject(target,content,source); inserted++;
                }
                catch(OperationCanceledException) { throw; }
                catch(Exception ex) { errors.Add(System.IO.Path.GetFileName(path)+": "+ex.Message); }
                completedFiles++; ApplyOperationReport(new($"Обработано фотографий: {completedFiles}/{input.Length}",completedFiles,input.Length));
            }
            Status=inserted>0 ? $"На лист вставлено фотографий: {inserted}. Перетаскивайте их или задайте положение и размер справа." : "Фотографии не вставлены. Исходный лист не изменен.";
            if(errors.Count!=0) { FreezeOperationReports(); var reason=errors[0].Split('\n')[0].Trim(); if(reason.Length>250) reason=reason[..250]+"…"; _dialogs.ShowOperationError(new("Не все фотографии вставлены",reason+$"\nНе удалось вставить файлов: {errors.Count}. Уже добавленные фотографии сохранены.","Проверьте изображения и доступ к ним, затем вставьте оставшиеся файлы.",string.Join("\n\n",errors))); }
        }
        catch(OperationCanceledException) { Status="Вставка отменена. Уже добавленные фотографии сохранены на листе."; }
        finally { EndOperation(); NotifySheetSelection(); }
    }
    internal void AddSheetText(PointD location,string text,TextObject? existing=null)
    {
        if(!CanEdit || !IsSheetSelected) return;
        if(string.IsNullOrWhiteSpace(text)) { if(existing!=null) SelectedPage!.Document.RemoveObject(existing.Id); return; }
        var style=existing?.Style ?? _defaultTextStyle;
        var item=SheetEditing.CreateText(text,style,location,SafeAreaMm,existing);
        if(existing==null) SelectedPage!.Document.AddObject(item); else SelectedPage!.Document.ReplaceObject(item,"Изменить текст");
        SelectedSheetObjectId=item.Id; Tool=EditorTool.Select;
        Status=SheetEditing.Outside(item,SafeAreaMm) ? "Текст выходит за безопасную область. Переместите блок, сократите текст или уменьшите его размер перед печатью." : "Текст добавлен. Двойной щелчок по блоку — изменить текст.";
    }
    [RelayCommand(CanExecute=nameof(CanModifySheetObject))]
    private void AlignSheetObject(string alignment)=>SelectedPage!.Document.SetPlacement(SelectedSheetObject!.Id,SheetEditing.Align(SelectedSheetObject,SafeAreaMm,alignment));
    [RelayCommand(CanExecute=nameof(CanModifySheetObject))]
    private void RemoveSheetObject() { SelectedPage!.Document.RemoveObject(SelectedSheetObject!.Id); SelectedSheetObjectId=null; }
}
