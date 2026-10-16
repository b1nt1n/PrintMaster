using PrintMaster.Models;
using PrintMaster.Services;
using System.ComponentModel;
using System.Windows.Input;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private string WaitForOperationHint=>CancellationRequested
        ? "Отмена уже запрошена. Дождитесь безопасного завершения текущей операции."
        : "Дождитесь завершения текущей операции или отмените её.";
    private const string LeavePrintPreviewHint="Выключите «Печатный вид», чтобы вернуться к редактированию.";
    private const string RelinkMaterialHint="Материал страницы отсутствует. Выберите «Проект → Переподключить исходники…».";
    private bool _actionAvailabilityInitialized;
    private static readonly string[] ActionHintProperties=
    [
        nameof(IdleActionAvailability),nameof(PrintActionAvailability),nameof(ExportActionAvailability),
        nameof(SaveCopyActionAvailability),nameof(ResetCropActionAvailability),nameof(RotateActionAvailability),
        nameof(UndoActionAvailability),nameof(RedoActionAvailability),nameof(EditorActionAvailability),
        nameof(InsertImageActionAvailability),nameof(QueueSelectionActionAvailability),
        nameof(QueueMoveUpActionAvailability),nameof(QueueMoveDownActionAvailability),
        nameof(ObjectEditActionAvailability),nameof(ObjectSelectionActionAvailability),
        nameof(CopyActionAvailability),nameof(PasteActionAvailability),nameof(CancelActionAvailability)
    ];

    // The same view model owns the event, so this subscription does not extend its lifetime.
    // A fixed dependency list prevents notifications for the hints from recursively notifying themselves.
    private void InitializeActionAvailability()
    {
        if(_actionAvailabilityInitialized) return;
        _actionAvailabilityInitialized=true;
        PropertyChanged+=ActionAvailabilityChanged;
    }
    private void ActionAvailabilityChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is null or "" or nameof(IsBusy) or nameof(IsIdle) or nameof(CancellationRequested) or nameof(SelectedPage)
            or nameof(HasSelection) or nameof(IsImageSelected) or nameof(IsDocumentSelected)
            or nameof(IsSheetSelected) or nameof(CanEdit) or nameof(CanCropImage) or nameof(CanInsertOnCurrentPage)
            or nameof(ShowPrintPreview) or nameof(IsEditingText) or nameof(IsTextSelected)
            or nameof(SelectedSheetObject) or nameof(HasSheetObject) or nameof(SelectedObjects)
            or nameof(SelectedSourceAnnotation) or nameof(SourceImageSelected)
            or nameof(HighlightedPageCount) or nameof(QueueSummary) or nameof(SelectedCount) or nameof(Impressions)
            or nameof(HasPendingPaperJob) or nameof(UndoDescription) or nameof(RedoDescription))
            foreach(var property in ActionHintProperties) OnPropertyChanged(property);
    }

    public string IdleActionAvailability=>IsBusy ? WaitForOperationHint : "Действие доступно.";
    public string CancelActionAvailability=>!IsBusy ? "Сейчас нет выполняющейся операции для отмены."
        : CancellationRequested ? "Отмена уже запрошена. Дождитесь безопасного завершения текущего этапа."
        : "Запросить отмену текущей операции. Уже выполненная часть сохраняется; завершение может занять время.";
    public string PrintActionAvailability=>IsBusy ? WaitForOperationHint : HasPendingPaperJob
        ? "Продолжить ожидающее задание: загрузить бумагу или напечатать оборот. Уже переданная часть не повторяется."
        : SelectedCount==0 ? "Отметьте страницы галочками в очереди, чтобы открыть окно печати."
        : $"Открыть окно печати: {RussianQuantity.Format(SelectedCount,"страница","страницы","страниц")} · {RussianQuantity.Format(Impressions,"оттиск","оттиска","оттисков")}. Принтер и параметры можно выбрать в этом окне.";
    public string ExportActionAvailability=>IsBusy ? WaitForOperationHint : Pages.Count==0
        ? "Добавьте файл или создайте лист, чтобы экспортировать страницы."
        : "Сохранить листы в PNG или PDF. Область экспорта и параметры выбираются в отдельном окне.";
    public string SaveCopyActionAvailability=>IsBusy ? WaitForOperationHint : !IsImageSelected
        ? "Выберите страницу с исходным изображением. Для целого листа или документа используйте «Экспорт листов…»."
        : "Сохранить копию исходного изображения с кадрированием и аннотациями. Для целого листа используйте «Экспорт листов…».";
    public string ResetCropActionAvailability=>IsBusy ? WaitForOperationHint : !IsImageSelected
        ? "Выберите страницу с исходным изображением, чтобы сбросить кадрирование."
        : "Вернуть полный размер исходного изображения без кадрирования.";
    public string RotateActionAvailability=>IsBusy ? WaitForOperationHint : CanRotate()
        ? "Повернуть выбранное изображение или страницу на 90°."
        : SelectedPage==null ? "Выберите страницу для поворота."
        : IsSheetSelected ? "Выберите фотографию на листе. Фигуры и текст поворачиваются ручкой объекта."
        : "На выбранной странице нет исходного изображения или документа для поворота.";
    public string UndoActionAvailability=>HistoryActionHint(false);
    public string RedoActionAvailability=>HistoryActionHint(true);
    private string HistoryActionHint(bool redo)
    {
        if(IsBusy) return WaitForOperationHint;
        if(IsEditingText) return "Завершите ввод текста кнопкой «Готово» или «Отмена». В текстовом поле Ctrl+Z/Ctrl+Y изменяют сам текст.";
        if(ShowPrintPreview) return LeavePrintPreviewHint;
        if(_historyDepth!=0) return "Завершите текущее редактирование, чтобы изменить историю действий.";
        return redo ? _historyRedo.Count==0 ? "Нет отменённых действий для повтора." : RedoDescription
            : _historyUndo.Count==0 ? "Нет действий для отмены." : UndoDescription;
    }
    public string EditorActionAvailability=>CanEdit ? "Редактировать объекты и аннотации на выбранной странице."
        : EditorUnavailableHint();
    private string EditorUnavailableHint()
    {
        if(IsBusy) return WaitForOperationHint;
        if(SelectedPage==null) return "Выберите изображение или создайте пустой лист для редактирования.";
        if(SelectedPage.HasMissingSources) return RelinkMaterialHint;
        if(ShowPrintPreview) return LeavePrintPreviewHint;
        if(IsDocumentSelected) return "PDF и DOCX доступны для просмотра и поворота. Добавьте текст или рисунок на пустой лист либо изображение.";
        return "Выберите изображение или пустой лист для редактирования.";
    }
    public string InsertImageActionAvailability=>CanInsertSheetImage()
        ? "Вставить фотографию на текущий лист с возможностью перемещения и изменения размера."
        : EditorUnavailableHint();
    public string QueueSelectionActionAvailability=>IsBusy ? WaitForOperationHint : HighlightedPageCount==0
        ? "Выделите страницы в очереди. Ctrl/Shift+щелчок — несколько страниц; Ctrl+A в очереди — все страницы."
        : $"Применить действие к {RussianQuantity.Format(HighlightedPageCount,"выделенной странице","выделенным страницам","выделенным страницам")}. Галочки печати задаются отдельно.";
    public string QueueMoveUpActionAvailability=>QueueMoveActionHint(-1);
    public string QueueMoveDownActionAvailability=>QueueMoveActionHint(1);
    private string QueueMoveActionHint(int direction)
    {
        if(!HasHighlightedPages()) return QueueSelectionActionAvailability;
        if(direction is not (-1 or 1)) return "Укажите направление перемещения страниц.";
        if(CanMoveHighlightedPages(direction)) return direction<0 ? "Переместить выделенные страницы выше." : "Переместить выделенные страницы ниже.";
        return direction<0 ? "Выделенные страницы уже находятся в начале очереди."
            : "Выделенные страницы уже находятся в конце очереди.";
    }
    public string ObjectEditActionAvailability=>CanModifySheetObject()
        ? "Изменить выбранный объект на листе."
        : IsBusy ? WaitForOperationHint : ShowPrintPreview ? LeavePrintPreviewHint
        : SelectedSheetObject?.Locked==true ? "Выбранный объект заблокирован. Выберите другой объект."
        : "Выберите один объект на листе. Для нескольких объектов используйте команды группы в меню ПКМ.";
    public string ObjectSelectionActionAvailability=>CanModifySheetSelection()
        ? "Изменить выделенные объекты на листе."
        : !CanEdit ? EditorUnavailableHint() : !IsSheetSelected ? "Разместите фотографию на листе, чтобы изменять объекты макета."
        : "Выделите объекты на листе. Ctrl+щелчок — группа; Ctrl+A на листе — все доступные объекты.";
    public string CopyActionAvailability=>CanCopySelection ? "Скопировать выделенный объект вместе с оформлением."
        : IsBusy ? WaitForOperationHint : ShowPrintPreview ? LeavePrintPreviewHint
        : "Выделите объект на листе, исходную фотографию или аннотацию. Для копирования страниц перейдите в очередь.";
    // Clipboard availability is queried on demand: its contents can change outside PrintMaster.
    public string PasteActionAvailability=>IsBusy ? WaitForOperationHint : CanPasteClipboard
        ? "Вставить из буфера. На холсте содержимое добавляется на лист; в очереди — отдельными страницами."
        : "В буфере нет подходящего содержимого. Скопируйте файл, фотографию, текст или объект PrintMaster.";

    public string GetActionHint(ICommand? command,object? parameter=null)
    {
        if(command==null) return "";
        if(ReferenceEquals(command,CancelCommand)) return CancelActionAvailability;
        if(ReferenceEquals(command,PrintCommand)) return PrintActionAvailability;
        if(ReferenceEquals(command,ExportPagesCommand)) return ExportActionAvailability;
        if(ReferenceEquals(command,SaveCopyCommand)) return SaveCopyActionAvailability;
        if(ReferenceEquals(command,ResetCropCommand)) return ResetCropActionAvailability;
        if(ReferenceEquals(command,RotateCommand)) return RotateActionAvailability;
        if(ReferenceEquals(command,UndoCommand) || ReferenceEquals(command,UndoQueueCommand)) return UndoActionAvailability;
        if(ReferenceEquals(command,RedoCommand) || ReferenceEquals(command,RedoQueueCommand)) return RedoActionAvailability;
        if(ReferenceEquals(command,InsertSheetImagesCommand)) return InsertImageActionAvailability;
        if(ReferenceEquals(command,CopySelectionCommand)) return CopyActionAvailability;
        if(ReferenceEquals(command,PasteClipboardCommand)) return PasteActionAvailability;
        if(ReferenceEquals(command,AlignSheetObjectCommand) || ReferenceEquals(command,RemoveSheetObjectCommand)) return ObjectEditActionAvailability;
        if(ReferenceEquals(command,AlignSheetSelectionCommand)) return ObjectSelectionActionAvailability;
        if(ReferenceEquals(command,OrderSheetSelectionCommand))
        {
            if(!CanModifySheetSelection()) return ObjectSelectionActionAvailability;
            if(parameter is not int orderDirection || orderDirection is not (-2 or -1 or 1 or 2)) return "Укажите направление изменения порядка объектов.";
            if(CanOrderSheetSelection(orderDirection)) return "Изменить порядок наложения выделенных объектов.";
            return orderDirection>0 ? "Выделенные объекты уже на переднем плане." : "Выделенные объекты уже на заднем плане.";
        }
        if(ReferenceEquals(command,MoveHighlightedPagesCommand)) return parameter is int moveDirection ? QueueMoveActionHint(moveDirection) : "Укажите направление перемещения страниц.";
        if(ReferenceEquals(command,DeleteHighlightedPagesCommand) || ReferenceEquals(command,DuplicateHighlightedPagesCommand)
            || ReferenceEquals(command,RotateHighlightedPagesCommand) || ReferenceEquals(command,ToggleHighlightedPrintingCommand)
            || ReferenceEquals(command,SetHighlightedPrintingCommand) || ReferenceEquals(command,SetHighlightedCopiesCommand)) return QueueSelectionActionAvailability;
        if(ReferenceEquals(command,PlaceSourceImageCommand)) return CanPlaceSourceImage ? "Разместить исходную фотографию на листе для свободного перемещения."
            : !CanEdit ? EditorUnavailableHint() : "Выберите исходную фотографию, ещё не размещённую на листе.";
        if(ReferenceEquals(command,BeginFlowTextCommand)) return CanBeginFlowText() ? "Ввести текст с переносом по страницам."
            : !CanEdit ? EditorUnavailableHint() : "Создайте пустой лист или выберите лист с текстом для ввода с переносом.";
        if(ReferenceEquals(command,EditSelectedTextCommand)) return CanEditSelectedText() ? "Редактировать выбранный текст."
            : !CanEdit ? EditorUnavailableHint() : "Выберите доступный текстовый объект для редактирования.";
        if(ReferenceEquals(command,ArrangePhotoGridCommand))return PhotoGridAvailability;
        if(ReferenceEquals(command,ResumePaperJobCommand) || ReferenceEquals(command,DiscardPaperJobCommand))
            return IsBusy ? WaitForOperationHint : HasPendingPaperJob ? "Продолжить или закрыть ожидающее задание печати." : "Нет задания, ожидающего загрузки или переворота бумаги.";
        if(ReferenceEquals(command,ShowPaperGuideCommand)) return IsBusy ? WaitForOperationHint : CanShowPaperGuide()
            ? "Показать схему загрузки бумаги выбранной страницы." : "Выберите страницу для просмотра схемы загрузки бумаги.";
        return IsBusy && !command.CanExecute(parameter) ? WaitForOperationHint : "";
    }
}
