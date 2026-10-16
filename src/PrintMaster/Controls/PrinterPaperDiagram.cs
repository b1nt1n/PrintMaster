using PrintMaster.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PrintMaster.Controls;

// A native vector instruction, based on the P1102 input tray photograph and HP loading guide.
// It belongs to the dialog only and cannot enter a document raster.
public sealed class PrinterPaperDiagram : FrameworkElement
{
    public static readonly DependencyProperty SettingsProperty=DependencyProperty.Register(nameof(Settings),typeof(PrintSettingsSnapshot),typeof(PrinterPaperDiagram),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsReloadProperty=DependencyProperty.Register(nameof(IsReload),typeof(bool),typeof(PrinterPaperDiagram),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
    public PrintSettingsSnapshot? Settings { get=>(PrintSettingsSnapshot?)GetValue(SettingsProperty); set=>SetValue(SettingsProperty,value); }
    public bool IsReload { get=>(bool)GetValue(IsReloadProperty); set=>SetValue(IsReloadProperty,value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if(Settings is not {} settings) return;
        var scale=Math.Min(ActualWidth/460,ActualHeight/260); if(scale<=0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth-460*scale)/2,(ActualHeight-260*scale)/2));
        dc.PushTransform(new ScaleTransform(scale,scale));
        var outline=new Pen(new SolidColorBrush(Color.FromRgb(130,145,156)),1.5);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(227,232,235)),outline,new(45,16,370,105),16,16);
        dc.DrawRoundedRectangle(Brushes.WhiteSmoke,outline,new(72,53,316,54),8,8);
        dc.DrawEllipse(Brushes.White,new Pen(Brushes.Gray,1),new(65,72),6,6);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(51,61,67)),null,new(83,99,294,15));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(239,242,244)),outline,new(78,111,304,130));
        var knownP1102=settings.PrinterName.Contains("P1102",StringComparison.OrdinalIgnoreCase);
        // One scale for both dimensions keeps A4/A5/custom proportions honest.
        // Landscape changes the raster, never the physical feed rectangle.
        var paperScale=Math.Min(150/settings.PaperSizeMm.Width,122/settings.PaperSizeMm.Height);
        var paperW=settings.PaperSizeMm.Width*paperScale;
        var paperH=settings.PaperSizeMm.Height*paperScale; var x=230-paperW/2; const double y=110;
        var sheet=new Rect(x,y,paperW,paperH);
        dc.DrawRectangle(IsReload ? new SolidColorBrush(Color.FromRgb(245,245,243)) : Brushes.White,outline,sheet);
        if(settings.Notebook is {} notebook)
        {
            var ruling=new Pen(new SolidColorBrush(Color.FromArgb(90,91,149,187)),0.6);
            for(var iy=y+7;iy<sheet.Bottom;iy+=7) dc.DrawLine(ruling,new(x,iy),new(sheet.Right,iy));
            if(notebook.IsGrid) for(var ix=x+7;ix<sheet.Right;ix+=7) dc.DrawLine(ruling,new(ix,y),new(ix,sheet.Bottom));
            var margin=new Pen(new SolidColorBrush(Color.FromArgb(160,180,69,80)),1);
            if(notebook.IsSpread)
            {
                var fold=y+paperH*notebook.FoldMm/notebook.WidthMm;
                dc.DrawLine(new Pen(Brushes.DimGray,1) { DashStyle=DashStyles.Dash },new(x,fold),new(sheet.Right,fold));
                dc.DrawLine(margin,new(x,y+paperH*notebook.LeftMarginMm/notebook.WidthMm),new(sheet.Right,y+paperH*notebook.LeftMarginMm/notebook.WidthMm));
                dc.DrawLine(margin,new(x,sheet.Bottom-paperH*notebook.RightMarginMm/notebook.WidthMm),new(sheet.Right,sheet.Bottom-paperH*notebook.RightMarginMm/notebook.WidthMm));
            }
            else
            {
                var mx=notebook.SingleMarginOnRight ? sheet.Right-paperW*notebook.RightMarginMm/notebook.WidthMm : x+paperW*notebook.LeftMarginMm/notebook.WidthMm;
                dc.DrawLine(margin,new(mx,y),new(mx,sheet.Bottom));
            }
        }
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(155,167,175)),outline,new(x-9,112,7,33));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(155,167,175)),outline,new(sheet.Right+2,112,7,33));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(155,167,175)),outline,new(215,sheet.Bottom+1,30,5));
        if(knownP1102 && settings.PaperSizeMm.Height<185)
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(33,91,137)),null,new(222,sheet.Bottom-5,16,6));
        var arrow=new Pen(new SolidColorBrush(Color.FromRgb(33,91,137)),3);
        dc.DrawLine(arrow,new(230,183),new(230,120)); dc.DrawLine(arrow,new(230,120),new(221,133)); dc.DrawLine(arrow,new(230,120),new(239,133));
        Label(dc,knownP1102 ? "P1102 · входной лоток" : "Принтер · условная схема",135,31,13,250);
        Label(dc,$"{settings.PaperSizeMm.Width:g} мм — {(knownP1102 ? "вперед" : "размер подачи")}",x-10,sheet.Bottom+9,12,Math.Max(160,paperW+30));
        Label(dc,knownP1102 ? IsReload ? "Уже напечатанная\nсторона вниз" : "Сторона для\nновой печати\nвверх" : "Сторона и край:\nпо руководству\nпринтера",12,151,12,104);
        Label(dc,knownP1102 ? settings.PaperSizeMm.Height<185 ? "Передняя\nнаправляющая\nс насадкой" : "Боковые и\nпередняя\nнаправляющие" : "Загрузка:\nпо руководству",340,151,12,110);
        dc.Pop(); dc.Pop();
    }
    private void Label(DrawingContext dc,string value,double x,double y,double size,double width)
    {
        var text=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,
            new SolidColorBrush(Color.FromRgb(48,63,75)),VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth=width };
        dc.DrawText(text,new(x,y));
    }
}
