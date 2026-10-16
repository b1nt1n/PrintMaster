using PrintMaster.Models;
using PrintMaster.Services;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class MarkdownPersistenceTests
{
    private static MarkdownDocument Definition(params MarkdownImageReference[] images)=>new(Guid.NewGuid(),
        "# Русский заголовок\n\n$$\\int x^n \\, dx=\\frac{x^{n+1}}{n+1}+C$$\n\n![Фото](photo.png)",
        new(new(FontSizePt:13,LineSpacing:1.2,Bold:true),new(11,12,13,14),3),images.ToImmutableArray(),"Математика");
    private static SourceAsset Asset(string path)=>new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(1000,750),new(200,150));
    private static MarkdownObject Object(MarkdownDocument document,int page)=>new(Guid.NewGuid(),new(new(0,0,210,297)),document.Id,page) {Document=document};
    private static ProjectSession Session(MarkdownDocument document,ImmutableArray<SourceAsset> assets=default,int count=2)
    {
        if(assets.IsDefault) assets=[];
        var pages=Enumerable.Range(0,count).Select(i=>new ProjectPage(new(Guid.NewGuid(),SheetPaper.A4,assets,[Object(document,i)],null),i==0,i+1)).ToImmutableArray();
        return new(ProjectPrintSettings.Capture(new PrintSettings()),new(pages[0].Sheet.Id,50,true,false,"#111827",4,36,0,300),pages)
            {MarkdownDocuments=[document]};
    }
    private static async Task MutateManifest(string path,Action<JsonObject> mutation)
    {
        using var archive=ZipFile.Open(path,ZipArchiveMode.Update); var entry=archive.GetEntry("manifest.json")!; JsonObject json;
        using(var reader=new StreamReader(entry.Open())) json=JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        mutation(json); entry.Delete(); using var writer=new StreamWriter(archive.CreateEntry("manifest.json").Open()); await writer.WriteAsync(json.ToJsonString());
    }

    [Fact] public async Task PortablePagesWriteOneOriginalAndHydrateOneSharedDefinitionWithOriginalImages()
    {
        var path=Fixtures.CreateImage("md-persistence-image.png"); var first=Asset(path); var second=Asset(path);
        var document=Definition(new("photo.png",first.Id),new("other.png",second.Id)); var session=Session(document,[first,second]);
        var destination=ProjectFileTests.PathFor("md-portable"); var service=new ProjectFileService(); await service.SaveAsync(session,destination,true,default);
        using(var archive=ZipFile.OpenRead(destination))
        {
            Assert.Single(archive.Entries,e=>e.FullName.StartsWith("assets/",StringComparison.Ordinal));
            using var reader=new StreamReader(archive.GetEntry("manifest.json")!.Open()); var manifest=JsonNode.Parse(await reader.ReadToEndAsync())!;
            Assert.Equal(10,manifest["version"]!.GetValue<int>()); var definitions=manifest["session"]!["markdownDocuments"]!.AsArray(); Assert.Single(definitions);
            Assert.Equal(document.Source,definitions[0]!["source"]!.GetValue<string>());
            foreach(var page in manifest["session"]!["pages"]!.AsArray())
            {var item=page!["sheet"]!["objects"]![0]!.AsObject(); Assert.Equal("markdown",item["$kind"]!.GetValue<string>()); Assert.False(item.ContainsKey("document")); Assert.False(item.ContainsKey("source"));}
        }
        using var workspace=new SessionWorkspace(); var loaded=await service.LoadAsync(destination,workspace,default,embeddedOnly:true);
        Assert.Empty(loaded.Missing); var restored=Assert.Single(loaded.Session.MarkdownDocuments);
        Assert.Equal(document.Source,restored.Source); Assert.Equal(document.Style,restored.Style); Assert.Equal(document.Title,restored.Title);
        Assert.Equal(document.Images.ToArray(),restored.Images.ToArray()); Assert.Equal(new[] {true,false},loaded.Session.Pages.Select(p=>p.IsIncluded)); Assert.Equal(new[] {1,2},loaded.Session.Pages.Select(p=>p.Copies));
        foreach(var page in loaded.Session.Pages)
        {
            var item=Assert.IsType<MarkdownObject>(Assert.Single(page.Sheet.Objects)); Assert.Same(restored,item.Document); Assert.Equal(restored.Id,item.DocumentId);
            foreach(var asset in page.Sheet.Assets) {Assert.NotEqual(path,asset.WorkingPath); Assert.Equal(File.ReadAllBytes(path),File.ReadAllBytes(asset.WorkingPath!));}
        }
        Assert.Equal(loaded.Session.Pages[0].Sheet.Assets[0].WorkingPath,loaded.Session.Pages[1].Sheet.Assets[1].WorkingPath);
        Assert.Same(document,((MarkdownObject)session.Pages[0].Sheet.Objects[0]).Document);
    }

    [Fact] public async Task SaveCollectsTransientDefinitionWhenSessionWasConstructedFromSubset()
    {
        var document=Definition(); var unrelated=Definition() with {Source="Unused"}; var session=Session(document) with {MarkdownDocuments=[unrelated]};
        var path=ProjectFileTests.PathFor("md-subset"); var service=new ProjectFileService(); await service.SaveAsync(session,path,true,default);
        using var workspace=new SessionWorkspace(); var loaded=await service.LoadAsync(path,workspace,default);
        Assert.Equal(document.Id,Assert.Single(loaded.Session.MarkdownDocuments).Id); Assert.Equal("Unused",Assert.Single(session.MarkdownDocuments).Source);
    }

    [Fact] public void CollectDefinitionsUsesExplicitFallbackForUnhydratedPagesAndRejectsConflictingOriginals()
    {
        var document=Definition(); var session=Session(document); var first=session.Pages[0]; var item=(MarkdownObject)first.Sheet.Objects[0];
        var unhydrated=first with {Sheet=first.Sheet with {Objects=[item with {Document=null}]}};
        Assert.Same(document,Assert.Single(ProjectFileService.CollectMarkdownDocuments([unhydrated],[document])));
        Assert.Throws<InvalidDataException>(()=>ProjectFileService.CollectMarkdownDocuments([unhydrated]));
        var different=first with {Sheet=first.Sheet with {Objects=[item with {Document=document with {Source="Changed source"}}]}};
        Assert.Throws<InvalidDataException>(()=>ProjectFileService.CollectMarkdownDocuments([first,different]));
        Assert.Throws<InvalidDataException>(()=>ProjectFileService.CollectMarkdownDocuments([first],[document,document]));
    }

    [Fact] public async Task MissingReferenceRetainsMarkdownAndImageMappingsUntilRelink()
    {
        using var sourceWorkspace=new SessionWorkspace(); var source=Path.Combine(sourceWorkspace.NewImportDirectory(),"source.png"); File.Copy(Fixtures.CreateImage(),source);
        var asset=Asset(source); var document=Definition(new MarkdownImageReference("photo.png",asset.Id)); var path=ProjectFileTests.PathFor("md-missing"); var service=new ProjectFileService();
        await service.SaveAsync(Session(document,[asset]),path,false,default); sourceWorkspace.Dispose();
        using var destination=new SessionWorkspace(); var missing=await service.LoadAsync(path,destination,default);
        Assert.Equal(2,missing.Missing.Length); Assert.Equal(document.Source,Assert.Single(missing.Session.MarkdownDocuments).Source);
        Assert.All(missing.Session.Pages,p=>Assert.Null(Assert.Single(p.Sheet.Assets).WorkingPath));
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.LoadAsync(path,destination,default,embeddedOnly:true));
        var replacement=Fixtures.CreateImage("md-replacement.png"); var relinked=await service.LoadAsync(path,destination,default,new Dictionary<string,string> {{missing.Missing[0].ResourceId,replacement}});
        Assert.Empty(relinked.Missing); Assert.All(relinked.Session.Pages,p=>Assert.True(File.Exists(Assert.Single(p.Sheet.Assets).WorkingPath)));
        Assert.Equal(document.Images.ToArray(),Assert.Single(relinked.Session.MarkdownDocuments).Images.ToArray());
    }

    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task LegacyProjectVersionsWithoutMarkdownStillOpen(int version)
    {
        var session=ProjectFileTests.Session(PrintPageItem.CreateBlank(SheetPaper.A4)); var path=ProjectFileTests.PathFor("md-legacy"); var service=new ProjectFileService();
        await service.SaveAsync(session,path,true,default); await MutateManifest(path,json=>{json["version"]=version;json["session"]!.AsObject().Remove("markdownDocuments");});
        using var workspace=new SessionWorkspace(); var loaded=await service.LoadAsync(path,workspace,default); Assert.Empty(loaded.Session.MarkdownDocuments); Assert.Single(loaded.Session.Pages);
    }

    [Theory] [InlineData("missing-definition")] [InlineData("duplicate-definition")] [InlineData("missing-image")]
    [InlineData("wrong-document-id")] [InlineData("negative-page")] [InlineData("too-many-pages")] [InlineData("legacy-version")]
    public async Task InvalidMarkdownLinksAreRejectedBeforeResourcesAreUsed(string mutation)
    {
        var document=Definition(); var path=ProjectFileTests.PathFor("md-invalid"); var service=new ProjectFileService(); await service.SaveAsync(Session(document),path,true,default);
        await MutateManifest(path,json=>
        {
            var session=json["session"]!;var definitions=session["markdownDocuments"]!.AsArray();var item=session["pages"]![0]!["sheet"]!["objects"]![0]!;
            switch(mutation)
            {
                case "missing-definition":definitions.Clear();break;
                case "duplicate-definition":definitions.Add(definitions[0]!.DeepClone());break;
                case "missing-image":definitions[0]!["images"]!.AsArray().Add(new JsonObject { ["url"]="missing.png",["assetId"]=Guid.NewGuid().ToString() });break;
                case "wrong-document-id":item["documentId"]=Guid.NewGuid().ToString();break;
                case "negative-page":item["pageIndex"]=-1;break;
                case "too-many-pages":item["pageIndex"]=TextLimits.MaxPages;break;
                case "legacy-version":json["version"]=6;break;
            }
        });
        using var workspace=new SessionWorkspace(); await Assert.ThrowsAnyAsync<Exception>(()=>service.LoadAsync(path,workspace,default));
    }

    [Theory] [InlineData("id")] [InlineData("source")] [InlineData("font")] [InlineData("font-size")] [InlineData("line-spacing")]
    [InlineData("margin")] [InlineData("gap")] [InlineData("duplicate-url")] [InlineData("image-id")]
    public void InvalidMarkdownSourceAndStyleAreRejected(string invalid)
    {
        var document=Definition(); document=invalid switch
        {
            "id"=>document with {Id=Guid.Empty},"source"=>document with {Source=new('x',TextLimits.MaxCharacters+1)},
            "font"=>document with {Style=document.Style with {Body=document.Style.Body with {FontFamily=""}}},
            "font-size"=>document with {Style=document.Style with {Body=document.Style.Body with {FontSizePt=double.NaN}}},
            "line-spacing"=>document with {Style=document.Style with {Body=document.Style.Body with {LineSpacing=0.5}}},
            "margin"=>document with {Style=document.Style with {Margins=new(-1,0,0,0)}},
            "gap"=>document with {Style=document.Style with {ParagraphGapMm=double.PositiveInfinity}},
            "duplicate-url"=>document with {Images=[new("same.png",Guid.NewGuid()),new("same.png",Guid.NewGuid())]},
            "image-id"=>document with {Images=[new("image.png",Guid.Empty)]},_=>document
        };
        Assert.ThrowsAny<Exception>(()=>ProjectFileService.ValidateMarkdownDocument(document));
    }

    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ClipboardMarkdownAutomaticallyUsesVersionFourAndHydratesOriginalWithoutDuplication(int requestedVersion)
    {
        var document=Definition(); var item=Object(document,1);var package=new ClipboardObjectPackage(requestedVersion,item,[]);
        var json=ClipboardObjectService.Serialize(package);var restored=ClipboardObjectService.Deserialize(json);
        Assert.Equal(5,restored.Version); Assert.Equal(requestedVersion,package.Version); var definition=Assert.Single(restored.MarkdownDocuments);
        var copy=Assert.IsType<MarkdownObject>(restored.Object); Assert.Same(definition,copy.Document); Assert.Equal(document.Source,definition.Source); Assert.Equal(document.Style,definition.Style);
        Assert.False(JsonNode.Parse(json)!["object"]!.AsObject().ContainsKey("document")); Assert.Equal(1,copy.PageIndex);
        var clone=Assert.IsType<MarkdownObject>(ClipboardObjectService.CloneObject(copy,new(3,4)));Assert.NotEqual(copy.Id,clone.Id);Assert.Equal(copy.DocumentId,clone.DocumentId);
        Assert.Same(copy.Document,clone.Document); Assert.Equal(3,clone.Placement.BoundsMm.X);Assert.Equal(4,clone.Placement.BoundsMm.Y);Assert.True(clone.Locked);
    }

    [Fact] public void ClipboardMarkdownEmbedsAllImagesWithNoWorkingPathsAndNoOriginalMutation()
    {
        var path=Fixtures.CreateImage("md-clipboard.png");var first=Asset(path);var second=Asset(path);
        var document=Definition(new("photo.png",first.Id),new("other.png",second.Id));var sheet=Session(document,[first,second]).Pages[0].Sheet;
        var captured=ClipboardObjectService.Capture(sheet,sheet.Objects[0]);var restored=ClipboardObjectService.Deserialize(ClipboardObjectService.Serialize(captured));
        Assert.Equal(5,captured.Version);Assert.Equal(2,restored.Materials.Length);Assert.Single(restored.MarkdownDocuments);
        Assert.All(restored.Materials,m=>{Assert.Null(m.Asset.WorkingPath);Assert.Null(m.Asset.OriginalPath);Assert.Equal(File.ReadAllBytes(path),m.Bytes);});
        Assert.Same(document,((MarkdownObject)sheet.Objects[0]).Document);Assert.Equal(path,sheet.Assets[0].WorkingPath);
    }

    [Theory] [InlineData(1)] [InlineData(2)]
    public void LegacyClipboardWithoutMarkdownPropertyStillOpensAndKeepsItsVersion(int version)
    {
        var text=new TextObject(Guid.NewGuid(),new(new(10,10,50,30)),"Обычный текст",new());var json=JsonNode.Parse(ClipboardObjectService.Serialize(new(version,text,[])))!.AsObject();json.Remove("markdownDocuments");
        var restored=ClipboardObjectService.Deserialize(json.ToJsonString());Assert.Equal(version,restored.Version);Assert.Empty(restored.MarkdownDocuments);Assert.Equal(text,restored.Object);
    }

    [Theory] [InlineData(0)] [InlineData(6)] [InlineData(999)]
    public void InvalidAndFutureClipboardVersionsAreNeverUpgraded(int version)
    {
        var document=Definition();var item=Object(document,0);
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Serialize(new(version,item,[]) {MarkdownDocuments=[document]}));
        var json=JsonNode.Parse(ClipboardObjectService.Serialize(new(3,item,[]) {MarkdownDocuments=[document]}))!;json["version"]=version;
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Deserialize(json.ToJsonString()));
    }

    [Theory] [InlineData("missing-definition")] [InlineData("duplicate-definition")] [InlineData("wrong-document-id")] [InlineData("legacy-version")]
    public void CorruptMarkdownClipboardCannotSilentlyLoseSource(string mutation)
    {
        var document=Definition();var json=JsonNode.Parse(ClipboardObjectService.Serialize(new(3,Object(document,0),[]) {MarkdownDocuments=[document]}))!;
        switch(mutation)
        {
            case "missing-definition":json["markdownDocuments"]!.AsArray().Clear();break;
            case "duplicate-definition":json["markdownDocuments"]!.AsArray().Add(json["markdownDocuments"]![0]!.DeepClone());break;
            case "wrong-document-id":json["object"]!["documentId"]=Guid.NewGuid().ToString();break;
            case "legacy-version":json["version"]=2;break;
        }
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Deserialize(json.ToJsonString()));
    }
}
