using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed class FakeClipboard : IClipboardService
{
    public bool HasSupportedContent()=>Content.Image!=null || !Content.Files.IsDefaultOrEmpty || !string.IsNullOrEmpty(Content.Text)
        || Content.ObjectJson!=null || Content.SelectionArchive!=null || ReadError!=null;
    public ClipboardSnapshot Content { get; set; }=ClipboardSnapshot.Empty;
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public Exception? ReadError { get; set; }
    public Exception? WriteError { get; set; }
    public Task<ClipboardSnapshot> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Reads++; return ReadError!=null ? Task.FromException<ClipboardSnapshot>(ReadError) : Task.FromResult(Content); }
    public Task WriteAsync(ClipboardSnapshot content,CancellationToken token) { token.ThrowIfCancellationRequested(); if(WriteError!=null) return Task.FromException(WriteError); Content=content; Writes++; return Task.CompletedTask; }
}

public sealed class ClipboardTests
{
    public static BitmapSource Image()
    {
        // Alpha, dark gray and white pixels exercise PNG preservation without the system clipboard.
        var bytes=new byte[] {0,0,255,0, 80,80,80,255, 255,255,255,255, 0,0,0,255};
        var image=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,bytes,8); image.Freeze(); return image;
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void EveryObjectTypeCopiesStylesAndGetsAnIndependentIdentity(int kind)
    {
        SheetObject item=kind switch
        {
            0=>new TextObject(Guid.NewGuid(),new(new(10,20,80,40)),"Текст",new("Arial",14,"#123456",true,true,PrintMaster.Models.TextAlignment.Right,1.5),Flow:new(Guid.NewGuid(),0,false)),
            1=>new ShapeObject(Guid.NewGuid(),new(new(10,20,80,40),23),SheetShapeKind.Arrow,new("#175E9C",0.8,0.5)),
            2=>new InkObject(Guid.NewGuid(),new(new(10,20,80,40)),[new(0,0),new(0.5,0.8),new(1,1)],new("#333333",1.2,0.3)),
            _=>new ShapeObject(Guid.NewGuid(),new(new(10,20,80,40)),SheetShapeKind.Ellipse,new("#175E9C",0.8,1,"#EEEEEE"))
        };
        var package=ClipboardObjectService.Deserialize(ClipboardObjectService.Serialize(new(1,item,[]))); var copy=ClipboardObjectService.CloneObject(package.Object!,new(3,3));
        Assert.NotEqual(item.Id,copy.Id); Assert.Equal(item.GetType(),copy.GetType()); Assert.Equal(13,copy.Placement.BoundsMm.X); Assert.Equal(23,copy.Placement.BoundsMm.Y);
        Assert.Equal(item.Placement.RotationDegrees,copy.Placement.RotationDegrees);
        if(copy is TextObject text) { Assert.Equal(((TextObject)item).Style,text.Style); Assert.Null(text.Flow); }
        if(copy is InkObject ink) Assert.True(((InkObject)item).Points.SequenceEqual(ink.Points));
    }
    [Fact]
    public void InvalidOrPathBasedClipboardObjectsAreRejectedBeforeInsertion()
    {
        Assert.ThrowsAny<Exception>(()=>ClipboardObjectService.Deserialize("bad json"));
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Deserialize("{\"version\":999,\"materials\":[]}"));
        var source=new SourceAsset(Guid.NewGuid(),SourceKind.Image,"user.png","secret.png",0,1,new(1000,1000),new(10,10));
        var content=new ContentObject(Guid.NewGuid(),new(new(10,10,20,20)),source.Id,ContentEdits.Empty(source.LogicalSize));
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Serialize(new(1,content,[new(source,".png",[1,2,3])])));
    }
    [Fact]
    public Task LocalDataObjectUsesFileImageTextPriorityAndPreservesPngTransparency()=>OnSta(async ()=>
    {
        var text=new DataObject(); text.SetData(DataFormats.UnicodeText,"# Это обычный текст"); Assert.Equal("# Это обычный текст",ClipboardService.ReadData(text).Text);
        var files=new DataObject(); files.SetData(DataFormats.FileDrop,new[] { "sample.pdf","photo.png" }); files.SetData(DataFormats.UnicodeText,"ignore text");
        Assert.Equal(2,ClipboardService.ReadData(files).Files.Length); Assert.Null(ClipboardService.ReadData(files).Text);
        var image=ClipboardService.CreateData(new([],Image(),"ignore text")); var read=ClipboardService.ReadData(image); Assert.NotNull(read.Image); Assert.Null(read.Text);
        var pixels=new byte[16]; new FormatConvertedBitmap(read.Image!,PixelFormats.Bgra32,null,0).CopyPixels(pixels,8,0); Assert.Equal(0,pixels[3]); Assert.Equal(255,pixels[7]);
        var objectData=ClipboardService.CreateData(new([],Image(),"fallback",ClipboardObjectService.Serialize(new(1,new TextObject(Guid.NewGuid(),new(new(10,10,50,30)),"fallback",new()),[]))));
        Assert.NotNull(ClipboardService.ReadData(objectData).ObjectJson); Assert.Equal("fallback",objectData.GetData(DataFormats.UnicodeText));
        var group=ClipboardService.CreateData(new([],Text:"ignore text",ObjectJson:"ignore object",SelectionArchive:[1,2,3,4],SelectionKind:ClipboardSelectionKind.Pages));
        var groupRead=ClipboardService.ReadData(group); Assert.Equal(new byte[] {1,2,3,4},groupRead.SelectionArchive);
        Assert.Equal(ClipboardSelectionKind.Pages,groupRead.SelectionKind); Assert.Null(groupRead.ObjectJson); Assert.Null(groupRead.Text);
        await Task.CompletedTask;
    });
    [Fact]
    public async Task ClipboardPhotoOnSheetIsOwnedPortableAndUndoRemovesMaterial()
    {
        var clipboard=new FakeClipboard { Content=new([],Image()) }; var dialogs=new TestDialogs(); var vm=CreateVm(dialogs,clipboard);
        try
        {
            vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4)); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Single(vm.Pages); var sheet=vm.SelectedPage!; Assert.True(sheet.IsBlank); var source=sheet.Document.Snapshot().Assets.Single(); Assert.Null(source.OriginalPath); Assert.True(File.Exists(source.WorkingPath));
            var item=(ContentObject)sheet.Document.Objects.Single(); vm.UndoCommand.Execute(null); Assert.Empty(sheet.Document.Objects); Assert.Empty(sheet.Document.Snapshot().Assets); vm.RedoCommand.Execute(null); Assert.Equal(item.Id,sheet.Document.Objects.Single().Id);
            var path=ProjectFileTests.PathFor("pm005-photo"); Assert.True(await vm.SaveProjectToAsync(path));
            using var next=new SessionWorkspace(); var saved=await new ProjectFileService().LoadAsync(path,next,default); Assert.Empty(saved.Missing); Assert.True(File.Exists(saved.Session.Pages.Single().Sheet.Assets.Single().WorkingPath));
            Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task FilesGoToQueueWhileRawPhotosAndMarkdownTextFollowTheirContext()
    {
        var clipboard=new FakeClipboard(); var dialogs=new TestDialogs(); var vm=CreateVm(dialogs,clipboard);
        try
        {
            clipboard.Content=new([Fixtures.CreatePdf("pm005-files.pdf",1),Fixtures.CreateImage("pm005-files.png")]);
            await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); Assert.Equal(2,vm.Pages.Count); Assert.Contains(vm.Pages,p=>p.Kind==SourceKind.Pdf);
            var before=vm.Pages.Count; clipboard.Content=new([],Image()); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Queue); Assert.Equal(before+1,vm.Pages.Count); Assert.True(vm.SelectedPage!.IsImage); Assert.Null(vm.SelectedPage.Document.PrimaryAsset!.OriginalPath);
            clipboard.Content=new([],Text:"# Заголовок\n**буквальный текст**"); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.True(vm.SelectedPage!.IsSheetLayout); Assert.Equal("# Заголовок\n**буквальный текст**",vm.SelectedPage.Document.Objects.OfType<TextObject>().Single().Text);
            Assert.False(vm.SelectedPage.Document.Objects.OfType<TextObject>().Single().Style.Bold); Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task CopiedPhotoSurvivesTheOriginalWorkspaceClosingAndKeepsCropRotationAndAnnotations()
    {
        var clipboard=new FakeClipboard(); var dialogs=new TestDialogs(); var original=CreateVm(dialogs,clipboard);
        try
        {
            original.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4)); clipboard.Content=new([],Image()); await original.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            var item=(ContentObject)original.SelectedPage!.Document.Objects.Single(); var edits=item.Edits with { Crop=new(0,0,500,1000),QuarterTurns=1,Annotations=[Annotation.Create(AnnotationKind.Rectangle,new(40,50),new(200,250),"#111827",4)] };
            original.SelectedPage.Document.ReplaceObject(item with { Edits=edits }); original.SelectedSheetObjectId=item.Id; await original.CopySelectionCommand.ExecuteAsync(null);
            Assert.NotNull(clipboard.Content.ObjectJson); Assert.DoesNotContain(original.SelectedPage.Document.Snapshot().Assets.Single().WorkingPath!,clipboard.Content.ObjectJson!);
        }
        finally { await original.PrepareCloseAsync(); original.Dispose(); }
        var target=CreateVm(dialogs,clipboard);
        try
        {
            target.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4)); await target.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            var copy=(ContentObject)target.SelectedPage!.Document.Objects.Single(); Assert.Equal(new RectD(0,0,500,1000),copy.Edits.Crop); Assert.Equal(1,copy.Edits.QuarterTurns); Assert.Single(copy.Edits.Annotations);
            Assert.True(File.Exists(target.SelectedPage.Document.Snapshot().Assets.Single().WorkingPath)); Assert.Empty(dialogs.Errors);
        }
        finally { await target.PrepareCloseAsync(); target.Dispose(); }
    }
    [Fact]
    public async Task CorruptClipboardAndBusyBufferKeepTheExistingDocument()
    {
        var clipboard=new FakeClipboard { Content=new([],ObjectJson:"invalid") }; var dialogs=new TestDialogs(); var vm=CreateVm(dialogs,clipboard);
        try
        {
            var page=PrintPageItem.CreateBlank(SheetPaper.A4); vm.AddPage(page); var before=page.Document.Snapshot(); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Equal(before,page.Document.Snapshot()); Assert.Single(vm.Pages); Assert.Single(dialogs.Errors); dialogs.Errors.Clear();
            clipboard.ReadError=new InvalidOperationException("Буфер занят"); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto); Assert.Equal(before,page.Document.Snapshot()); Assert.Single(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public async Task LegacyAnnotationsRemainEditableAndCanPasteOnBothImagesAndSheets()
    {
        var clipboard=new FakeClipboard(); var dialogs=new TestDialogs(); var vm=CreateVm(dialogs,clipboard);
        try
        {
            await vm.ImportFilesAsync([Fixtures.CreateImage("pm005-annotation.png")]); var original=Annotation.Create(AnnotationKind.Text,new(100,100),new(500,180),"#175E9C",4,"Аннотация",36); vm.SelectedPage!.Edits.Add(original); vm.SelectedSourceAnnotation=original;
            await vm.CopySelectionCommand.ExecuteAsync(null); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Equal(2,vm.SelectedPage.Edits.Annotations.Count); Assert.NotEqual(original.Id,vm.SelectedPage.Edits.Annotations[1].Id); Assert.Equal(original.Text,vm.SelectedPage.Edits.Annotations[1].Text);
            vm.AddPage(PrintPageItem.CreateBlank(SheetPaper.A4)); vm.SelectedPage=vm.Pages.Last(); await vm.PasteClipboardCommand.ExecuteAsync(PasteDestination.Auto);
            Assert.Equal(original.Text,vm.SelectedPage.Document.Objects.OfType<TextObject>().Single().Text); Assert.Empty(dialogs.Errors);
        }
        finally { await vm.PrepareCloseAsync(); vm.Dispose(); }
    }
    [Fact]
    public Task FrozenDecodedFrameCanBeEncodedOnWorkerWithoutDispatcherMetadata()=>OnSta(async ()=>
    {
        var pixels=new byte[] {0,0,255,255, 0,255,0,0, 255,0,0,255, 128,128,128,255};
        var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(2,2,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,8); bitmap.Freeze();
        using var png=new MemoryStream(); var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); encoder.Save(png); png.Position=0;
        var decoder=new System.Windows.Media.Imaging.PngBitmapDecoder(png,System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        decoder.Frames[0].Freeze();
        using var workspace=new SessionWorkspace();
        var page=await new FileImportService(workspace,new(),new()).ImportClipboardImageAsync(decoder.Frames[0],default);
        using var loaded=new System.Drawing.Bitmap(page.WorkingPath);
        Assert.Equal(2,loaded.Width); Assert.Equal(2,loaded.Height); Assert.Equal(0,loaded.GetPixel(1,0).A); Assert.Equal(255,loaded.GetPixel(0,0).R);
    });
    public static PrintSessionViewModel CreateVm(TestDialogs dialogs,FakeClipboard clipboard)=>new(dialogs,Path.Combine(Fixtures.Root,"pm005-recovery-"+Guid.NewGuid().ToString("N")),clipboard);
    public sealed class TestDialogs : IUserDialogs
    {
        public List<string> Errors { get; }=[];
        public string[] PickFiles()=>[]; public string? PickPdfOutput()=>null; public string? PickLibreOffice()=>null; public bool ConfirmCropping(int count)=>false; public void Error(string message)=>Errors.Add(message);
    }
    private static Task OnSta(Func<Task> body)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async ()=>{ try { await body(); completion.SetResult(); } catch(Exception ex) { completion.SetException(ex); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground=true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}
