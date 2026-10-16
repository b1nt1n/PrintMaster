using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class TextLayoutTests
{
    [Theory]
    [InlineData("Русский текст\r\nСледующая строка\n\nХвост\n")]
    [InlineData("  spaces    and\ttabs  end  ")]
    [InlineData("👩🏽‍💻e\u0301👩🏽‍💻e\u0301👩🏽‍💻e\u0301")]
    [InlineData("Оченьдлинноесловобезпробеловдляпроверкипереноса")]
    public void WrappingAndPaginationKeepEveryCharacterAndGrapheme(string value)
    {
        var text=string.Join("\n",Enumerable.Repeat(value,12)); var style=new TextStyle(FontSizePt:14);
        var layout=TextLayoutService.Layout(text,style,35);
        var boundaries=StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
        Assert.All(layout.Lines,line=>{ Assert.Contains(line.Start,boundaries); Assert.Contains(line.Start+line.Length,boundaries); });
        var chunks=TextLayoutService.Paginate(text,style,new(5,5,35,45));
        Assert.True(chunks.Count>1); Assert.Equal(text,string.Concat(chunks.Select(p=>p.Text)));
        for(var i=0;i<chunks.Count;i++)
        {
            var measured=TextLayoutService.Layout(chunks[i].Text,style,35,i+1<chunks.Count);
            Assert.False(measured.WidthOverflow); Assert.InRange(measured.HeightMm,0,45.001);
            Assert.InRange(measured.HeightMm,0,chunks[i].HeightMm+0.01);
        }
    }
    [Fact]
    public void LastExplicitNewlineNeverOverflowsItsGeneratedFrame()
    {
        var style=new TextStyle(FontSizePt:14); var line=TextLayoutService.Layout("A",style,30).LineHeightMm;
        var pages=TextLayoutService.Paginate("A\r\n",style,new(0,0,30,line+0.5));
        Assert.Equal(2,pages.Count); Assert.Equal("A\r\n",string.Concat(pages.Select(p=>p.Text)));
        Assert.Equal("",pages[1].Text);
        Assert.True(TextLayoutService.Layout(pages[0].Text,style,30,true).HeightMm<=pages[0].HeightMm+0.01);
    }
    [Fact]
    public void FontSpacingAndWidthRecalculateWithoutShrinkingFont()
    {
        var text=string.Join(" ",Enumerable.Repeat("Текст для печати",45)); var style=new TextStyle("Arial",14,"#175E9C",true,true,TextAlignment.Right,1);
        var normal=TextLayoutService.Layout(text,style,120); var narrow=TextLayoutService.Layout(text,style,60);
        var spaced=TextLayoutService.Layout(text,style with { LineSpacing=2 },120);
        Assert.True(narrow.Lines.Length>normal.Lines.Length); Assert.True(normal.Lines.SequenceEqual(spaced.Lines));
        Assert.Equal(normal.LineHeightMm*2,spaced.LineHeightMm,6);
        var pages=TextLayoutService.Paginate(text,style with { LineSpacing=2 },new(5,5,120,60)); Assert.True(pages.Count>1); Assert.Equal(14,style.FontSizePt);
        Assert.Throws<InvalidOperationException>(()=>TextLayoutService.Paginate(text,style with { FontSizePt=1000 },new(5,5,200,287)));
    }
    [Fact]
    public void UnknownFontIsExplicitlyResolvedAndNarrowOrShortFramesWarn()
    {
        var style=new TextStyle("__missing_printmaster_font__",18); var layout=TextLayoutService.Layout("Hello",style,80);
        Assert.True(layout.FontFallback); Assert.NotEqual(style.FontFamily,layout.FontFamily);
        Assert.True(TextLayoutService.Overflows(new(Guid.NewGuid(),new(new(5,5,0.1,30)),"W",new(FontSizePt:18))));
        Assert.True(TextLayoutService.Overflows(new(Guid.NewGuid(),new(new(5,5,80,1)),"Hello",style)));
        Assert.Throws<ArgumentException>(()=>TextLayoutService.Layout("A",style with { LineSpacing=0.5 },80));
    }
    [Fact]
    public void InteractiveTextEditingIsOneUndoAndCancelRestoresThePreviousObjects()
    {
        var document=SheetDocument.CreateBlank(SheetPaper.A4); var before=document.Snapshot();
        document.BeginEdit("Текст"); var item=SheetEditing.CreateText("A",new(),new(10,10),document.Paper.SafeAreaMm); document.AddObject(item);
        document.ReplaceObject(item with { Text="AB" }); document.ReplaceObject(item with { Text="ABC",Style=item.Style with { Bold=true } });
        Assert.False(document.CanUndo); Assert.Equal("ABC",((TextObject)document.Objects.Single()).Text);
        document.EndEdit(true); Assert.True(document.CanUndo); document.Undo(); Assert.Equal(before,document.Snapshot()); document.Redo();
        var edited=document.Snapshot(); document.BeginEdit("Правка"); document.RemoveObject(item.Id); document.EndEdit(false); Assert.Equal(edited,document.Snapshot());
    }
    [Fact]
    public async Task StyledTextAndFlowLinksRoundTripAndLegacyVersionOneStillLoads()
    {
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); var style=new TextStyle("Arial",18,"#123456",true,true,TextAlignment.Center,1.5);
        page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(20,20,150,40)),"Текст проекта",style,Flow:new(Guid.NewGuid(),0,false)));
        var session=new ProjectSession(ProjectPrintSettings.Capture(new() { PrinterName="Microsoft Print to PDF" }),new(page.Id,50,true,false,"#111827",4,24,0,300,style),[new(page.Document.Snapshot(),true,2)]);
        var path=ProjectFileTests.PathFor("pm004-style"); var service=new ProjectFileService(); await service.SaveAsync(session,path,true,default);
        using var workspace=new SessionWorkspace(); var loaded=await service.LoadAsync(path,workspace,default);
        Assert.Equal(style,loaded.Session.Editor.SheetTextStyle); var restored=(TextObject)loaded.Session.Pages.Single().Sheet.Objects.Single(); Assert.Equal(style,restored.Style); Assert.NotNull(restored.Flow);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var entry=zip.GetEntry("manifest.json")!; JsonNode node; using(var reader=new StreamReader(entry.Open())) node=JsonNode.Parse(reader.ReadToEnd())!;
            node["version"]=1; node["session"]!["editor"]!.AsObject().Remove("sheetTextStyle");
            var text=node["session"]!["pages"]![0]!["sheet"]!["objects"]![0]!; text.AsObject().Remove("flow"); text["style"]!.AsObject().Remove("lineSpacing");
            entry.Delete(); using var writer=new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(node.ToJsonString());
        }
        loaded=await service.LoadAsync(path,workspace,default); restored=(TextObject)loaded.Session.Pages.Single().Sheet.Objects.Single();
        Assert.Equal(1,restored.Style.LineSpacing); Assert.Null(restored.Flow); Assert.Null(loaded.Session.Editor.SheetTextStyle);
    }
    [Fact]
    public async Task FlowTextReformatsAndUndoesAllPagesWithoutLosingTextOrCopies()
    {
        var vm=new PrintSessionViewModel(new TestDialogs(),Path.Combine(Fixtures.Root,"pm004-flow-recovery-"+Guid.NewGuid().ToString("N")));
        try
        {
            vm.Settings.PrinterName="Microsoft Print to PDF"; var blank=PrintPageItem.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault)); blank.Copies=2; vm.AddPage(blank);
            vm.BeginFlowTextCommand.Execute(null); var source=string.Join("\n",Enumerable.Range(1,100).Select(i=>$"Строка {i:D3}: длинный текст остается целиком."));
            vm.EditingText=source; Assert.True(vm.IsEditingText); Assert.False(vm.UndoCommand.CanExecute(null)); vm.FinishTextEditCommand.Execute(null);
            Assert.True(vm.Pages.Count>1); Assert.Equal(source,vm.StoryText(vm.Pages[0].Document.Objects.OfType<TextObject>().Single().Flow!.StoryId));
            Assert.All(vm.Pages,p=>Assert.Equal(2,p.Copies)); var oldCount=vm.Pages.Count; var oldFirst=vm.Pages[0];
            vm.TextFontSizePt=24; Assert.True(vm.Pages.Count>oldCount); Assert.All(vm.Pages,p=>Assert.Equal(24,p.Document.Objects.OfType<TextObject>().Single().Style.FontSizePt));
            vm.UndoCommand.Execute(null); Assert.Equal(oldCount,vm.Pages.Count); Assert.Same(oldFirst,vm.Pages[0]);
            vm.UndoCommand.Execute(null); Assert.Single(vm.Pages); Assert.Same(blank,vm.Pages[0]); Assert.Empty(blank.Document.Objects);
            vm.RedoCommand.Execute(null); Assert.Equal(oldCount,vm.Pages.Count);
            var middle=vm.Pages[1]; vm.SelectedPage=middle; vm.SelectedSheetObjectId=middle.Document.Objects.OfType<TextObject>().Single().Id;
            vm.EditSelectedTextCommand.Execute(null); var fragment=vm.EditingText; vm.EditingText="ДОБАВЛЕНО\n"+fragment; vm.FinishTextEditCommand.Execute(null);
            var story=vm.Pages[0].Document.Objects.OfType<TextObject>().Single().Flow!.StoryId;
            Assert.Equal(source.Insert(source.IndexOf(fragment,StringComparison.Ordinal),"ДОБАВЛЕНО\n"),vm.StoryText(story));
            vm.UndoCommand.Execute(null); Assert.Equal(source,vm.StoryText(story));
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    private sealed class TestDialogs : IUserDialogs
    {
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null; public bool ConfirmCropping(int count)=>false;
        public void Error(string message)=>throw new InvalidOperationException(message);
    }
    [Fact]
    public async Task NewFlowOnAnExistingLayoutCanBeCancelledAndUndoneWithoutDeletingTheLayout()
    {
        var vm=new PrintSessionViewModel(new TestDialogs(),Path.Combine(Fixtures.Root,"pm004-extra-recovery-"+Guid.NewGuid().ToString("N")));
        try
        {
            var layout=PrintPageItem.CreateBlank(SheetPaper.A4); layout.Document.AddObject(SheetEditing.CreateText("Исходный макет",new(),new(15,15),layout.Document.Paper.SafeAreaMm)); vm.AddPage(layout);
            vm.BeginFlowTextCommand.Execute(null); Assert.Equal(2,vm.Pages.Count); vm.EditingText="Отменить этот текст"; vm.CancelTextEditCommand.Execute(null);
            Assert.Single(vm.Pages); Assert.Same(layout,vm.SelectedPage); Assert.Equal("Исходный макет",layout.Document.Objects.OfType<TextObject>().Single().Text);
            vm.BeginFlowTextCommand.Execute(null); vm.EditingText=string.Join("\n",Enumerable.Range(1,100).Select(i=>"Новая строка "+i)); vm.FinishTextEditCommand.Execute(null);
            var count=vm.Pages.Count; Assert.True(count>2); vm.UndoCommand.Execute(null); Assert.Single(vm.Pages); Assert.Same(layout,vm.SelectedPage);
            Assert.True(vm.RedoCommand.CanExecute(null)); vm.RedoCommand.Execute(null); Assert.Equal(count,vm.Pages.Count);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task ClearingAnExistingTextDeletesItAsOneUndoableEdit()
    {
        var vm=new PrintSessionViewModel(new TestDialogs(),Path.Combine(Fixtures.Root,"pm004-clear-recovery-"+Guid.NewGuid().ToString("N")));
        try
        {
            var page=PrintPageItem.CreateBlank(SheetPaper.A4); var text=SheetEditing.CreateText("Сохранить для отмены",new(),new(15,15),page.Document.Paper.SafeAreaMm); page.Document.AddObject(text); vm.AddPage(page);
            vm.BeginTextEditing(new(15,15),text); vm.EditingText=""; vm.FinishTextEditCommand.Execute(null); Assert.Empty(page.Document.Objects);
            vm.UndoCommand.Execute(null); Assert.Equal(text,page.Document.Objects.Single());
            vm.BeginTextEditing(new(15,15),(TextObject)page.Document.Objects.Single()); vm.EditingText="   "; vm.FinishTextEditCommand.Execute(null); Assert.Equal("   ",page.Document.Objects.OfType<TextObject>().Single().Text);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task FlowShorteningPreservesManualFiguresAndUndoRestoresAllText()
    {
        var vm=new PrintSessionViewModel(new TestDialogs(),Path.Combine(Fixtures.Root,"pm004-decor-recovery-"+Guid.NewGuid().ToString("N")));
        try
        {
            vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4)); vm.BeginFlowTextCommand.Execute(null); var source=string.Join("\n",Enumerable.Range(1,140).Select(i=>"Строка "+i)); vm.EditingText=source; vm.FinishTextEditCommand.Execute(null);
            var last=vm.Pages.Last(); var lastText=last.Document.Objects.OfType<TextObject>().Single(); var story=lastText.Flow!.StoryId;
            var figure=new ShapeObject(Guid.NewGuid(),new(new(20,20,40,20)),SheetShapeKind.Rectangle,new()); last.Document.AddObject(figure);
            foreach(var page in vm.Pages.ToArray())
            {
                vm.SelectedPage=page; vm.SelectedSheetObjectId=page.Document.Objects.OfType<TextObject>().Single().Id;
                vm.BeginTextEditing(new(5,5),page.Document.Objects.OfType<TextObject>().Single()); vm.EditingText=page==vm.Pages.First() ? "Остался короткий текст" : "";
                // End the edit before the next loop; each reflow may change page references.
                vm.FinishTextEditCommand.Execute(null); break;
            }
            Assert.Contains(vm.Pages.SelectMany(p=>p.Document.Objects),o=>o.Id==figure.Id);
            vm.UndoCommand.Execute(null); Assert.Equal(source,vm.StoryText(story)); Assert.Contains(vm.Pages.SelectMany(p=>p.Document.Objects),o=>o.Id==figure.Id);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task SavingDuringInlineTypingFinalizesTheEditAndKeepsItsStyle()
    {
        var vm=new PrintSessionViewModel(new TestDialogs(),Path.Combine(Fixtures.Root,"pm004-save-recovery-"+Guid.NewGuid().ToString("N")));
        try
        {
            vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4)); vm.BeginTextEditing(new(12,12)); vm.EditingText="Текст без отдельного нажатия «Готово»"; vm.TextBold=true; vm.TextLineSpacing=1.5;
            var path=ProjectFileTests.PathFor("pm004-during-input"); Assert.True(await vm.SaveProjectToAsync(path)); Assert.False(vm.IsEditingText); Assert.False(vm.HasUnsavedChanges);
            using var workspace=new SessionWorkspace(); var saved=await new ProjectFileService().LoadAsync(path,workspace,default); var text=saved.Session.Pages.Single().Sheet.Objects.OfType<TextObject>().Single();
            Assert.Equal("Текст без отдельного нажатия «Готово»",text.Text); Assert.True(text.Style.Bold); Assert.Equal(1.5,text.Style.LineSpacing);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact] [Trait("Category","Printer")]
    public async Task MultiPageTextPrintsEveryLineAt600DpiAndKeepsPaperSize()
    {
        var printer=PrinterInterop.ListPrinters().First(p=>p.Equals("Microsoft Print to PDF",StringComparison.OrdinalIgnoreCase));
        var settings=new PrintSettings { PrinterName=printer,Paper=PaperPreset.A5,Dpi=600 };
        var profile=new PrinterInterop().Probe(settings.Snapshot()); var safe=PageLayout.PrintSafeArea(profile.ActualPaperMm,profile.PrintableMm,5);
        var style=new TextStyle("Arial",14,"#000000",false,false,TextAlignment.Left,1.5);
        var source=string.Join("\n",Enumerable.Range(1,55).Select(i=>$"Line {i:D3}: PrintMaster keeps every sentence.")); var chunks=TextLayoutService.Paginate(source,style,safe);
        Assert.True(chunks.Count>=2); var story=Guid.NewGuid(); var pages=chunks.Select((chunk,i)=>
        {
            var page=PrintPageItem.CreateBlank(new(new(148,210),false,SheetMargins.SafeDefault));
            page.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(safe.X,safe.Y,safe.Width,chunk.HeightMm)),chunk.Text,style,Flow:new(story,i,i+1<chunks.Count))); return page.Snapshot();
        }).ToArray();
        var path=Path.Combine(Fixtures.Root,"pm004-text-600dpi-a5.pdf"); if(File.Exists(path)) File.Delete(path);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60)); await new PrintEngine(new(new()),new()).PrintAsync(pages,settings.Snapshot(path),null,timeout.Token);
        for(var i=0;i<100 && !File.Exists(path);i++) await Task.Delay(100);
        var pdf=new PdfiumRenderer(); var printed=pdf.ReadPages(path); Assert.Equal(chunks.Count,printed.Count);
        for(var page=0;page<printed.Count;page++)
        {
            Assert.InRange(printed[page].Width/72*25.4,147,149); Assert.InRange(printed[page].Height/72*25.4,209,211);
            using var raster=pdf.Render(path,page,592,840,0,false,default);
            var text=(TextObject)pages[page].GetSheet().Objects.Single(); var layout=TextLayoutService.Layout(text.Text,style,safe.Width,page+1<printed.Count);
            for(var line=0;line<layout.Lines.Length;line++)
            {
                if(layout.Lines[line].DisplayText.Length==0) continue; var top=(int)((safe.Y+line*layout.LineHeightMm)*4); var bottom=Math.Min(raster.Height,(int)((safe.Y+(line+1)*layout.LineHeightMm)*4)+2); var ink=0;
                for(var y=Math.Max(0,top);y<bottom;y++) for(var x=15;x<raster.Width-15;x++) if(raster.GetPixel(x,y).R<100) ink++;
                Assert.True(ink>20,$"Missing printed text on page {page+1}, line {line+1}.");
            }
            raster.Save(Path.Combine(Fixtures.Root,$"pm004-pdf-page-{page+1}.png"));
        }
    }
}
