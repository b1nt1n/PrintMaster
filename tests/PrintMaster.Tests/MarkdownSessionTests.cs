using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownSessionTests
{
    private sealed class Dialogs:IUserDialogs
    {
        public int MarkdownEditorCalls {get;private set;}
        public string? MarkdownSource {get;private set;}
        public string[]PickFiles()=>[];public string?PickPdfOutput()=>null;public string?PickLibreOffice()=>null;public bool ConfirmCropping(int count)=>false;public void Error(string message)=>throw new Exception(message);
        public bool EditMarkdown(MarkdownEditorViewModel model)
        {MarkdownEditorCalls++;MarkdownSource=model.Source;return false;}
    }
    private static PrintSessionViewModel Session(Dialogs? dialogs=null,FakeClipboard? clipboard=null)=>new(dialogs ?? new Dialogs(),Path.Combine(Fixtures.Root,"pm014-session-"+Guid.NewGuid().ToString("N")),clipboard);
    private static MarkdownDocument Doc(string source)=>new(Guid.NewGuid(),source,MarkdownStyle.Default,[],"Заметка");
    [Fact]
    public async Task PasteMarkdownCommand_PreservesExactClipboardSourceAndCancelLeavesQueueUnchanged()
    {
        const string source="# Математика\r\n\r\n  $x^2$ и $$\\frac{1}{2}$$\r\n\r\n";
        var clipboard=new FakeClipboard {Content=new([],Text:source)};var dialogs=new Dialogs();using var vm=Session(dialogs,clipboard);vm.Settings.PrinterName="TestPrinter";
        var page=PrintPageItem.CreateBlank(SheetPaper.A4);page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(20,20,90,30)),"До вставки",new()));vm.AddPage(page);vm.SelectedPage=page;
        var pages=vm.Pages.ToArray();var snapshots=pages.Select(p=>p.Document.Snapshot()).ToArray();var undo=vm.UndoCount;var settings=vm.Settings.Snapshot();
        await vm.PasteMarkdownCommand.ExecuteAsync(null);
        Assert.Equal(1,clipboard.Reads);Assert.Equal(1,dialogs.MarkdownEditorCalls);Assert.Equal(source,dialogs.MarkdownSource);Assert.Equal(source,clipboard.Content.Text);Assert.Equal(0,clipboard.Writes);
        Assert.Equal(pages,vm.Pages.ToArray());Assert.Equal(snapshots,vm.Pages.Select(p=>p.Document.Snapshot()).ToArray());Assert.Same(page,vm.SelectedPage);Assert.Equal(undo,vm.UndoCount);Assert.Equal(settings,vm.Settings.Snapshot());Assert.False(vm.IsBusy);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task PasteMarkdownCommand_WithoutNonblankTextDoesNotOpenEditorOrCreatePage(int contentKind)
    {
        var clipboard=new FakeClipboard {Content=contentKind switch
        {
            1=>new([],Text:" \r\n\t"),
            2=>new([],Image:ClipboardTests.Image()),
            3=>new([Fixtures.CreateImage("pm014-nontext-clipboard.png")]),
            _=>ClipboardSnapshot.Empty
        }};
        var dialogs=new Dialogs();using var vm=Session(dialogs,clipboard);var page=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(page);vm.SelectedPage=page;
        var snapshot=page.Document.Snapshot();var undo=vm.UndoCount;
        await vm.PasteMarkdownCommand.ExecuteAsync(null);
        Assert.Equal(1,clipboard.Reads);Assert.Equal(0,dialogs.MarkdownEditorCalls);Assert.Equal(0,clipboard.Writes);Assert.Same(page,Assert.Single(vm.Pages));Assert.Equal(snapshot,page.Document.Snapshot());Assert.Equal(undo,vm.UndoCount);Assert.False(vm.HasMarkdown);Assert.False(vm.IsBusy);Assert.Contains("Скопируйте текст Markdown",vm.Status);
    }
    [Fact]
    public async Task MultiPageApplyAndReEdit_AreOneUndoEachAndPreserveFullOriginal()
    {
        using var vm=Session();var source=string.Join("\n\n",Enumerable.Range(1,80).Select(i=>$"Строка {i:000}: исходный текст с формулой $x^2+1$."));
        var document=Doc(source);var paper=new SheetPaper(new(148,210),false,SheetMargins.SafeDefault);
        var before=vm.UndoCount;await vm.ApplyMarkdownAsync(new(document,[]),paper,null,default);
        Assert.True(vm.Pages.Count>1);Assert.Equal(before+1,vm.UndoCount);Assert.True(vm.CanEditMarkdown);
        var session=vm.CaptureProject();Assert.Single(session.MarkdownDocuments);Assert.Equal(source,session.MarkdownDocuments[0].Source);
        Assert.All(vm.Pages,p=>Assert.Same(document,p.Document.Objects.OfType<MarkdownObject>().Single().Document));
        var count=vm.Pages.Count;vm.Pages[0].Copies=3;vm.Pages[1].IsIncluded=false;
        var changed=document with {Source=source+"\n\nПоследняя формула: $$\\sqrt{b^2-4ac}$$"};before=vm.UndoCount;
        await vm.ApplyMarkdownAsync(new(changed,[]),paper,document.Id,default);Assert.Equal(before+1,vm.UndoCount);Assert.Equal(3,vm.Pages[0].Copies);Assert.False(vm.Pages[1].IsIncluded);
        vm.UndoCommand.Execute(null);Assert.Equal(count,vm.Pages.Count);Assert.Equal(source,vm.CaptureProject().MarkdownDocuments.Single().Source);
        vm.RedoCommand.Execute(null);Assert.Equal(changed.Source,vm.CaptureProject().MarkdownDocuments.Single().Source);
    }
    [Fact]
    public async Task BadFormulaDoesNotReplaceExistingPagesOrLoseSource()
    {
        using var vm=Session();var document=Doc("Текст $x^2$.");await vm.ApplyMarkdownAsync(new(document,[]),SheetPaper.A4,null,default);
        var pages=vm.Pages.ToArray();var snapshots=pages.Select(p=>p.Document.Snapshot()).ToArray();var undo=vm.UndoCount;
        var broken=document with {Source="$$\\printmasterUnknownCommand{1}$$"};
        await Assert.ThrowsAsync<MarkdownLayoutException>(()=>vm.ApplyMarkdownAsync(new(broken,[]),SheetPaper.A4,document.Id,default));
        Assert.Equal(pages,vm.Pages.ToArray());Assert.Equal(undo,vm.UndoCount);Assert.Equal(document.Source,vm.CaptureProject().MarkdownDocuments.Single().Source);
        Assert.Equal("$$\\printmasterUnknownCommand{1}$$",broken.Source);Assert.Equal(snapshots,vm.Pages.Select(p=>p.Document.Snapshot()).ToArray());
    }
    [Fact]
    public async Task ImportRelativeImages_OwnsMaterialAndSurvivesOriginalRemoval()
    {
        using var vm=Session();var directory=Path.Combine(Fixtures.Root,"pm014-import-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"note.md");var image=Path.Combine(directory,"picture.png");File.Copy(Fixtures.CreateImage(),image);
        await File.WriteAllTextAsync(path,"# Записка\n\n![Картинка](picture.png)\n\n$$\\frac{1}{2}$$");
        await vm.ImportMarkdownAsync(path,default);var page=vm.Pages[0];var source=Assert.Single(page.Document.Snapshot().Assets);Assert.NotEqual(image,source.WorkingPath);
        File.Delete(image);File.Delete(path);Assert.False(page.HasMissingSources);using var bitmap=new PageRasterizer(new()).RenderSheet(page.Document.Snapshot(),120,false,default,600);
        Assert.True(bitmap.Width>0);Assert.Contains("picture.png",vm.CaptureProject().MarkdownDocuments.Single().Source);
        var project=ProjectFileTests.PathFor("pm014-images");await new ProjectFileService().SaveAsync(vm.CaptureProject(),project,true,default);
        using var workspace=new SessionWorkspace();var loaded=await new ProjectFileService().LoadAsync(project,workspace,default);Assert.Empty(loaded.Missing);Assert.Single(loaded.Session.MarkdownDocuments);
    }
    [Fact]
    public async Task QueueDuplicateKeepsLinkedCopiesIndependentFromOriginalDocument()
    {
        using var vm=Session();var document=Doc(string.Join("\n\n",Enumerable.Range(1,100).Select(i=>$"Строка {i}: заметка для нескольких страниц.")));
        await vm.ApplyMarkdownAsync(new(document,[]),new(new(148,210),false,SheetMargins.SafeDefault),null,default);
        var originals=vm.Pages.ToArray();vm.SetQueueSelection(originals,originals[0]);vm.DuplicateHighlightedPagesCommand.Execute(null);
        Assert.Equal(originals.Length*2,vm.Pages.Count);var definitions=vm.CaptureProject().MarkdownDocuments;Assert.Equal(2,definitions.Length);
        var newId=definitions.Single(d=>d.Id!=document.Id).Id;var copies=vm.Pages.Where(p=>!originals.Contains(p)).ToArray();
        Assert.All(copies,p=>Assert.Equal(newId,p.Document.Objects.OfType<MarkdownObject>().Single().DocumentId));
    }
    [Fact]
    public async Task ShortenedDocumentRetainsTailOverlaysAsOrdinarySheets()
    {
        using var vm=Session();var document=Doc(string.Join("\n\n",Enumerable.Range(1,100).Select(i=>$"Строка {i}: длинная заметка.")));
        var paper=new SheetPaper(new(148,210),false,SheetMargins.SafeDefault);await vm.ApplyMarkdownAsync(new(document,[]),paper,null,default);
        Assert.True(vm.Pages.Count>1);var tail=vm.Pages.Last();var drawing=new ShapeObject(Guid.NewGuid(),new(new(20,20,30,30)),SheetShapeKind.Rectangle,new());tail.Document.AddObject(drawing);
        var oldCount=vm.Pages.Count;await vm.ApplyMarkdownAsync(new(document with {Source="Коротко."},[]),paper,document.Id,default);
        Assert.Equal(2,vm.Pages.Count);var retained=vm.Pages.Single(p=>p.Document.Objects.Any(o=>o.Id==drawing.Id));Assert.DoesNotContain(retained.Document.Objects,o=>o is MarkdownObject);
        Assert.Equal(drawing,Assert.Single(retained.Document.Objects));vm.UndoCommand.Execute(null);Assert.Equal(oldCount,vm.Pages.Count);
    }
    [Theory] [InlineData(300)] [InlineData(600)]
    public async Task SheetRasterizerUsesPhysicalCoordinatesWithoutSecondSafeAreaScaling(int dpi)
    {
        var paper=new SheetPaper(new(60,80),false,SheetMargins.SafeDefault);var document=Doc("Слово и $x^2$.") with {Style=new(new(FontSizePt:12),new(15,20,15,20))};
        var layout=await MarkdownLayoutService.Shared.LayoutAsync(document,paper,ImmutableArray<SourceAsset>.Empty,default);
        var item=new MarkdownObject(Guid.NewGuid(),new(paper.SafeAreaMm),document.Id,0){Document=document};
        var sheet=new SheetSnapshot(Guid.NewGuid(),paper,[],[item],null);
        using var direct=MarkdownDrawingRenderer.Render(layout.Pages[0],dpi,default,maxPixelsAcross:null);
        using var rendered=new PageRasterizer(new()).RenderSheet(sheet,dpi,false,default);
        Assert.Equal(direct.Size,rendered.Size);
        static (int Left,int Top,int Right,int Bottom,double CenterX,double CenterY) InkBounds(Bitmap bitmap)
        {
            var left=bitmap.Width;var top=bitmap.Height;var right=-1;var bottom=-1;long sumX=0,sumY=0,count=0;
            for(var y=0;y<bitmap.Height;y++)for(var x=0;x<bitmap.Width;x++)
            {
                var darkness=255-bitmap.GetPixel(x,y).R;
                if(darkness>127){left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
                sumX+=x*darkness;sumY+=y*darkness;count+=darkness;
            }
            Assert.True(count>0);return(left,top,right,bottom,sumX/(double)count,sumY/(double)count);
        }
        var expected=InkBounds(direct);var actual=InkBounds(rendered);
        Assert.InRange(Math.Abs(expected.Left-actual.Left),0,1);Assert.InRange(Math.Abs(expected.Top-actual.Top),0,1);
        Assert.InRange(Math.Abs(expected.Right-actual.Right),0,1);Assert.InRange(Math.Abs(expected.Bottom-actual.Bottom),0,1);
        Assert.InRange(Math.Abs(expected.CenterX-actual.CenterX),0,1);Assert.InRange(Math.Abs(expected.CenterY-actual.CenterY),0,1);
        Assert.False(SheetEditing.HasClipping(new PrintPageItem(SheetDocument.Restore(sheet)).Snapshot(),paper.SafeAreaMm));
    }
}
