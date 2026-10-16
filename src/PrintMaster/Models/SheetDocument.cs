using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Immutable;

namespace PrintMaster.Models;

public sealed class SheetDocument : ObservableObject
{
    private SheetSnapshot _current;
    private readonly Stack<(SheetSnapshot State,string Action)> _undo=new();
    private readonly Stack<(SheetSnapshot State,string Action)> _redo=new();
    private SheetSnapshot? _editBefore;
    private string? _editAction;
    public Guid Id=>_current.Id;
    public int Revision { get; private set; }
    public bool CanUndo=>_editBefore==null && _undo.Count>0;
    public bool CanRedo=>_editBefore==null && _redo.Count>0;
    public bool IsEditing=>_editBefore!=null;
    public string? UndoAction=>_undo.TryPeek(out var entry) ? entry.Action : null;
    public string? RedoAction=>_redo.TryPeek(out var entry) ? entry.Action : null;
    public SheetPaper Paper=>_current.Paper;
    public IReadOnlyList<SheetObject> Objects=>_current.Objects;
    public SourceAsset? PrimaryAsset=>_current.PrimaryAsset;
    public ContentObject? PrimaryContent=>_current.PrimaryContent;
    public NotebookTextStory? NotebookText=>_current.NotebookText;

    private SheetDocument(SheetSnapshot initial) { Validate(initial); _current=initial; }
    public static SheetDocument CreateBlank(SheetPaper paper)=>new(new(Guid.NewGuid(),paper,[],[],null));
    public static SheetDocument FromSource(SourceAsset asset,SheetPaper? paper=null,ContentEdits? edits=null)
    {
        var placement=new ObjectPlacement(new(0,0,asset.PhysicalSizeMm.Width,asset.PhysicalSizeMm.Height),0,PlacementMode.Automatic);
        var content=new ContentObject(Guid.NewGuid(),placement,asset.Id,Freeze(edits ?? ContentEdits.Empty(asset.LogicalSize)));
        return new(new(Guid.NewGuid(),paper ?? SheetPaper.A4,[asset],[content],content.Id));
    }
    public SheetSnapshot Snapshot()=>_current;
    // A committed logical change; BeginEdit drafts emit one event when EndEdit commits.
    public event EventHandler<SheetChange>? Changed;
    public static SheetDocument Restore(SheetSnapshot snapshot)=>new(snapshot with { Objects=snapshot.Objects.Select(Freeze).ToImmutableArray() });
    public void ReplaceAsset(SourceAsset source)
    {
        var index=-1; for(var i=0;i<_current.Assets.Length;i++) if(_current.Assets[i].Id==source.Id) { index=i; break; }
        if(index<0) throw new KeyNotFoundException("Исходник не найден.");
        Commit(_current with { Assets=_current.Assets.SetItem(index,source) },"Переподключить исходник");
    }

