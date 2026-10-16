using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintMaster.Models;
using PrintMaster.Services;

namespace PrintMaster.ViewModels;

public partial class PrintSessionViewModel
{
    private TextStyle _defaultTextStyle=new(FontSizePt:14);
    private PrintPageItem? _textEditPage,_textEditAnchor;
    private Guid? _textEditId;
    private bool _createdTextPage,_endingTextEdit,_applyingTextFlow,_editingExistingText;
    private IDisposable? _textHistoryEdit;
    [ObservableProperty] private bool isEditingText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTextFormattingError))] private string textFormattingError="";
    public bool HasTextFormattingError=>TextFormattingError.Length!=0;
    public IReadOnlyList<string> TextFonts=>TextLayoutService.FontNames;
    public IReadOnlyList<Choice<Models.TextAlignment>> TextAlignments { get; }=[new(Models.TextAlignment.Left,"Слева"),new(Models.TextAlignment.Center,"По центру"),new(Models.TextAlignment.Right,"Справа")];
    private TextObject? ActiveText=>IsEditingAnnotation ? AnnotationPresentation?.Text : _textEditPage?.Document.Objects.OfType<TextObject>().FirstOrDefault(t=>t.Id==_textEditId);
    public TextObject? SelectedText=>ActiveText ?? AnnotationPresentation?.Text ?? SelectedSheetObject as TextObject;
    public bool IsTextSelected=>SelectedText!=null;
    public bool IsFlowText=>SelectedText?.Flow!=null;
    public bool ShowTextFormatting=>CanEdit && (IsEditingText || Tool==EditorTool.Text || Tool==EditorTool.Select && IsTextSelected);
    public bool CanResizeObject=>CanEdit && !IsFlowText;
    public bool CanSetTextWidth=>CanEdit && IsTextSelected && !IsFlowText;
    public double TextBlockWidthMm
    {
        get=>SelectedText?.Placement.BoundsMm.Width ?? 40;
        set
        {
            if(!CanSetTextWidth || !double.IsFinite(value) || value<=0 || value>10000) return;
            if(AnnotationPresentation is {} a) UpdateAnnotationText(a.Annotation.Text,a.Text.Style,value);
            else if(SelectedText is {} text)
            {
                var layout=TextLayoutService.Layout(text.Text,text.Style,value,text.Flow?.HasNext==true);
                (_textEditPage ?? SelectedPage)!.Document.ReplaceObject(text with { Placement=text.Placement with { BoundsMm=text.Placement.BoundsMm with { Width=value,Height=layout.HeightMm } } },"Ширина текста");
            }
            NotifyTextProperties();
        }
    }
    private TextStyle CurrentTextStyle=>SelectedText?.Flow?.Notebook==true ? (_textEditPage ?? SelectedPage)?.Document.NotebookText?.Style ?? SelectedText.Style : SelectedText?.Style ?? _defaultTextStyle;
    public string TextFontFamily { get=>CurrentTextStyle.FontFamily; set=>ChangeTextStyle(CurrentTextStyle with { FontFamily=value }); }
    public double TextFontSizePt { get=>CurrentTextStyle.FontSizePt; set=>ChangeTextStyle(CurrentTextStyle with { FontSizePt=value }); }
    public bool TextBold { get=>CurrentTextStyle.Bold; set=>ChangeTextStyle(CurrentTextStyle with { Bold=value }); }
    public bool TextItalic { get=>CurrentTextStyle.Italic; set=>ChangeTextStyle(CurrentTextStyle with { Italic=value }); }
    public string TextColor { get=>CurrentTextStyle.Color; set=>ChangeTextStyle(CurrentTextStyle with { Color=value }); }
    public Models.TextAlignment TextAlignment { get=>CurrentTextStyle.Alignment; set=>ChangeTextStyle(CurrentTextStyle with { Alignment=value }); }
    public double TextLineSpacing { get=>CurrentTextStyle.LineSpacing; set=>ChangeTextStyle(CurrentTextStyle with { LineSpacing=value }); }
    public string EditingText
    {
        get=>ActiveText?.Flow?.Notebook==true ? _textEditPage?.Document.NotebookText?.Source ?? "" : ActiveText?.Text ?? "";
        set
        {
            if(IsEditingAnnotation)
            {
                if(value.Length>TextLimits.MaxCharacters) { TextFormattingError="Максимум — 1 000 000 символов в тексте."; return; }
                if(EditingText==value) return;
                UpdateAnnotationText(value,CurrentTextStyle); TextFormattingError=""; NotifyTextProperties(); return;
            }
            if(ActiveText is not {} text || _textEditPage==null || EditingText==value) return;
            if(value.Length>TextLimits.MaxCharacters) { TextFormattingError="Максимум — 1 000 000 символов в тексте."; return; }
            if(text.Flow?.Notebook==true)
            {
                SetNotebookStory(_textEditPage,value,_textEditPage.Document.NotebookText!.Style,text.Flow.StoryId);
                TextFormattingError=""; NotifyTextProperties(); return;
            }
            if(text.Flow is {} link && StoryText(link.StoryId).Length-text.Text.Length+value.Length>TextLimits.MaxCharacters) { TextFormattingError="Максимум — 1 000 000 символов во всем тексте продолжений."; return; }
            var layout=TextLayoutService.Layout(value,text.Style,text.Placement.BoundsMm.Width,text.Flow?.HasNext==true);
            _textEditPage.Document.ReplaceObject(text with { Text=value,Placement=text.Placement with { BoundsMm=text.Placement.BoundsMm with { Height=layout.HeightMm } } });
            TextFormattingError=""; NotifyTextProperties();
        }
    }
    public double EditingLeft=>(ActiveText?.Placement.BoundsMm.X ?? 0)*SheetEditing.DipPerMm;
    public double EditingTop=>(ActiveText?.Placement.BoundsMm.Y ?? 0)*SheetEditing.DipPerMm
        +(EditingRotation==0 ? 0 : ((ActiveText?.Placement.BoundsMm.Height ?? 20)*SheetEditing.DipPerMm-EditingHeight)/2);
    public double EditingWidth=>(ActiveText?.Placement.BoundsMm.Width ?? 40)*SheetEditing.DipPerMm;
    public double EditingRotation=>ActiveText?.Placement.RotationDegrees ?? 0;
    public double EditingHeight=>ActiveText?.Flow?.Notebook==true
        ? Math.Max(40,ActiveText.Placement.BoundsMm.Height*SheetEditing.DipPerMm)
        : Math.Max(40,Math.Min((ActiveText?.Placement.BoundsMm.Height ?? 20)*SheetEditing.DipPerMm,Math.Max(40,(SafeAreaMm.Bottom-(ActiveText?.Placement.BoundsMm.Y ?? 0))*SheetEditing.DipPerMm)));
    public string TextWarning
    {
        get
        {
            var text=SelectedText; if(text==null) return "";
            var layout=TextLayoutService.Layout(text);
            var warnings=new List<string>();
            if(layout.FontFallback) warnings.Add($"Шрифт «{text.Style.FontFamily}» или его начертание недоступны. Сейчас используется {layout.FontFamily}.");
            if(layout.WidthOverflow) warnings.Add("Текст шире блока: увеличьте его ширину или измените шрифт.");
            if(layout.HeightMm>text.Placement.BoundsMm.Height+0.05) warnings.Add("В блок не помещаются все строки. Увеличьте высоту или измените ширину.");
            if(AnnotationPresentation is {} annotation)
            {
                var page=IsEditingAnnotation ? _textEditPage! : SelectedPage!;
                var content=page.Document.Objects.OfType<ContentObject>().Single(c=>c.Id==annotation.ContentId);
                var sourceBounds=AnnotationLayout.VisualBounds(annotation.Annotation);
                if(sourceBounds.Intersect(content.Edits.Crop)!=sourceBounds)
                    warnings.Add("Часть надписи выходит за кадр фотографии и будет обрезана вместе с фото. Измените кроп, положение или ширину текста.");
            }
            if(SheetEditing.Outside(text,SafeAreaMm)) warnings.Add(text.Flow?.Notebook==true ? "Текст выходит за печатные поля. Проверьте профиль тетради и отступы." : IsEditingText && IsFlowText ? "После завершения ввода текст продолжится на следующих листах." : "Текст выходит за печатные поля. Переместите блок или используйте «Текст на страницах».");
            return string.Join("\n",warnings);
        }
    }
    public bool HasTextWarning=>TextWarning.Length!=0;
    private void NotifyTextProperties()
    {
        foreach(var name in new[] { nameof(SelectedText),nameof(IsTextSelected),nameof(IsFlowText),nameof(ShowTextFormatting),nameof(CanResizeObject),nameof(TextFontFamily),nameof(TextFontSizePt),nameof(TextBold),nameof(TextItalic),nameof(TextColor),nameof(TextAlignment),nameof(TextLineSpacing),nameof(SheetTextSizePt),nameof(TextWarning),nameof(HasTextWarning),nameof(EditingText),nameof(EditingLeft),nameof(EditingTop),nameof(EditingWidth),nameof(EditingHeight),nameof(EditingRotation) }) OnPropertyChanged(name);
        BeginFlowTextCommand.NotifyCanExecuteChanged(); EditSelectedTextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TextBlockWidthMm)); OnPropertyChanged(nameof(CanSetTextWidth));
        NotifyInspector(); FinishTextEditCommand.NotifyCanExecuteChanged(); CancelTextEditCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsEditingTextChanged(bool value) { NotifyTextProperties(); UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged(); }
    private void ChangeTextStyle(TextStyle style)
    {
        if(!CanEdit || CurrentTextStyle==style) return;
        try
        {
            TextLayoutService.ValidateStyle(style); TextFormattingError="";
            if(AnnotationPresentation is {} annotation)
            { UpdateAnnotationText(annotation.Annotation.Text,style); NotifyTextProperties(); return; }
            if(SelectedText is {} text)
            {
                if(text.Locked) return;
                if(text.Flow?.Notebook==true)
                { SetNotebookStory(_textEditPage ?? SelectedPage!,StoryText(text.Flow.StoryId),style,text.Flow.StoryId); NotifyTextProperties(); return; }
                if(text.Flow!=null && !IsEditingText) { ApplyTextFlow(StoryText(text.Flow.StoryId),style,SelectedPage!.Document.Paper,text.Flow.StoryId); return; }
                var layout=TextLayoutService.Layout(text.Text,style,text.Placement.BoundsMm.Width,text.Flow?.HasNext==true);
                (_textEditPage ?? SelectedPage)!.Document.ReplaceObject(text with { Style=style,Placement=text.Placement with { BoundsMm=text.Placement.BoundsMm with { Height=layout.HeightMm } } },"Оформить текст");
            }
            else { _defaultTextStyle=style; MarkProjectChanged(); }
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException) { TextFormattingError=ex.Message; }
        NotifyTextProperties();
    }
    internal void BeginTextEditing(PointD location,TextObject? existing=null)
    {
        if(!CanEdit || !IsSheetSelected && (!CanInsertOnCurrentPage || existing!=null)) return;
        FinishTextEditing();
        SelectedSourceAnnotation=null; _selectedTextAnnotationContentId=null;
        _textHistoryEdit=BeginHistoryEdit(existing==null ? "Добавить текст" : "Редактировать текст");
        _textEditPage=SelectedPage!; _textEditId=existing?.Id ?? Guid.NewGuid();
        _editingExistingText=existing!=null;
        _textEditPage.Document.BeginEdit(existing==null ? "Добавить текст" : "Редактировать текст");
        if(existing==null)
        {
            var item=SheetEditing.CreateText("",_defaultTextStyle,location,SafeAreaMm) with { Id=_textEditId.Value };
            if(_textEditPage.IsSheetLayout) _textEditPage.Document.AddObject(item);
            else InsertObject(CaptureInsertionTarget(_textEditPage),item);
        }
        SelectedSheetObjectId=_textEditId; Tool=EditorTool.Select; IsEditingText=true; TextFormattingError="";
        NotifyTextProperties(); Status="Введите текст на листе. Ctrl+Enter — готово, Esc — отменить ввод.";
    }
    private bool CanBeginFlowText()=>CanEdit && IsSheetSelected && !HasMarkdown;
    [RelayCommand(CanExecute=nameof(CanBeginFlowText))]
    private void BeginFlowText()
    {
        if(!CanBeginFlowText())return;
        if(SelectedPage?.Document.Paper.Notebook!=null)
        {
            FinishTextEditing();
            var existing=SelectedPage.Document.Objects.OfType<TextObject>().FirstOrDefault(t=>t.Flow?.Notebook==true);
            if(existing!=null) { BeginTextEditing(new(existing.Placement.BoundsMm.X,existing.Placement.BoundsMm.Y),existing); return; }
            BeginTextEditing(new(SafeAreaMm.X,SafeAreaMm.Y));
            var page=_textEditPage!; var temporary=ActiveText!;
            page.Document.RemoveObject(temporary.Id);
            SetNotebookStory(page,"",_defaultTextStyle);
            _textEditId=page.Document.Objects.OfType<TextObject>().First(t=>t.Flow?.Notebook==true).Id;
            SelectedSheetObjectId=_textEditId; NotifyTextProperties();
            Status="Введите текст разворота. Ctrl+Enter — распределить слева направо; остаток будет показан как переполнение."; return;
        }
        FinishTextEditing(); using var history=BeginHistoryEdit("Текст на страницах"); var selected=SelectedPage!; _textEditAnchor=selected;
        if(selected.Document.Objects.Count!=0)
        {
            var page=PrintPageItem.CreateBlank(selected.Document.Paper); page.Copies=selected.Copies; page.IsIncluded=selected.IsIncluded; page.PrintOptions=selected.PrintOptions;
            AddPage(page); Pages.Move(Pages.IndexOf(page),Pages.IndexOf(selected)+1); SelectedPage=page;
            _createdTextPage=true;
        }
        BeginTextEditing(new(SafeAreaMm.X,SafeAreaMm.Y));
        var item=ActiveText!; _textEditPage!.Document.ReplaceObject(item with { Flow=new(Guid.NewGuid(),0,false) });
        NotifyTextProperties(); Status="Введите длинный текст. При завершении появятся страницы продолжения с тем же шрифтом.";
    }
    private bool CanEditSelectedText()=>CanEdit && IsTextSelected && SelectedText?.Locked!=true;
    [RelayCommand(CanExecute=nameof(CanEditSelectedText))]
    private void EditSelectedText()
    {
        if(AnnotationPresentation is {} a) BeginAnnotationTextEditing(a.Annotation.Start,a.Annotation,a.ContentId);
        else if(SelectedText is {} text) BeginTextEditing(new(text.Placement.BoundsMm.X,text.Placement.BoundsMm.Y),text);
    }
    [RelayCommand(CanExecute=nameof(IsEditingText))] private void FinishTextEdit()=>FinishTextEditing();
    [RelayCommand(CanExecute=nameof(IsEditingText))] private void CancelTextEdit()=>FinishTextEditing(false);
    internal void FinishTextEditing(bool commit=true)
    {
        if(!IsEditingText || _endingTextEdit || _textEditPage==null) return;
        if(IsEditingAnnotation) { FinishAnnotationTextEditing(commit); return; }
        _endingTextEdit=true; var page=_textEditPage; var text=ActiveText; var createdPage=_createdTextPage; var anchor=_textEditAnchor; var editingExisting=_editingExistingText;
        try
        {
            var flowText=text?.Flow is {} flow ? StoryText(flow.StoryId) : null;
            var flowStyle=text?.Style; var paper=page.Document.Paper;
            IsEditingText=false; _textEditPage=null; _textEditId=null; _createdTextPage=false; _textEditAnchor=null;
            var sourceLength=text?.Flow?.Notebook==true ? page.Document.NotebookText?.Source.Length ?? 0 : text?.Text.Length ?? 0;
            if(!commit || text==null || !editingExisting && sourceLength==0)
            {
                page.Document.EndEdit(false);
                if(createdPage) { DetachFlowPage(page); SelectedPage=anchor; }
            }
            else if(text.Flow is { Notebook:true })
            {
                if(page.Document.NotebookText?.Source.Length==0) page.Document.SetNotebookText(null,[]);
                page.Document.EndEdit(true);
            }
            else if(text.Flow is {} link)
            {
                // Restore the pre-edit models before capturing a single queue-wide undo action.
                var position=Pages.IndexOf(page); page.Document.EndEdit(false);
                try { ApplyTextFlow(flowText!,flowStyle!,paper,link.StoryId,page,position); }
                catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
                {
                    // Retain every typed character if it cannot be paginated with this font/paper.
                    if(page.Document.Objects.Any(o=>o.Id==text.Id)) page.Document.ReplaceObject(text,"Редактировать текст"); else page.Document.AddObject(text);
                    TextFormattingError=ex.Message; Status="Текст сохранен в блоке. "+ex.Message;
                }
            }
            else
            {
                if(text.Text.Length==0) page.Document.RemoveObject(text.Id);
                page.Document.EndEdit(true);
            }
            NotifySheetSelection(); NotifyTextProperties();
            if(TextFormattingError.Length==0) Status=commit ? "Текст сохранен." : "Ввод текста отменен.";
        }
        finally
        {
            _endingTextEdit=false; var history=_textHistoryEdit; _textHistoryEdit=null; history?.Dispose();
        }
    }
}
