using CommunityToolkit.Mvvm.Input;
using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PrintMaster.Views;

public partial class MainWindow
{
    private bool _imageCropOpen;
    private bool WorkMenuIsOpen=>QueueList.ContextMenu?.IsOpen==true || SheetEditor.ContextMenu?.IsOpen==true || Editor.ContextMenu?.IsOpen==true || CanvasCommands.IsOverflowOpen || DrawingToolbar.IsOverflowOpen;
    private void CloseWorkMenus()
    {
        CanvasCommands.IsOverflowOpen=false; DrawingToolbar.IsOverflowOpen=false;
        foreach(var host in new FrameworkElement[] {QueueList,SheetEditor,Editor})
            if(host.ContextMenu is {} menu) menu.IsOpen=false;
    }
    internal void SelectQueueContextTarget(PrintPageItem? page)
    {
        if(!ViewModel.IsIdle) return;
        var targetId=page?.Id;var highlightedIds=ViewModel.HighlightedPages.Select(p=>p.Id).ToHashSet();
        CommitFocusedField(); ViewModel.FinishTextEditing();
        // Committing a text flow replaces its page models and may change selection.
        // Keep the right-clicked identity and the group from before that commit.
        var live=targetId==null ? null : ViewModel.Pages.FirstOrDefault(p=>p.Id==targetId);
        var group=live==null ? [] : highlightedIds.Contains(live.Id)
            ? ViewModel.Pages.Where(p=>highlightedIds.Contains(p.Id)).ToArray() : new[] {live};
        ViewModel.SetQueueSelection(group,live ?? ViewModel.SelectedPage);
    }
    private void QueueRightButtonDown(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is not DependencyObject source || Ancestor<TextBoxBase>(source)!=null) return;
        var container=Ancestor<ListBoxItem>(source);
        e.Handled=true; // ListBox must not collapse a group during its own right-button handling.
        var target=container?.DataContext as PrintPageItem;
        SelectQueueContextTarget(target);
        // The commit above can replace the clicked page and its visual container.
        var live=target==null ? null : ViewModel.Pages.FirstOrDefault(p=>p.Id==target.Id);
        var liveContainer=live==null ? null : QueueList.ItemContainerGenerator.ContainerFromItem(live) as ListBoxItem;
        _syncingQueue=true;
        try { if(liveContainer==null || !liveContainer.Focus()) QueueList.Focus(); }
        finally { _syncingQueue=false; }
    }
    private void QueueContextMenuOpening(object sender,ContextMenuEventArgs e)
    {
        if(e.OriginalSource is DependencyObject source && Ancestor<TextBoxBase>(source)!=null) return;
        if(!ViewModel.IsIdle) { e.Handled=true; return; }
        var menu=PrepareQueueContextMenu();
        if(e.CursorLeft>=0) { menu.Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint; menu.HorizontalOffset=menu.VerticalOffset=0; }
    }
    internal ContextMenu PrepareQueueContextMenu()
    {
        CommitFocusedField(); ViewModel.FinishTextEditing();
        var menu=QueueList.ContextMenu!; ConfigureContextMenu(menu); menu.PlacementTarget=QueueList; menu.Items.Clear();
        var ids=ViewModel.HighlightedPages.Select(p=>p.Id).ToArray();
        bool current()=>ViewModel.IsIdle && ids.SequenceEqual(ViewModel.HighlightedPages.Select(p=>p.Id));
        if(ids.Length>0)
        {
            MenuAction(menu,"Include","Включить в печать",()=>ViewModel.SetHighlightedPrintingCommand.Execute(true),()=>current() && ViewModel.HighlightedPages.Any(p=>!p.IsIncluded));
            MenuAction(menu,"Exclude","Исключить из печати",()=>ViewModel.SetHighlightedPrintingCommand.Execute(false),()=>current() && ViewModel.HighlightedPages.Any(p=>p.IsIncluded));
            MenuAction(menu,"Copies","Количество копий…",()=>SetContextCopies(ids),current);
            MenuAction(menu,"PageOptions","Настройки печати…",()=>ViewModel.EditPageOptionsCommand.Execute("selection"),current);
            MenuAction(menu,"Rotate","Повернуть на 90°",()=>ViewModel.RotateHighlightedPagesCommand.Execute(null),current);
            var move=Submenu(menu,"Move","Переместить");
            MenuAction(move,"MoveUp","Выше",()=>ViewModel.MoveHighlightedPagesCommand.Execute(-1),()=>current() && ViewModel.MoveHighlightedPagesCommand.CanExecute(-1),"Alt+↑");
            MenuAction(move,"MoveDown","Ниже",()=>ViewModel.MoveHighlightedPagesCommand.Execute(1),()=>current() && ViewModel.MoveHighlightedPagesCommand.CanExecute(1),"Alt+↓");
            menu.Items.Add(new Separator());
            RoutedMenuAction(menu,"Cut","Вырезать",ApplicationCommands.Cut,QueueList,"Ctrl+X");
            RoutedMenuAction(menu,"Copy","Копировать",ApplicationCommands.Copy,QueueList,"Ctrl+C");
        }
        RoutedMenuAction(menu,"Paste","Вставить страницы",ApplicationCommands.Paste,QueueList,"Ctrl+V");
        if(ids.Length>0)
        {
            MenuAction(menu,"Duplicate","Дублировать",()=>ViewModel.DuplicateHighlightedPagesCommand.Execute(null),current,"Ctrl+D");
            MenuAction(menu,"Delete","Удалить",()=>ViewModel.DeleteHighlightedPagesCommand.Execute(null),current,"Del");
        }
        else
        {
            menu.Items.Add(new Separator());
            MenuAction(menu,"AddFiles","Добавить файлы…",()=>ViewModel.AddFilesCommand.Execute(null),()=>ViewModel.IsIdle,"Ctrl+O");
            MenuAction(menu,"CreateSheet","Создать лист…",()=>ViewModel.CreateSheetCommand.Execute(null),()=>ViewModel.IsIdle,"Ctrl+N");
        }
        return menu;
    }
    private void SetContextCopies(Guid[] ids)
    {
        var pages=ViewModel.HighlightedPages;
        var dialog=new QueueCopiesDialog(ids.Length,pages.Select(p=>p.Copies).Distinct().Count()==1 ? pages[0].Copies : 1) { Owner=this };
        if(dialog.ShowDialog()!=true || !ids.SequenceEqual(ViewModel.HighlightedPages.Select(p=>p.Id))) return;
        ViewModel.BatchCopies=dialog.Copies; ViewModel.SetHighlightedCopiesCommand.Execute(null);
    }
    private void CanvasContextMenuOpening(object sender,ContextMenuEventArgs e)
    {
        if(!ViewModel.IsIdle || sender is not FrameworkElement canvas) { e.Handled=true; return; }
        var menu=PrepareCanvasContextMenu(canvas,e.CursorLeft<0 || e.CursorTop<0 ? null : new Point(e.CursorLeft,e.CursorTop));
        if(e.CursorLeft>=0) { menu.Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint; menu.HorizontalOffset=menu.VerticalOffset=0; }
    }
    internal ContextMenu PrepareCanvasContextMenu(FrameworkElement canvas,Point? location=null)
    {
        CommitFocusedField(); ViewModel.FinishTextEditing();
        if(location is {} p)
        {
            if(canvas is SheetCanvas sheet) sheet.SelectClipboardAt(p);
            else if(canvas is AnnotationCanvas source) source.SelectClipboardAt(p);
        }
        if(ViewModel.CanEdit) ViewModel.Tool=EditorTool.Select;
        canvas.Focus(); var page=ViewModel.SelectedPage;
        var menu=canvas.ContextMenu!; ConfigureContextMenu(menu); menu.PlacementTarget=canvas; menu.Items.Clear();
        bool current()=>ViewModel.IsIdle && page!=null && ViewModel.SelectedPage==page;
        bool editable()=>current() && ViewModel.CanEdit;
        var sheetMode=canvas==SheetEditor;
        var hasObject=sheetMode ? ViewModel.SelectedObjects.Count>0 : Editor.WholeImageSelected || Editor.SelectedAnnotationIds.Length>0;
        if(sheetMode && location is {} hitPoint && editable())
        {
            var hits=SheetEditor.ObjectsAt(new(hitPoint.X/SheetEditing.DipPerMm,hitPoint.Y/SheetEditing.DipPerMm));
            if(hits.Count>1)
            {
                var pick=Submenu(menu,"SelectUnder","Объекты под курсором");
                foreach(var item in hits)
                {
                    var label=item switch { TextObject text=>"Текст: "+text.Text[..Math.Min(30,text.Text.Length)],ContentObject=>"Фото",ShapeObject shape=>shape.Shape switch { SheetShapeKind.Arrow=>"Стрелка",SheetShapeKind.Ellipse=>"Эллипс",_=>"Прямоугольник" },_=>"Штрих" };
                    var layer=page!.Document.Objects.ToList().FindIndex(o=>o.Id==item.Id)+1;
                    MenuAction(pick,"Select_"+item.Id.ToString("N"),$"{label} · слой {layer}",()=>ViewModel.SetObjectSelection([item.Id]),()=>editable() && page!.Document.Objects.Any(o=>o.Id==item.Id && o.Visible && !o.Locked));
                }
                menu.Items.Add(new Separator());
            }
        }
        if(hasObject && ViewModel.CanEdit)
        {
            RoutedMenuAction(menu,"Cut","Вырезать",ApplicationCommands.Cut,canvas,"Ctrl+X");
            RoutedMenuAction(menu,"Copy","Копировать",ApplicationCommands.Copy,canvas,"Ctrl+C");
        }
        if(ViewModel.CanEdit)
        {
            RoutedMenuAction(menu,"Paste","Вставить",ApplicationCommands.Paste,canvas,"Ctrl+V");
            if(ViewModel.CanArrangePhotoGrid)
                MenuAction(menu,"PhotoGrid","Фото по сетке…",()=>ViewModel.ArrangePhotoGridCommand.Execute(null),()=>editable() && ViewModel.CanArrangePhotoGrid);
            if(hasObject)
            {
                MenuAction(menu,"Duplicate","Дублировать",()=>{ if(sheetMode) ViewModel.DuplicateSheetSelection(); else DuplicateSourceSelection(); },editable,"Ctrl+D");
                RoutedMenuAction(menu,"Delete","Удалить",ApplicationCommands.Delete,canvas,"Del");
                menu.Items.Add(new Separator());
                if(sheetMode && ViewModel.SelectedSheetObject is TextObject)
                    MenuAction(menu,"EditText","Редактировать текст",()=>ViewModel.EditSelectedTextCommand.Execute(null),()=>editable() && ViewModel.EditSelectedTextCommand.CanExecute(null));
                else if(!sheetMode && !Editor.WholeImageSelected && Editor.SelectedAnnotation is { Kind:AnnotationKind.Text } annotation)
                    MenuAction(menu,"EditText","Редактировать текст",()=>EditText(Editor,new(annotation.Start,annotation)),editable);
                var content=sheetMode ? ViewModel.SelectedSheetObject as ContentObject : Editor.WholeImageSelected ? page?.Document.PrimaryContent : null;
                if(sheetMode && content is { Locked:false } sourcePhoto && sourcePhoto.Edits.Annotations.Any(a=>a.Kind==AnnotationKind.Text))
                {
                    var captions=Submenu(menu,"ImageCaptions","Надписи фотографии");
                    foreach(var caption in sourcePhoto.Edits.Annotations.Where(a=>a.Kind==AnnotationKind.Text))
                    {
                        var label=caption.Text.Replace('\n',' ').Replace('\r',' ');
                        if(label.Length>40) label=label[..40]+"…";
                        MenuAction(captions,"Caption_"+caption.Id.ToString("N"),label.Length==0 ? "Пустой текст" : label,
                            ()=>ViewModel.BeginAnnotationTextEditing(caption.Start,caption,sourcePhoto.Id),editable);
                    }
                }
                if(content!=null && page?.Document.Snapshot().Assets.Any(a=>a.Id==content.AssetId && a.Kind==SourceKind.Image)==true)
                    MenuAction(menu,"Crop","Кадрировать фото…",()=>ShowImageCrop(page!,content.Id),()=>CanCropImageTarget(page!,content.Id),"C");
                if(!sheetMode && Editor.WholeImageSelected)
                    MenuAction(menu,"PlaceImage","Разместить фото на листе",()=>ViewModel.PlaceSourceImageCommand.Execute(null),()=>editable() && ViewModel.CanPlaceSourceImage);
                if(sheetMode || !Editor.WholeImageSelected) AddObjectLayoutMenus(menu,sheetMode,editable);
            }
            else
            {
                menu.Items.Add(new Separator());
                MenuAction(menu,"AddImage","Добавить фото…",()=>ViewModel.InsertSheetImagesCommand.Execute(null),()=>editable() && ViewModel.InsertSheetImagesCommand.CanExecute(null));
                var anchor=location is {} point ? new PointD(point.X/SheetEditing.DipPerMm,point.Y/SheetEditing.DipPerMm)
                    : new PointD(ViewModel.SafeAreaMm.X+5,ViewModel.SafeAreaMm.Y+5);
                MenuAction(menu,"AddText","Добавить текст",()=>ViewModel.BeginTextEditing(anchor),editable);
            }
        }
        else if(current() && ViewModel.ShowPrintPreview && (page!.IsImage || page.IsSheetLayout) && !page.HasMissingSources)
            MenuAction(menu,"EditMode","Вернуться к редактированию",()=>ViewModel.ShowPrintPreview=false,current);
        if(page!=null)
        {
            if(menu.Items.Count>0) menu.Items.Add(new Separator());
            MenuAction(menu,"Properties",hasObject ? "Свойства объекта" : "Свойства листа",ShowContextProperties,current);
            MenuAction(menu,"Fit","Показать лист целиком",()=>ViewModel.FitZoomCommand.Execute(null),current);
        }
        return menu;
    }
    private void AddObjectLayoutMenus(ContextMenu menu,bool sheetMode,Func<bool> editable)
    {
        var align=Submenu(menu,"Align",sheetMode ? "Выровнять по печатной области" : "Выровнять по области фото");
        foreach(var (key,label) in new[] {("left","По левому краю"),("center","По центру по горизонтали"),("right","По правому краю"),("top","По верхнему краю"),("middle","По центру по вертикали"),("bottom","По нижнему краю")})
            MenuAction(align,"Align_"+key,label,()=>{ if(sheetMode) ViewModel.AlignSheetSelectionCommand.Execute(key); else Editor.AlignSelection(key); },editable);
        if(sheetMode)
        {
            align.Items.Add(new Separator());
            foreach(var (key,label) in new[] {("center-both","По центру листа"),("top-left","Верхний левый угол"),("top-right","Верхний правый угол"),("bottom-left","Нижний левый угол"),("bottom-right","Нижний правый угол")})
                MenuAction(align,"Align_"+key.Replace('-','_'),label,()=>ViewModel.AlignSheetSelectionCommand.Execute(key),editable);
        }
        var order=Submenu(menu,"Order","Порядок наложения");
        foreach(var (direction,key,label) in new[] {(1,"Forward","На слой вперед"),(-1,"Backward","На слой назад"),(2,"Front","На передний план"),(-2,"Back","На задний план")})
            MenuAction(order,"Order"+key,label,()=>{ if(sheetMode) ViewModel.OrderSheetSelectionCommand.Execute(direction); else Editor.OrderSelection(direction); },
                ()=>editable() && (sheetMode ? ViewModel.OrderSheetSelectionCommand.CanExecute(direction) : Editor.CanOrderSelection(direction)));
    }
    private bool CanCropImageTarget(PrintPageItem page,Guid id)
    {
        if(!ViewModel.CanEdit || ViewModel.SelectedPage!=page || page.Document.Objects.FirstOrDefault(o=>o.Id==id) is not ContentObject { Locked:false,Visible:true } content) return false;
        return page.Document.Snapshot().Assets.Any(a=>a.Id==content.AssetId && a.Kind==SourceKind.Image && !string.IsNullOrEmpty(a.WorkingPath) && System.IO.File.Exists(a.WorkingPath));
    }
    private void OpenSourceImageCrop(object? sender,EventArgs e)
    {
        if(ViewModel.CanCropSelection && ViewModel.SelectedPage is {} page && (ViewModel.IsSheetSelected ? ViewModel.SelectedSheetObject : page.Document.PrimaryContent) is ContentObject content) ShowImageCrop(page,content.Id);
    }
    private bool TryCropShortcut(ShortcutScope scope)
    {
        if(scope is not (ShortcutScope.Source or ShortcutScope.Sheet) || !ViewModel.CanCropSelection) return false;
        ViewModel.SetToolCommand.Execute(EditorTool.Crop); return true;
    }
    private void ShowImageCrop(PrintPageItem page,Guid id)
    {
        if(_imageCropOpen || !CanCropImageTarget(page,id)) return;
        CommitFocusedField(); ViewModel.FinishTextEditing();
        if(!CanCropImageTarget(page,id)) return;
        CloseWorkMenus();
        var snapshot=page.Document.Snapshot(); var content=(ContentObject)snapshot.Objects.First(o=>o.Id==id);
        var asset=snapshot.Assets.First(a=>a.Id==content.AssetId);
        var dialog=new ImageCropDialog(asset,content.Edits) { Owner=this };
        _imageCropOpen=true;
        try
        {
            if(dialog.ShowDialog()==true && CanCropImageTarget(page,id) && page.Document.Snapshot()==snapshot)
                ViewModel.ApplyImageCrop(page,id,dialog.ResultCrop);
        }
        finally
        {
            _imageCropOpen=false;
            if(ViewModel.SelectedPage==page) (ViewModel.IsSheetSelected ? (FrameworkElement)SheetEditor : Editor).Focus();
        }
    }
    private void ShowContextProperties()
    {
        _inspectorVisible=true;
        if(WorkspaceRoot.ActualWidth<1080) _queueVisible=false;
        ApplyWorkspacePanels(); InspectorPane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private static MenuItem Submenu(ItemsControl menu,string key,string header)
    { var item=new MenuItem { Name=key,Header=header,Icon=CommandIcon(key) }; menu.Items.Add(item); return item; }
    private static void ConfigureContextMenu(ContextMenu menu)
    {
        if(menu.Resources.Contains(typeof(MenuItem))) return;
        var style=new Style(typeof(MenuItem),menu.TryFindResource(typeof(MenuItem)) as Style);
        var disabled=new Trigger { Property=IsEnabledProperty,Value=false };
        // The application's text style fixes header foreground; opacity keeps disabled entries visibly distinct.
        disabled.Setters.Add(new Setter(OpacityProperty,0.45)); style.Triggers.Add(disabled);
        menu.Resources[typeof(MenuItem)]=style;
    }
    private void MenuAction(ItemsControl menu,string key,string header,Action execute,Func<bool> canExecute,string gesture="")
    {
        var item=new MenuItem { Name=key,Header=header,Icon=CommandIcon(key),InputGestureText=gesture,Command=new RelayCommand(()=>{ if(canExecute()) execute(); },canExecute) };
        ToolTipService.SetShowOnDisabled(item,true);
        string Hint()=>canExecute() ? header : key switch
        {
            "MoveUp"=>ViewModel.QueueMoveUpActionAvailability,"MoveDown"=>ViewModel.QueueMoveDownActionAvailability,
            "AddImage"=>ViewModel.InsertImageActionAvailability,
            _ when key.StartsWith("Order")=>"Выбранные объекты уже у границы порядка наложения или недоступны для изменения.",
            _=>CurrentShortcutScope==ShortcutScope.Queue ? ViewModel.QueueSelectionActionAvailability : ViewModel.EditorActionAvailability
        };
        void Refresh(){var reason=Hint();item.ToolTip=reason;System.Windows.Automation.AutomationProperties.SetHelpText(item,reason);}
        Refresh(); item.ToolTipOpening+=(_,_)=>Refresh(); menu.Items.Add(item);
    }
    private static void RoutedMenuAction(ItemsControl menu,string key,string header,RoutedCommand command,FrameworkElement target,string gesture)
    { menu.Items.Add(new MenuItem { Name=key,Header=header,Icon=CommandIcon(key),CommandTarget=target,Command=command,InputGestureText=gesture }); }
    internal bool OpenWorkContextMenu()
    {
        if(!ViewModel.IsIdle) return false;
        var scope=CurrentShortcutScope;
        if(scope is ShortcutScope.Text or ShortcutScope.None) return false;
        ContextMenu menu; Point point;
        if(scope==ShortcutScope.Queue)
        {
            menu=PrepareQueueContextMenu();
            var container=QueueList.ItemContainerGenerator.ContainerFromItem(ViewModel.SelectedPage!) as FrameworkElement;
            point=container?.TranslatePoint(new Point(24,Math.Min(30,container.ActualHeight/2)),QueueList) ?? new Point(24,24);
        }
        else
        {
            var canvas=ViewModel.IsSheetSelected ? (FrameworkElement)SheetEditor : Editor;
            menu=PrepareCanvasContextMenu(canvas);
            RectD? b=ViewModel.IsSheetSelected && ViewModel.SelectedObjects.Count>0 ? SheetEditing.VisualBounds(ViewModel.SelectedObjects[0]) : null;
            point=b is {} bounds ? new(Math.Max(8,bounds.X*SheetEditing.DipPerMm),Math.Max(8,bounds.Y*SheetEditing.DipPerMm)) : canvas==Editor ? Editor.SelectionMenuPoint : new(24,24);
            var inViewport=canvas.TranslatePoint(point,CanvasScroll);
            point=CanvasScroll.TranslatePoint(new Point(Math.Clamp(inViewport.X,8,Math.Max(8,CanvasScroll.ViewportWidth-8)),Math.Clamp(inViewport.Y,8,Math.Max(8,CanvasScroll.ViewportHeight-8))),canvas);
        }
        menu.Placement=System.Windows.Controls.Primitives.PlacementMode.RelativePoint; menu.HorizontalOffset=point.X; menu.VerticalOffset=point.Y; menu.IsOpen=true; return true;
    }
}
