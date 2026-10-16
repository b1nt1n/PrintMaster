using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ProjectFileTests
{
    internal static ProjectSession Session(params PrintPageItem[] pages)=>new(
        new("HP LaserJet Professional P1102",PaperPreset.Notebook,165,205,true,ScaleMode.Fill,MonochromeMode.Photo,300,190),
        new(pages.FirstOrDefault()?.Id,80,false,true,"#C62828",7,44,16d/9,600),
        pages.Select(p=>new ProjectPage(p.Document.Snapshot(),p.IsIncluded,p.Copies) {PrintOptions=p.PrintOptions}).ToImmutableArray());
    internal static string PathFor(string label)=>Path.Combine(Fixtures.Root,label+"-"+Guid.NewGuid().ToString("N")+ProjectFileService.Extension);

    [Fact] public async Task PortableProjectRestoresAllObjectsSettingsQueueAndOriginalPixels()
    {
        var source=Fixtures.CreateImage("проект-исходник.png");
        var image=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,source,source,0,1,new(1000,750),new(200,150)));
        image.Copies=3; image.Edits.Add(Annotation.Create(AnnotationKind.Text,new(100,100),new(400,200),"#000000",3,"Русский текст",36)); image.Edits.SetCrop(new(50,70,800,600)); image.RotationQuarterTurns=1;
        image.Document.AddObject(new ShapeObject(Guid.NewGuid(),new(new(10,20,40,30),15),SheetShapeKind.Ellipse,new("#175E9C",0.7,0.5,"#CCCCCC")));
        image.Document.AddObject(new TextObject(Guid.NewGuid(),new(new(20,70,100,40)),"Текст листа",new(FontSizePt:18,Bold:true)));
        image.Document.AddObject(new InkObject(Guid.NewGuid(),new(new(20,120,70,40)),[new(0,0),new(0.5,1),new(1,0)],new()));
        var blank=PrintPageItem.CreateBlank(new(new(165,205),true,new(4,5,6,7))); blank.IsIncluded=false;
        var original=Session(blank,image); var path=PathFor("portable"); var service=new ProjectFileService();
        await service.SaveAsync(original,path,true,default); using var workspace=new SessionWorkspace(); var restored=await service.LoadAsync(path,workspace,default);
        Assert.Empty(restored.Missing); Assert.Equal(original.Print,restored.Session.Print); Assert.Equal(original.Editor,restored.Session.Editor);
        Assert.Equal(new[] {blank.Id,image.Id},restored.Session.Pages.Select(p=>p.Sheet.Id)); Assert.False(restored.Session.Pages[0].IsIncluded); Assert.Equal(3,restored.Session.Pages[1].Copies);
        var sheet=restored.Session.Pages[1].Sheet; Assert.Equal(4,sheet.Objects.Length); Assert.True(sheet.Objects[1] is ShapeObject); Assert.True(sheet.Objects[2] is TextObject); Assert.True(sheet.Objects[3] is InkObject);
        Assert.Equal("Русский текст",sheet.PrimaryContent!.Edits.Annotations[0].Text); Assert.Equal(new RectD(50,70,800,600),sheet.PrimaryContent.Edits.Crop); Assert.Equal(1,sheet.PrimaryContent.Edits.QuarterTurns);
        Assert.NotEqual(source,sheet.PrimaryAsset!.WorkingPath); Assert.Equal(File.ReadAllBytes(source),File.ReadAllBytes(sheet.PrimaryAsset.WorkingPath!));
        Assert.False(SheetDocument.Restore(sheet).CanUndo);
    }
    [Fact] public async Task ProjectCanMoveWithoutOriginalAndWithoutAnOriginalPath()
    {
        using var sourceWorkspace=new SessionWorkspace(); var directory=sourceWorkspace.NewImportDirectory();
        var source=Path.Combine(directory,"clipboard.png"); File.Copy(Fixtures.CreateImage("clipboard-seed.png"),source);
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,null,source,0,1,new(1000,750),new(200,150)));
        var path=PathFor("without-original"); var service=new ProjectFileService(); await service.SaveAsync(Session(page),path,true,default); sourceWorkspace.Dispose();
        using var destination=new SessionWorkspace(); var restored=await service.LoadAsync(path,destination,default);
        Assert.Empty(restored.Missing); Assert.Null(restored.Session.Pages[0].Sheet.PrimaryAsset!.OriginalPath);
        Assert.True(File.Exists(restored.Session.Pages[0].Sheet.PrimaryAsset!.WorkingPath));
    }
    [Fact] public async Task SharedPdfIsEmbeddedOnceForMultiplePages()
    {
        var source=Fixtures.CreatePdf(); var pages=new[] {0,1}.Select(i=>PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Pdf,source,source,i,2,new(595.276,841.89),new(210,297)))).ToArray();
        var service=new ProjectFileService(); var path=PathFor("deduplicated"); await service.SaveAsync(Session(pages),path,true,default);
        using(var archive=ZipFile.OpenRead(path)) Assert.Single(archive.Entries,e=>e.FullName.StartsWith("assets/"));
        using var workspace=new SessionWorkspace(); var loaded=await service.LoadAsync(path,workspace,default); Assert.Equal(2,loaded.Session.Pages.Length); Assert.Empty(loaded.Missing);
        Assert.Equal(loaded.Session.Pages[0].Sheet.PrimaryAsset!.WorkingPath,loaded.Session.Pages[1].Sheet.PrimaryAsset!.WorkingPath);
    }
    [Fact] public async Task MissingReferencedFileCanBeRelinkedWithoutLosingEdits()
    {
        using var sourceWorkspace=new SessionWorkspace(); var source=Path.Combine(sourceWorkspace.NewImportDirectory(),"reference.png"); File.Copy(Fixtures.CreateImage(),source);
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,source,source,0,1,new(1000,750),new(200,150))); page.RotationQuarterTurns=2;
        var service=new ProjectFileService(); var path=PathFor("referenced"); await service.SaveAsync(Session(page),path,false,default); File.Delete(source);
        using var workspace=new SessionWorkspace(); var missing=await service.LoadAsync(path,workspace,default); Assert.Single(missing.Missing); Assert.Equal(2,missing.Session.Pages[0].Sheet.PrimaryContent!.Edits.QuarterTurns);
        var replacement=Fixtures.CreateImage("reference-replacement.png"); var restored=await service.LoadAsync(path,workspace,default,new Dictionary<string,string>{{missing.Missing[0].ResourceId,replacement}});
        Assert.Empty(restored.Missing); Assert.Equal(2,restored.Session.Pages[0].Sheet.PrimaryContent!.Edits.QuarterTurns);
    }
    [Fact] public async Task MissingSourcesStayInASavedProject()
    {
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,@"C:\missing\image.png",null,0,1,new(1000,750),new(200,150)));
        var path=PathFor("missing"); var service=new ProjectFileService(); await service.SaveAsync(Session(page),path,true,default);
        using var workspace=new SessionWorkspace(); var loaded=await service.LoadAsync(path,workspace,default); Assert.Single(loaded.Missing); Assert.Single(loaded.Session.Pages);
    }
    [Fact] public async Task CanceledSavePreservesPreviousProject()
    {
        var page=PrintPageItem.CreateBlank(SheetPaper.A4); var service=new ProjectFileService(); var path=PathFor("atomic"); await service.SaveAsync(Session(page),path,true,default);
        var before=File.ReadAllBytes(path); using var cancel=new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.SaveAsync(Session(page),path,true,cancel.Token)); Assert.Equal(before,File.ReadAllBytes(path));
    }
    [Theory] [InlineData("version")] [InlineData("path")] [InlineData("copies")] [InlineData("kind")]
    public async Task InvalidProjectIsRejected(string mutation)
    {
        var image=Fixtures.CreateImage(); var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,image,image,0,1,new(1000,750),new(200,150)));
        var path=PathFor("invalid"); var service=new ProjectFileService(); await service.SaveAsync(Session(page),path,true,default);
        using(var archive=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var entry=archive.GetEntry("manifest.json")!; JsonObject node;
            using(var reader=new StreamReader(entry.Open())) node=JsonNode.Parse(reader.ReadToEnd())!.AsObject(); entry.Delete();
            switch(mutation)
            {
                case "version": node["version"]=999; break;
                case "path": node["resources"]![0]!["archiveEntry"]="../../escape.png"; break;
                case "copies": node["session"]!["pages"]![0]!["copies"]=0; break;
                case "kind": node["session"]!["pages"]![0]!["sheet"]!["objects"]![0]!["$kind"]="executable"; break;
            }
            using var writer=new StreamWriter(archive.CreateEntry("manifest.json").Open()); writer.Write(node.ToJsonString());
        }
        using var workspace=new SessionWorkspace(); await Assert.ThrowsAnyAsync<Exception>(()=>service.LoadAsync(path,workspace,default));
    }
    [Fact] public async Task CorruptEmbeddedMaterialIsRejected()
    {
        var image=Fixtures.CreateImage(); var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,image,image,0,1,new(1000,750),new(200,150)));
        var path=PathFor("corrupt"); var service=new ProjectFileService(); await service.SaveAsync(Session(page),path,true,default);
        using(var archive=ZipFile.Open(path,ZipArchiveMode.Update)) { var entry=archive.Entries.Single(e=>e.FullName.StartsWith("assets/")); var name=entry.FullName; var size=entry.Length; entry.Delete(); using var data=archive.CreateEntry(name).Open(); data.Write(new byte[size]); }
        using var workspace=new SessionWorkspace(); await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadAsync(path,workspace,default));
    }
    [Fact] public async Task RecoveryOnlyOffersAnInactiveSession()
    {
        var directory=Path.Combine(Fixtures.Root,"recovery-"+Guid.NewGuid().ToString("N")); var service=new ProjectFileService();
        var first=new ProjectRecoveryStore(service,directory); using var second=new ProjectRecoveryStore(service,directory);
        await first.WriteAsync(Session(PrintPageItem.CreateBlank(SheetPaper.A4)),default); Assert.Null(second.FindLatest()); var path=first.RecoveryPath; first.Dispose();
        Assert.Equal(path,second.FindLatest()); using var workspace=new SessionWorkspace(); var restored=await service.LoadAsync(path,workspace,default); Assert.Single(restored.Session.Pages);
        second.Dismiss(path); Assert.Null(second.FindLatest());
    }
    [Fact] [Trait("Category","Integration")]
    public async Task DocxProjectRestoresPreparedPdfAfterTheOriginalSessionIsRemoved()
    {
        var service=new ProjectFileService(); var path=PathFor("docx-prepared"); ProjectSession session;
        using(var imported=new SessionWorkspace())
        {
            var pages=await new FileImportService(imported,new(),new()).ImportAsync(Fixtures.CreateDocx(),default);
            session=Session(pages.ToArray()); await service.SaveAsync(session,path,false,default);
        }
        using var opened=new SessionWorkspace(); var restored=await service.LoadAsync(path,opened,default);
        Assert.Empty(restored.Missing); Assert.Equal(2,restored.Session.Pages.Length); Assert.All(restored.Session.Pages,p=>Assert.Equal(SourceKind.Docx,p.Sheet.PrimaryAsset!.Kind));
        Assert.All(restored.Session.Pages,p=>Assert.Equal(".pdf",Path.GetExtension(p.Sheet.PrimaryAsset!.WorkingPath)));
        using var bitmap=new PageRasterizer(new()).Render(new PrintPageItem(SheetDocument.Restore(restored.Session.Pages[0].Sheet)).Snapshot(),595,842,false,default);
        Assert.Equal(595,bitmap.Width);
    }
    [Fact] public async Task RestoredSceneRendersTheSamePixels()
    {
        var doc=SheetDocument.CreateBlank(SheetPaper.A4);
        doc.AddObject(new TextObject(Guid.NewGuid(),new(new(20,20,100,30)),"Проект сохранен",new(FontSizePt:18,Bold:true)));
        doc.AddObject(new ShapeObject(Guid.NewGuid(),new(new(20,70,80,30),15),SheetShapeKind.Rectangle,new("#C62828",0.5,0.7,"#CCCCCC")));
        var source=new PrintPageItem(doc); var service=new ProjectFileService(); var path=PathFor("render-equality"); await service.SaveAsync(Session(source),path,true,default);
        using var workspace=new SessionWorkspace(); var restored=await service.LoadAsync(path,workspace,default); var rasterizer=new PageRasterizer(new());
        using var first=rasterizer.RenderSheet(doc.Snapshot(),120,false,default); using var second=rasterizer.RenderSheet(restored.Session.Pages[0].Sheet,120,false,default);
        var a=BitmapBridge.ToWpf(first); var b=BitmapBridge.ToWpf(second); var stride=a.PixelWidth*4; var aa=new byte[stride*a.PixelHeight]; var bb=new byte[aa.Length];
        a.CopyPixels(aa,stride,0); b.CopyPixels(bb,stride,0); Assert.Equal(aa,bb);
    }
    [Fact] public async Task FailedLateSaveCannotOverwriteThePreviousFile()
    {
        var service=new ProjectFileService(); var path=PathFor("failed-save"); await service.SaveAsync(Session(PrintPageItem.CreateBlank(SheetPaper.A4)),path,true,default);
        var before=File.ReadAllBytes(path); using var workspace=new SessionWorkspace(); var material=Path.Combine(workspace.NewImportDirectory(),"too-large.png");
        using(var file=File.Create(material)) file.SetLength(ProjectFileService.MaxResourceBytes+1);
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,material,material,0,1,new(1000,750),new(200,150)));
        await Assert.ThrowsAsync<IOException>(()=>service.SaveAsync(Session(page),path,true,default)); Assert.Equal(before,File.ReadAllBytes(path));
    }
    [Fact] public async Task WrongReplacementAspectDoesNotReplaceTheSource()
    {
        var source=Fixtures.CreateImage("relink-original.png"); var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,source,source,0,1,new(1000,750),new(200,150));
        using var workspace=new SessionWorkspace(); var wrong=Path.Combine(workspace.NewImportDirectory(),"square.png");
        using(var bitmap=new System.Drawing.Bitmap(100,100)) bitmap.Save(wrong,System.Drawing.Imaging.ImageFormat.Png);
        await Assert.ThrowsAsync<InvalidDataException>(()=>new ProjectFileService().RelinkAsync(asset,wrong,workspace,default)); Assert.Equal(source,asset.WorkingPath);
    }
}
