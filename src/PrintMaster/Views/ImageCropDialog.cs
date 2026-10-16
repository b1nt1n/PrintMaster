using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintMaster.Views;

public sealed class ImageCropDialog : Window
{
    private readonly CancellationTokenSource _cancel=new();
    private readonly NumericField[] _fields;
    private Task? _loading;
    internal CropEditorViewModel ViewModel { get; }
    internal AnnotationCanvas CropEditor { get; }
    internal RectD ResultCrop=>ViewModel.ResultCrop;
    public ImageCropDialog(SourceAsset asset,ContentEdits edits)
    {
        ViewModel=new(asset,edits); DataContext=ViewModel;
        Title="Кадрирование фото"; Width=860; Height=740; MinWidth=420; MinHeight=440;
        WindowStartupLocation=WindowStartupLocation.CenterOwner; ShowInTaskbar=false;
        if(TryFindResource(typeof(Window)) is Style style) Style=style;
        var grid=new Grid(); Content=new Border { Padding=new Thickness(16),Background=TryFindResource("Background") as Brush ?? Brushes.White,Child=grid };
        grid.RowDefinitions.Add(new() { Height=GridLength.Auto }); grid.RowDefinitions.Add(new() {Height=GridLength.Auto});
        grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height=GridLength.Auto });
        var top=new WrapPanel { Margin=new(0,0,0,8) };
        var ratios=new ComboBox { Name="CropRatios",DisplayMemberPath="Label",SelectedValuePath="Value",Width=195,Margin=new(0,0,12,8) };
        ratios.SetBinding(ItemsControl.ItemsSourceProperty,new Binding(nameof(CropEditorViewModel.Ratios)));
        ratios.SetBinding(ComboBox.SelectedValueProperty,new Binding(nameof(CropEditorViewModel.Ratio)) {Mode=BindingMode.TwoWay});
        ratios.SetBinding(IsEnabledProperty,new Binding(nameof(CropEditorViewModel.CanApply)));
        AutomationProperties.SetName(ratios,"Пропорции кадрирования"); top.Children.Add(ratios);
        var reset=new Button { Name="ResetCropDraft",Content=new IconLabel {Icon="undo-2",Text="Сбросить"},Margin=new(0,0,0,8) };AutomationProperties.SetName(reset,"Сбросить кадрирование");
        reset.SetBinding(Button.CommandProperty,new Binding(nameof(CropEditorViewModel.ResetCommand)));
        reset.SetBinding(IsEnabledProperty,new Binding(nameof(CropEditorViewModel.CanApply)));
        top.Children.Add(reset); grid.Children.Add(top);
        var details=new Expander { Header="Точные координаты и размеры · px",Margin=new(0,0,0,10) }; Grid.SetRow(details,1); grid.Children.Add(details);
        var numbers=new WrapPanel(); details.Content=numbers;
        _fields=[Field("X слева",nameof(CropEditorViewModel.XText),nameof(CropEditorViewModel.XMaximum),0),
            Field("Y сверху",nameof(CropEditorViewModel.YText),nameof(CropEditorViewModel.YMaximum),0),
            Field("Ширина",nameof(CropEditorViewModel.WidthText),nameof(CropEditorViewModel.WidthMaximum),0),
            Field("Высота",nameof(CropEditorViewModel.HeightText),nameof(CropEditorViewModel.HeightMaximum),0)];
        for(var i=0;i<_fields.Length;i++)
        {
            var block=new StackPanel { Width=150,Margin=new(0,8,10,4) }; block.Children.Add(new TextBlock { Text=new[] {"X слева","Y сверху","Ширина","Высота"}[i],Margin=new(0,0,0,4) }); block.Children.Add(_fields[i]); numbers.Children.Add(block);
        }
        CropEditor=new AnnotationCanvas { Name="CropEditor",Page=ViewModel.DraftPage,Tool=EditorTool.Crop,Scaling=ScaleMode.Fit };
        CropEditor.SetBinding(AnnotationCanvas.CropRatioProperty,new Binding(nameof(CropEditorViewModel.LogicalRatio)));
        CropEditor.SetBinding(IsEnabledProperty,new Binding(nameof(CropEditorViewModel.CanApply)));
        reset.Click+=(_,_)=>CropEditor.CancelGesture();
        AutomationProperties.SetName(CropEditor,"Рамка кадрирования фото");
        AutomationProperties.SetHelpText(CropEditor,"Углы и стороны изменяют размер. Внутри рамки — перемещение. Стрелки — один пиксель, Shift со стрелками — десять. Точные размеры доступны выше.");
        Grid.SetRow(CropEditor,2); grid.Children.Add(CropEditor);
        CropEditor.SizeChanged+=(_,_)=>CropEditor.SafeArea=new(16,16,Math.Max(1,CropEditor.ActualWidth-32),Math.Max(1,CropEditor.ActualHeight-32));
        CropEditor.BeforeGesture+=(_,_)=>{ foreach(var field in _fields) field.CancelDraft(); };
        var footer=new Grid { Margin=new(0,12,0,0) }; footer.RowDefinitions.Add(new() {Height=GridLength.Auto}); footer.RowDefinitions.Add(new() {Height=GridLength.Auto});
        Grid.SetRow(footer,3); grid.Children.Add(footer);
        var summary=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,8) }; summary.SetBinding(TextBlock.TextProperty,new Binding(nameof(CropEditorViewModel.Summary))); footer.Children.Add(summary);
        var last=new DockPanel(); Grid.SetRow(last,1); footer.Children.Add(last);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal }; DockPanel.SetDock(buttons,Dock.Right); last.Children.Add(buttons);
        var cancelButton=new Button { Name="CancelCrop",Content=new IconLabel {Icon="x",Text="Отмена"},IsCancel=true,Margin=new(0,0,8,0) };AutomationProperties.SetName(cancelButton,"Отмена");buttons.Children.Add(cancelButton);
        var apply=new Button { Name="ApplyCrop",Content=new IconLabel {Icon="check",Text="Применить"},IsDefault=true };AutomationProperties.SetName(apply,"Применить кадрирование");
        if(TryFindResource("PrimaryButton") is Style primary)apply.Style=primary;
        apply.SetBinding(IsEnabledProperty,new Binding(nameof(CropEditorViewModel.CanApply)));
        apply.Click+=(_,_)=>Apply(); buttons.Children.Add(apply);
        var status=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new(0,0,12,0),VerticalAlignment=VerticalAlignment.Center };
        status.SetBinding(TextBlock.TextProperty,new Binding(nameof(CropEditorViewModel.Error)));
        var hints=new TextBlock { Text="Тяните края рамки или двигайте её внутри. Исходник сохраняется целиком.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,12,0),VerticalAlignment=VerticalAlignment.Center };
        var statusPanel=new StackPanel(); statusPanel.Children.Add(hints); statusPanel.Children.Add(status); last.Children.Add(statusPanel);
        Loaded+=async (_,_)=>{ await InitializeAsync(); if(ViewModel.CanApply) CropEditor.Focus(); };
        PreviewKeyDown+=(_,e)=>{ if(e.Key==Key.Escape && CropEditor.IsCropDragging) {CropEditor.CancelGesture();e.Handled=true;} };
        Closed+=(_,_)=>{ _cancel.Cancel(); CropEditor.CancelGesture(); ViewModel.Dispose(); _cancel.Dispose(); };
        WindowWorkArea.Attach(this);
    }
    private NumericField Field(string label,string property,string maximum,double minimum)
    {
        var field=new NumericField { Name=property.Replace("Text","Input"),Minimum=minimum,MinimumExclusive=property is nameof(CropEditorViewModel.WidthText) or nameof(CropEditorViewModel.HeightText),Unit="px",RejectionMessage="Размер не помещается в фото с выбранными пропорциями. Уменьшите значение.",ShowInlineError=true };
        AutomationProperties.SetName(field,"Кадрирование: "+label);
        field.SetBinding(NumericField.TextProperty,new Binding(property) {Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.Explicit});
        field.SetBinding(NumericField.MaximumProperty,new Binding(maximum));
        field.SetBinding(IsEnabledProperty,new Binding(nameof(CropEditorViewModel.CanApply)));
        return field;
    }
    private void Apply()
    {
        if(!ViewModel.CanApply) return;
        foreach(var field in _fields) if(!field.Commit()) {field.FocusInput();return;}
        CropEditor.CancelGesture(); DialogResult=true;
    }
    internal Task InitializeAsync()=>_loading ??=LoadAsync();
    private async Task LoadAsync()
    {
        var token=_cancel.Token; ViewModel.Error="Загрузка фото…";
        try
        {
            var loaded=await Task.Run(()=>
            {
                SizeD size;
                using(var image=System.Drawing.Image.FromFile(ViewModel.DraftPage.WorkingPath)) size=new(image.Width,image.Height);
                var preview=new PageRasterizer(new PdfiumRenderer()).LoadEditorSource(ViewModel.DraftPage.Snapshot(),token);
                return (Preview:preview,Size:size);
            },token);
            if(token.IsCancellationRequested) return;
            ViewModel.SetPixelSize(loaded.Size); CropEditor.SetCropPixelSize(loaded.Size); CropEditor.Preview=loaded.Preview; ViewModel.Error=""; ViewModel.IsLoading=false;
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(!token.IsCancellationRequested) { ViewModel.Error="Не удалось открыть фото: "+ex.Message+" Проверьте исходный файл и откройте кадрирование заново."; ViewModel.IsLoading=false; } }
    }
}
