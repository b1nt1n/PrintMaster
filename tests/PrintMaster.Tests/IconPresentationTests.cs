using PrintMaster.Controls;
using System.Collections;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace PrintMaster.Tests;

// These controls are measured and rendered on a private STA dispatcher. No
// Application singleton, visible Window, user clipboard or printer is created.
public sealed class IconPresentationTests
{
    [Fact]
    public Task IconFollowsOwnerForegroundChangesAndHonorsAnExplicitOverride()=>OnSta(async ()=>
    {
        var icon=new LucideIcon{Icon="printer"};var button=new Button{Content=icon,Foreground=Brushes.White};
        await Layout(button);Assert.Equal(Colors.White,ColorOf(icon.Foreground));
        button.Foreground=Brushes.Crimson;await Layout(button);Assert.Equal(Colors.Crimson,ColorOf(icon.Foreground));
        icon.Foreground=Brushes.ForestGreen;button.Foreground=Brushes.Blue;await Layout(button);
        Assert.Equal(Colors.ForestGreen,ColorOf(icon.Foreground));
        icon.ClearValue(LucideIcon.ForegroundProperty);await Layout(button);Assert.Equal(Colors.Blue,ColorOf(icon.Foreground));
        var drawing=Render(icon,20,20);Assert.True(VisiblePixels(drawing)>20,"The inherited blue icon must have visible strokes.");
    });

    [Fact]
    public Task IconLabelKeepsPrimaryButtonCaptionWhiteDespiteImplicitTextBlockStyle()=>OnSta(async ()=>
    {
        var resources=LoadResources();var label=new IconLabel{Icon="folder-open",Text="Добавить файлы"};
        var button=new Button{Resources=resources,Style=(Style)resources["PrimaryButton"],Content=label,
            FontSize=19,FontWeight=FontWeights.SemiBold,FontFamily=new("Consolas")};
        await Layout(button);
        var icon=Assert.IsType<LucideIcon>(label.Children[0]);var caption=Assert.IsType<TextBlock>(label.Children[1]);
        Assert.Equal(Colors.White,ColorOf(button.Foreground));Assert.Equal(Colors.White,ColorOf(icon.Foreground));Assert.Equal(Colors.White,ColorOf(caption.Foreground));
        Assert.NotEqual(ColorOf((Brush)resources["Ink"]),ColorOf(caption.Foreground));
        Assert.Equal("Добавить файлы",caption.Text);Assert.Equal(19,caption.FontSize);Assert.Equal(FontWeights.SemiBold,caption.FontWeight);Assert.Equal("Consolas",caption.FontFamily.Source);
        label.Icon="save";label.Text="Сохранить";button.Foreground=Brushes.Gold;button.FontSize=16;await Layout(button);
        Assert.Equal("save",icon.Icon);Assert.Equal("Сохранить",caption.Text);Assert.Equal(Colors.Gold,ColorOf(icon.Foreground));Assert.Equal(Colors.Gold,ColorOf(caption.Foreground));Assert.Equal(16,caption.FontSize);
        Assert.False(label.Focusable);Assert.False(label.IsHitTestVisible);Assert.False(icon.Focusable);Assert.False(icon.IsHitTestVisible);
    });

    [Fact]
    public Task PrimaryDisabledAndCheckedToolStatesKeepIconAndCaptionInTheSameVisualState()=>OnSta(async ()=>
    {
        var resources=LoadResources();var label=new IconLabel{Icon="printer",Text="Печать"};
        var button=new Button{Resources=resources,Style=(Style)resources["PrimaryButton"],Content=label};await Layout(button);
        var icon=Assert.IsType<LucideIcon>(label.Children[0]);var caption=Assert.IsType<TextBlock>(label.Children[1]);
        var frame=Assert.IsType<Border>(button.Template.FindName("Frame",button));
        Assert.Equal(ColorOf((Brush)resources["Accent"]),ColorOf(frame.Background));Assert.Equal(1,frame.Opacity);
        button.IsEnabled=false;await Layout(button);
        Assert.Equal(.4,frame.Opacity,3);Assert.False(icon.IsEnabled);Assert.False(caption.IsEnabled);
        Assert.Equal(ColorOf(button.Foreground),ColorOf(icon.Foreground));Assert.Equal(ColorOf(button.Foreground),ColorOf(caption.Foreground));
        button.IsEnabled=true;await Layout(button);Assert.Equal(1,frame.Opacity);

        var toolIcon=new LucideIcon{Icon="pencil"};var toggle=new ToggleButton{Resources=resources,Style=(Style)resources["ToolButton"],Content=toolIcon};
        await Layout(toggle,36,36);var toolFrame=Assert.IsType<Border>(toggle.Template.FindName("Frame",toggle));
        var offColor=ColorOf(toolFrame.Background);toggle.IsChecked=true;await Layout(toggle,36,36);
        Assert.NotEqual(offColor,ColorOf(toolFrame.Background));Assert.Equal(ColorOf((Brush)resources["Accent"]),ColorOf(toolFrame.BorderBrush));
        Assert.Equal(ColorOf(toggle.Foreground),ColorOf(toolIcon.Foreground));
        toggle.IsEnabled=false;await Layout(toggle,36,36);Assert.Equal(.4,toolFrame.Opacity,3);Assert.Equal(ColorOf(toggle.Foreground),ColorOf(toolIcon.Foreground));
        toggle.IsEnabled=true;toggle.IsChecked=false;await Layout(toggle,36,36);Assert.Equal(offColor,ColorOf(toolFrame.Background));Assert.Equal(1,toolFrame.Opacity);
    });