    public void SetPaper(SheetPaper paper) => Commit(_current with { Paper=paper },"Бумага");
    public void SetNotebookText(NotebookTextStory? story,IEnumerable<TextObject> parts,SheetPaper? paper=null,string action="Текст на тетрадном листе")
    {
        var objects=_current.Objects.Where(o=>o is not TextObject { Flow.Notebook:true }).ToImmutableArray().AddRange(parts);
        Commit(_current with { Paper=paper ?? Paper,Objects=objects,NotebookText=story },action);
    }
    public void AddAsset(SourceAsset asset)
    {
        if(_current.Assets.Any(a=>a.Id==asset.Id)) throw new ArgumentException("Исходник с таким ID уже добавлен.");
        Commit(_current with { Assets=_current.Assets.Add(asset) },"Добавить исходник");
    }
    public void AddObject(SheetObject item)
    {
        if(_current.Objects.Any(o=>o.Id==item.Id)) throw new ArgumentException("Объект с таким ID уже добавлен.");
        Commit(_current with { Objects=_current.Objects.Add(Freeze(item)) },"Добавить объект");
    }
    public void AddContent(SourceAsset asset,ContentObject content)
    {
        if(asset.Id!=content.AssetId || _current.Assets.Any(a=>a.Id==asset.Id) || _current.Objects.Any(o=>o.Id==content.Id))
            throw new ArgumentException("Недопустимый исходник или объект вставки.");
        // Import and placement are one user action, including undo and recovery.
        Commit(_current with { Assets=_current.Assets.Add(asset),Objects=_current.Objects.Add(Freeze(content)) },"Вставить изображение");
    }
    public void RemoveObject(Guid id)
    {
        if(_current.Objects.FirstOrDefault(o=>o.Id==id) is TextObject { Flow.Notebook:true })
        { SetNotebookText(null,[],action:"Удалить текст тетради"); return; }
        var index=Index(id); var objects=_current.Objects.RemoveAt(index);
        Commit(_current with { Objects=objects,PrimaryContentId=_current.PrimaryContentId==id ? null : _current.PrimaryContentId },"Удалить объект");
    }
    public void RemoveObjects(IEnumerable<Guid> ids)
    {
        var selected=ids.ToHashSet(); var objects=_current.Objects.Where(o=>!selected.Contains(o.Id)).ToImmutableArray();
        if(objects.Length==_current.Objects.Length) return;
        var notebookRemoved=_current.Objects.OfType<TextObject>().Any(t=>t.Flow?.Notebook==true && selected.Contains(t.Id));
        if(notebookRemoved) objects=objects.Where(o=>o is not TextObject { Flow.Notebook:true }).ToImmutableArray();
        Commit(_current with { Objects=objects,NotebookText=notebookRemoved ? null : _current.NotebookText,
            PrimaryContentId=selected.Contains(_current.PrimaryContentId ?? Guid.Empty) ? null : _current.PrimaryContentId },"Удалить выделенные объекты");
    }
    public void ReplaceObjects(IEnumerable<SheetObject> replacements,string action)
    {
        var changes=replacements.ToDictionary(o=>o.Id); var next=_current.Objects;
        foreach(var item in changes.Values)
        {
            var index=Index(item.Id); if(next[index].GetType()!=item.GetType()) throw new ArgumentException("Тип объекта нельзя менять.");
            next=next.SetItem(index,Freeze(item));
        }
        if(next.SequenceEqual(_current.Objects)) return;
        Commit(_current with { Objects=next },action);
    }
    internal void SetErasedDrawingObjects(ImmutableArray<SheetObject> objects)
        =>Commit(_current with {Objects=objects.Select(Freeze).ToImmutableArray()},"Стереть часть рисунка");
    internal void SetPhotoGrid(SheetPaper paper,PhotoGridPlan plan)
    {
        var selected=plan.OriginalPhotoIds.ToHashSet();var replacements=plan.Photos.ToDictionary(p=>p.Id);
        var objects=ImmutableArray.CreateBuilder<SheetObject>();
        foreach(var item in _current.Objects)
        {
            if(!selected.Contains(item.Id)){objects.Add(item);continue;}
            if(!replacements.TryGetValue(item.Id,out var replacement))throw new ArgumentException("Фотография для размещения не найдена.");
            objects.Add(replacement);
            if(plan.Repeat)objects.AddRange(plan.Photos.Where(p=>p.Id!=item.Id));
        }
        Commit(_current with {Paper=paper,Objects=objects.Select(Freeze).ToImmutableArray()},"Разместить фото по сетке");
    }
    public void AddObjects(IEnumerable<SheetObject> objects)
    {
        var added=objects.Select(Freeze).ToImmutableArray(); if(added.IsEmpty) return;
        Commit(_current with { Objects=_current.Objects.AddRange(added) },"Добавить выделенные объекты");
    }
    public void InsertIntoLayout(SheetObject item,SourceAsset? asset,SheetPaper paper,RectD primaryBounds)
    {
        if(_current.Objects.Any(o=>o.Id==item.Id)) throw new ArgumentException("Объект уже находится на листе.");
        var next=_current;
        if(next.PrimaryContent is { Placement.Mode:PlacementMode.Automatic } primary)
        {
            if(next.PrimaryAsset?.Kind!=SourceKind.Image) throw new InvalidOperationException("Документные страницы доступны только для просмотра.");
            var fixedContent=primary with { Placement=primary.Placement with { BoundsMm=primaryBounds,Mode=PlacementMode.Fixed } };
            next=next with { Paper=paper,Objects=next.Objects.SetItem(Index(primary.Id),fixedContent) };
        }
        if(asset!=null)
        {
            if(item is not ContentObject content || content.AssetId!=asset.Id || next.Assets.Any(a=>a.Id==asset.Id))
                throw new ArgumentException("Недопустимый материал вставки.");
            next=next with { Assets=next.Assets.Add(asset) };
        }
        // Freezing the original layout and inserting the new object form one undo step.
        // The original asset and its crop, rotation and annotations are never baked into a preview.
        Commit(next with { Objects=next.Objects.Add(Freeze(item)) },"Вставить на лист");
    }
    public void ReplaceObject(SheetObject item,string action="Свойства объекта")
    {
        var index=Index(item.Id); if(_current.Objects[index].GetType()!=item.GetType()) throw new ArgumentException("Тип объекта нельзя менять заменой свойств.");
        if(_current.Objects[index]==item) return;
        Commit(_current with { Objects=_current.Objects.SetItem(index,Freeze(item)) },action);
    }
    public void PlacePrimaryImage(SheetPaper paper,RectD bounds)
    {
        if(_current.PrimaryAsset?.Kind!=SourceKind.Image || _current.PrimaryContent is not { Locked:false,Visible:true } primary)
            throw new InvalidOperationException("Для размещения выберите доступное изображение.");
        var content=primary with { Placement=primary.Placement with { BoundsMm=bounds,Mode=PlacementMode.Fixed } };
        Commit(_current with { Paper=paper,Objects=_current.Objects.SetItem(Index(primary.Id),content) },"Разместить фото на листе");
    }
    public void InsertBatchIntoLayout(IEnumerable<SheetObject> items,IEnumerable<SourceAsset> assets,SheetPaper paper,RectD primaryBounds)
    {
        var added=items.Select(Freeze).ToImmutableArray(); var materials=assets.ToImmutableArray();
        if(added.IsEmpty) return;
        if(added.Select(o=>o.Id).Distinct().Count()!=added.Length || added.Any(o=>_current.Objects.Any(old=>old.Id==o.Id)))
            throw new ArgumentException("Повторяющиеся объекты.");
        if(materials.Select(a=>a.Id).Distinct().Count()!=materials.Length || materials.Any(a=>_current.Assets.Any(old=>old.Id==a.Id)))
            throw new ArgumentException("Повторяющиеся материалы.");
        var next=_current;
        if(next.PrimaryContent is { Placement.Mode:PlacementMode.Automatic } primary)
        {
            if(next.PrimaryAsset?.Kind!=SourceKind.Image) throw new InvalidOperationException("Документные страницы доступны только для просмотра.");
            next=next with { Paper=paper,Objects=next.Objects.SetItem(Index(primary.Id),primary with { Placement=primary.Placement with { BoundsMm=primaryBounds,Mode=PlacementMode.Fixed } }) };
        }
        Commit(next with { Assets=next.Assets.AddRange(materials),Objects=next.Objects.AddRange(added) },"Вставить группу на лист");
    }
    public void SetPlacement(Guid id,ObjectPlacement placement)=>ReplaceObject(_current.Objects[Index(id)] with { Placement=placement },"Размещение объекта");
    public void MoveObject(Guid id,double dxMm,double dyMm)
    {
        var item=_current.Objects[Index(id)]; var bounds=item.Placement.BoundsMm;
        SetPlacement(id,item.Placement with { BoundsMm=bounds with { X=bounds.X+dxMm,Y=bounds.Y+dyMm },Mode=PlacementMode.Fixed });
    }
    public void ResizeObject(Guid id,SizeD sizeMm)
    {
        var item=_current.Objects[Index(id)];
        SetPlacement(id,item.Placement with { BoundsMm=item.Placement.BoundsMm with { Width=sizeMm.Width,Height=sizeMm.Height },Mode=PlacementMode.Fixed });
    }
    public void RotateObject(Guid id,double degrees)
    {
        var item=_current.Objects[Index(id)]; SetPlacement(id,item.Placement with { RotationDegrees=degrees });
    }
    public void SetObjectOrder(Guid id,int index)
    {
        var old=Index(id); if(index<0 || index>=_current.Objects.Length) throw new ArgumentOutOfRangeException(nameof(index)); if(old==index) return;
        var item=_current.Objects[old]; var objects=_current.Objects.RemoveAt(old).Insert(index,item);
        Commit(_current with { Objects=objects },"Порядок объектов");
    }
    public void SetObjectsOrder(IEnumerable<Guid> ids,int direction)
    {
        var selected=ids.ToHashSet();
        var objects=Services.SelectionOrder.Move(_current.Objects,o=>selected.Contains(o.Id) && o.Visible && !o.Locked,direction);
        if(objects.SequenceEqual(_current.Objects)) return;
        Commit(_current with { Objects=objects },"Порядок выделенных объектов");
    }
    public void SetContentEdits(Guid id,ContentEdits edits,string action)
    {
        if(_current.Objects[Index(id)] is not ContentObject content) throw new ArgumentException("У объекта нет правок исходного материала.");
        ReplaceObject(content with { Edits=Freeze(edits) },action);
    }
    public void SetContentRotation(int quarterTurns)
    {
        if(PrimaryContent is not {} content) return;
        var turns=(quarterTurns%4+4)%4; if(content.Edits.QuarterTurns==turns) return;
        SetContentEdits(content.Id,content.Edits with { QuarterTurns=turns },"Поворот исходника");
    }
    public void Undo()
    {
        if(IsEditing) return;
        if(!_undo.TryPop(out var entry)) return; var before=_current; _redo.Push((_current,entry.Action)); _current=entry.State;
        Changed?.Invoke(this,new(before,_current,"Отменить: "+entry.Action)); Notify();
    }
    public void Redo()
    {
        if(IsEditing) return;
        if(!_redo.TryPop(out var entry)) return; var before=_current; _undo.Push((_current,entry.Action)); _current=entry.State;
        Changed?.Invoke(this,new(before,_current,"Повторить: "+entry.Action)); Notify();
    }
    private int Index(Guid id)
    {
        for(var i=0;i<_current.Objects.Length;i++) if(_current.Objects[i].Id==id) return i;
        throw new KeyNotFoundException("Объект не найден.");
    }
    private void Commit(SheetSnapshot next,string action)
    {
        if(next==_current) return; Validate(next);
        if(_editBefore==null) { _undo.Push((_current,action)); _redo.Clear(); }
        var before=_current; _current=next;
        if(_undo.Count>100) { var keep=_undo.Take(100).Reverse().ToArray(); _undo.Clear(); foreach(var entry in keep) _undo.Push(entry); }
        if(_editBefore==null) Changed?.Invoke(this,new(before,_current,action));
        Notify();
    }
    public void BeginEdit(string action)
    {
        if(IsEditing) throw new InvalidOperationException("Редактирование листа уже начато.");
        _editBefore=_current; _editAction=action; Notify();
    }
    public void EndEdit(bool commit)
    {
        if(_editBefore==null) return;
        var before=_editBefore; _editBefore=null;
        if(commit && before!=_current) { _undo.Push((before,_editAction ?? "Редактировать текст")); _redo.Clear(); }
        else if(!commit) _current=before;
        var action=_editAction ?? "Редактировать текст"; _editAction=null;
        if(_undo.Count>100) { var keep=_undo.Take(100).Reverse().ToArray(); _undo.Clear(); foreach(var entry in keep) _undo.Push(entry); }
        if(commit && before!=_current) Changed?.Invoke(this,new(before,_current,action));
        Notify();
    }
    internal void RestoreHistoryState(SheetSnapshot snapshot)
    {
        if(snapshot.Id!=Id || IsEditing) throw new InvalidOperationException("Нельзя восстановить состояние другого или редактируемого листа.");
        if(snapshot==_current) return;
        Validate(snapshot); _current=snapshot; _undo.Clear(); _redo.Clear(); Notify();
    }
    private void Notify()
    {
        Revision++; foreach(var name in new[] { nameof(Revision),nameof(Paper),nameof(Objects),nameof(NotebookText),nameof(PrimaryAsset),nameof(PrimaryContent),nameof(CanUndo),nameof(CanRedo),nameof(UndoAction),nameof(RedoAction) }) OnPropertyChanged(name);
    }
    private static Annotation Freeze(Annotation a)=>a with { Points=a.Points.ToImmutableArray() };
    private static ContentEdits Freeze(ContentEdits edits)=>edits with { Annotations=edits.Annotations.Select(Freeze).ToImmutableArray() };
    private static SheetObject Freeze(SheetObject item)=>item is ContentObject content ? content with { Edits=Freeze(content.Edits) } : item;
    private static void Validate(SheetSnapshot state)
    {
        state.Paper.Validate();
        foreach(var asset in state.Assets)
        {
            if(asset.Id==Guid.Empty || !Enum.IsDefined(asset.Kind) || asset.Kind==SourceKind.Blank || asset.PageIndex<0 || asset.PageCount<=asset.PageIndex) throw new ArgumentException("Недопустимый исходник.");
            SheetValidation.Size(asset.LogicalSize,nameof(asset)); SheetValidation.Size(asset.PhysicalSizeMm,nameof(asset));
        }
        if(state.Assets.Select(a=>a.Id).Distinct().Count()!=state.Assets.Length || state.Objects.Select(o=>o.Id).Distinct().Count()!=state.Objects.Length)
            throw new ArgumentException("ID объектов и исходников должны быть уникальными.");
        if(state.Objects.OfType<MarkdownObject>().Count()>1 || state.Objects.OfType<MarkdownObject>().Any() && state.NotebookText!=null)
            throw new ArgumentException("На одном листе размещается один документ Markdown. Тетрадный текст с продолжением создавайте на отдельном листе.");
        if(state.Objects.OfType<TextObject>().Count(t=>t.Flow is { Notebook:false })>1) throw new ArgumentException("На одном листе допустим один фрагмент автоматического продолжения.");
        if(state.NotebookText is {} story)
        {
            if(state.Paper.Notebook==null || story.Id==Guid.Empty || story.Source==null || story.Source.Length>TextLimits.MaxCharacters)
                throw new ArgumentException("Недопустимый текст тетради.");
            Services.TextLayoutService.ValidateStyle(story.Style);
            var parts=state.Objects.OfType<TextObject>().Where(t=>t.Flow?.Notebook==true).OrderBy(t=>t.Flow!.PartIndex).ToArray();
            if(parts.Length>2 || parts.Any(t=>t.Flow!.StoryId!=story.Id) || parts.Select(t=>t.Flow!.PartIndex).Distinct().Count()!=parts.Length
                || !story.Source.StartsWith(string.Concat(parts.Select(t=>t.Text)),StringComparison.Ordinal))
                throw new ArgumentException("Фрагменты текста тетради не совпадают с исходным текстом.");
        }
        else if(state.Objects.OfType<TextObject>().Any(t=>t.Flow?.Notebook==true)) throw new ArgumentException("Отсутствует исходный текст тетради.");
        foreach(var item in state.Objects)
        {
            if(item is MarkdownObject markdown)
            {
                if(markdown.DocumentId==Guid.Empty || markdown.PageIndex<0 || markdown.PageIndex>=TextLimits.MaxPages)
                    throw new ArgumentException("Недопустимая страница Markdown.");
                if(markdown.Document is {} definition)
                {
                    Services.ProjectFileService.ValidateMarkdownDocument(definition);
                    if(definition.Id!=markdown.DocumentId || definition.Images.Any(image=>!state.Assets.Any(a=>a.Id==image.AssetId && a.Kind==SourceKind.Image)))
                        throw new ArgumentException("Материалы Markdown не совпадают с документом.");
                }
            }
            if(item is TextObject checkedText) Services.TextLayoutService.ValidateStyle(checkedText.Style);
            if(item.Id==Guid.Empty) throw new ArgumentException("Объекту нужен ID."); SheetValidation.Placement(item.Placement);
            if(!Enum.IsDefined(item.Placement.Mode)) throw new ArgumentException("Неизвестный режим размещения.");
            if(item is ContentObject content)
            {
                var source=state.Assets.FirstOrDefault(a=>a.Id==content.AssetId) ?? throw new ArgumentException("Исходник объекта не найден.");
                var crop=content.Edits.Crop; var full=new RectD(0,0,source.LogicalSize.Width,source.LogicalSize.Height);
                if(!double.IsFinite(crop.X) || !double.IsFinite(crop.Y) || !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height)
                    || crop.IsEmpty || crop.Intersect(full)!=crop || content.Edits.QuarterTurns is <0 or >3) throw new ArgumentException("Недопустимый кроп/поворот.");
                foreach(var annotation in content.Edits.Annotations)
                {
                    ValidateColor(annotation.Color);
                    if(annotation.FillColor!=null) ValidateColor(annotation.FillColor);
                    if(annotation.Opacity is {} opacity && (!double.IsFinite(opacity) || opacity<0 || opacity>1)) throw new ArgumentException("Непрозрачность аннотации должна быть от 0 до 1.");
                    if(!Enum.IsDefined(annotation.Kind) || !double.IsFinite(annotation.Thickness) || annotation.Thickness<=0 || !double.IsFinite(annotation.FontSize) || annotation.FontSize<=0
                        || !Finite(annotation.Start) || !Finite(annotation.End) || annotation.Points.Any(p=>!Finite(p))) throw new ArgumentException("Недопустимая аннотация.");
                    if(annotation.TextFormat!=null)
                    {
                        var layoutScale=Services.AnnotationTextLayout.LayoutScale(annotation);
                        if(annotation.Kind!=AnnotationKind.Text || annotation.Text.Length>TextLimits.MaxCharacters || !double.IsFinite(layoutScale) || layoutScale<=0 || layoutScale>10000) throw new ArgumentException("Недопустимое оформление аннотации.");
                        Services.TextLayoutService.ValidateStyle(Services.AnnotationTextLayout.Style(annotation));
                    }
                }
            }
            if(item is TextObject text && (string.IsNullOrWhiteSpace(text.Style.FontFamily) || !double.IsFinite(text.Style.FontSizePt) || text.Style.FontSizePt<=0 || text.Style.FontSizePt>1000))
                throw new ArgumentException("Недопустимый шрифт.");
            if(item is TextObject styled && (!double.IsFinite(styled.Style.LineSpacing) || styled.Style.LineSpacing<1 || styled.Style.LineSpacing>3 || styled.Text.Length>TextLimits.MaxCharacters))
                throw new ArgumentException("Недопустимый интервал или слишком большой текст.");
            if(item is TextObject { Flow:{} flow } && (flow.StoryId==Guid.Empty || flow.PartIndex<0 || flow.PartIndex>=TextLimits.MaxPages))
                throw new ArgumentException("Недопустимая связь продолжений текста.");
            var stroke=item is ShapeObject shape ? shape.Style : item is InkObject ink ? ink.Style : null;
            if(stroke!=null && (!double.IsFinite(stroke.WidthMm) || stroke.WidthMm<=0 || !double.IsFinite(stroke.Opacity) || stroke.Opacity<0 || stroke.Opacity>1)) throw new ArgumentException("Недопустимый штрих.");
            if(stroke!=null) { ValidateColor(stroke.Color); if(stroke.FillColor!=null) ValidateColor(stroke.FillColor); }
            if(item is TextObject caption) { ValidateColor(caption.Style.Color); if(!Enum.IsDefined(caption.Style.Alignment)) throw new ArgumentException("Недопустимое выравнивание текста."); }
            if(item is ShapeObject figure && !Enum.IsDefined(figure.Shape)) throw new ArgumentException("Неизвестная фигура.");
            if(item is InkObject drawing && (drawing.Points.IsDefaultOrEmpty || drawing.Points.Any(p=>!Finite(p) || p.X<0 || p.X>1 || p.Y<0 || p.Y>1))) throw new ArgumentException("Штриху нужны точки в пределах 0..1.");
        }
        if(state.PrimaryContentId!=null && state.PrimaryContent==null) throw new ArgumentException("Основной объект не найден.");
    }
    private static bool Finite(PointD p)=>double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static void ValidateColor(string color)
    {
        if(string.IsNullOrWhiteSpace(color)) throw new ArgumentException("Нужен цвет.",nameof(color));
        try { _=System.Drawing.ColorTranslator.FromHtml(color); }
        catch(Exception ex) when(ex is ArgumentException or FormatException) { throw new ArgumentException("Недопустимый цвет.",nameof(color),ex); }
    }
}