    [Fact]
    public Task LongCommandCaptionWrapsWithinANarrowButtonWithoutHidingTheIcon()=>OnSta(async ()=>
    {
        var resources=LoadResources();var label=new IconLabel{Icon="save",Text="Сохранить копию изображения"};
        var button=new Button{Resources=resources,Content=label};await Layout(button,180,100);
        var icon=Assert.IsType<LucideIcon>(label.Children[0]);var caption=Assert.IsType<TextBlock>(label.Children[1]);
        Assert.Equal(TextWrapping.Wrap,caption.TextWrapping);Assert.InRange(caption.ActualWidth,1,132);
        Assert.True(caption.ActualHeight>caption.FontSize*1.5,"A long caption must wrap instead of overflowing a narrow dialog.");
        Assert.Equal(18,icon.ActualWidth);Assert.Equal(18,icon.ActualHeight);Assert.True(VisiblePixels(Render(icon,18,18))>10);
    });

    [Fact]
    public Task EveryBundledLucideSvgLoadsAndProducesVisibleVectorStrokes()=>OnSta(async ()=>
    {
        var assembly=typeof(LucideIcon).Assembly;
        using var stream=assembly.GetManifestResourceStream("PrintMaster.g.resources");Assert.NotNull(stream);
        using var resources=new ResourceReader(stream!);
        var names=resources.Cast<DictionaryEntry>().Select(e=>(string)e.Key).Where(name=>name.StartsWith("assets/icons/",StringComparison.Ordinal) && name.EndsWith(".svg",StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(names);
        var sourceDirectory=Path.GetFullPath(Path.Combine(Fixtures.Root,"..","..","src","PrintMaster","Assets","Icons"));
        var sourceNames=Directory.GetFiles(sourceDirectory,"*.svg").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Assert.Equal(sourceNames,names.Select(Path.GetFileNameWithoutExtension).Order().ToArray());
        foreach(var resourceName in names)
        {
            var name=Path.GetFileNameWithoutExtension(resourceName);Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$",name);
            var icon=new LucideIcon{Icon=name,Foreground=Brushes.Magenta,Width=32,Height=32};await Layout(icon,32,32);
            var image=Render(icon,32,32);Assert.True(VisiblePixels(image)>10,$"Bundled Lucide asset '{name}' rendered no visible strokes.");
            var bytes=Pixels(image);
            Assert.Contains(Enumerable.Range(0,bytes.Length/4),i=>bytes[i*4+3]>180 && bytes[i*4]>100 && bytes[i*4+2]>100 && bytes[i*4+1]<10);
        }
    });

    private static ResourceDictionary LoadResources()
    {
        var source=XDocument.Load(Path.GetFullPath(Path.Combine(Fixtures.Root,"..","..","src","PrintMaster","App.xaml")));
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        // These tests create no Window. Its production Icon setter needs the
        // application pack-URI service, which is intentionally unavailable after
        // the full window smoke test shuts Application down. Keep the real button,
        // toggle and TextBlock styles, without introducing another Application.
        var resources=new XElement(ns+"ResourceDictionary",source.Root!.Attributes().Where(a=>a.IsNamespaceDeclaration),source.Root.Element(ns+"Application.Resources")!.Elements()
            .Where(element=>element.Name!=ns+"Style" || (string?)element.Attribute("TargetType") is not ("Window" or "{x:Type Window}")));
        resources.SetAttributeValue(XNamespace.Xmlns+"views","clr-namespace:PrintMaster.Views;assembly=PrintMaster");
        foreach(var element in resources.Descendants().Where(e=>e.Name.NamespaceName=="clr-namespace:PrintMaster.Views").ToArray())
            element.Name=XNamespace.Get("clr-namespace:PrintMaster.Views;assembly=PrintMaster")+element.Name.LocalName;
        return (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
    private static Color ColorOf(Brush brush)=>Assert.IsType<SolidColorBrush>(brush).Color;
    private static async Task Layout(FrameworkElement element,double width=240,double height=48)
    {
        element.Measure(new(width,height));element.Arrange(new(0,0,width,height));element.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        element.Measure(new(width,height));element.Arrange(new(0,0,width,height));element.UpdateLayout();
    }
    private static RenderTargetBitmap Render(Visual visual,int width,int height)
    {var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(visual);return image;}
    private static byte[] Pixels(BitmapSource image)
    {var bytes=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(bytes,image.PixelWidth*4,0);return bytes;}
    private static int VisiblePixels(BitmapSource image)=>Pixels(image).Where((_,i)=>i%4==3).Count(alpha=>alpha>0);
    private static Task OnSta(Func<Task> body)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async ()=>
            {try{await body();completion.SetResult();}catch(Exception ex){completion.SetException(ex);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
            Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
}
