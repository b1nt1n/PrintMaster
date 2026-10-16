using PrintMaster.Models;
using PrintMaster.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ColorClipboardCompatibilityTests : IDisposable
{
    private readonly string _root=Path.Combine(Fixtures.Root,"pm010-color-clipboard-"+Guid.NewGuid().ToString("N"));
    private static Annotation Drawing(bool opacity=false,bool fill=false)=>Annotation.Create(AnnotationKind.Rectangle,
        new(10,20),new(150,100),"#204080",4,points:[new(10,20),new(100,80),new(150,100)])
        with { Opacity=opacity ? 0.37 : null,FillColor=fill ? "#A03060" : null };
    private static ClipboardObjectPackage Packet(Annotation annotation,int version=1)=>new(version,null,[],annotation,new(400,300));

    [Theory] [InlineData(false)] [InlineData(true)]
    public void Serialize_PlainAnnotationAndExistingSheetStyleRemainVersionOne(bool sheetObject)
    {
        var annotation=Drawing(); var item=new ShapeObject(Guid.NewGuid(),new(new(10,20,50,40)),SheetShapeKind.Rectangle,
            new("#204080",0.4,0.37,"#A03060"));
        var original=sheetObject ? new ClipboardObjectPackage(1,item,[]) : Packet(annotation);
        var json=ClipboardObjectService.Serialize(original); var restored=ClipboardObjectService.Deserialize(json);
        Assert.Equal(1,JsonNode.Parse(json)!["version"]!.GetValue<int>()); Assert.Equal(1,restored.Version);
        Assert.Equal(1,ClipboardObjectService.AnnotationVersion(annotation)); Assert.Equal(1,original.Version);
        if(sheetObject) Assert.Equal(item,restored.Object);
        else { Assert.Null(restored.Annotation!.Opacity); Assert.Null(restored.Annotation.FillColor); }
    }

    [Theory] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void Serialize_NewAnnotationAppearanceUpgradesVersionAndPreservesStyleGeometryAndPoints(bool opacity,bool fill)
    {
        var annotation=Drawing(opacity,fill); var original=Packet(annotation);
        var json=ClipboardObjectService.Serialize(original); var restored=ClipboardObjectService.Deserialize(json);
        Assert.Equal(2,JsonNode.Parse(json)!["version"]!.GetValue<int>()); Assert.Equal(2,restored.Version);
        Assert.Equal(2,ClipboardObjectService.AnnotationVersion(annotation)); Assert.Equal(1,original.Version);
        Assert.DoesNotContain("effectiveOpacity",json,StringComparison.OrdinalIgnoreCase);
        var copy=restored.Annotation!; Assert.Equal(annotation.Id,copy.Id); Assert.Equal(annotation.Color,copy.Color);
        Assert.Equal(annotation.Opacity,copy.Opacity); Assert.Equal(annotation.FillColor,copy.FillColor);
        Assert.Equal(annotation.Thickness,copy.Thickness); Assert.Equal(annotation.Start,copy.Start); Assert.Equal(annotation.End,copy.End);
        Assert.Equal(annotation.Points.ToArray(),copy.Points.ToArray()); Assert.Equal(new SizeD(400,300),restored.AnnotationSourceSize);
    }

    [Theory]
    [InlineData(false,false,1)] [InlineData(true,false,2)] [InlineData(false,true,2)] [InlineData(true,true,2)]
    public void Capture_ContentPacketUsesAppearanceVersionAndEmbedsMaterialWithoutPrivatePaths(bool opacity,bool fill,int expectedVersion)
    {
        Directory.CreateDirectory(_root); var path=Path.Combine(_root,"private-original.png");
        using(var bitmap=new Bitmap(400,300)) { using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.FromArgb(23,57,119)); bitmap.Save(path,ImageFormat.Png); }
        var bytes=File.ReadAllBytes(path); var sha=SHA256.HashData(bytes);
        var page=PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(400,300),new(100,75)));
        var annotation=Drawing(opacity,fill); page.Edits.Add(annotation); page.Edits.SetCrop(new(5,10,300,230)); page.RotationQuarterTurns=3;
        var snapshot=page.Document.Snapshot(); var captured=ClipboardObjectService.Capture(snapshot,snapshot.PrimaryContent!);
        Assert.Equal(expectedVersion,captured.Version); var material=Assert.Single(captured.Materials);
        Assert.Equal(".png",material.Extension); Assert.Equal(bytes,material.Bytes); Assert.Null(material.Asset.OriginalPath); Assert.Null(material.Asset.WorkingPath);
        var json=ClipboardObjectService.Serialize(captured); Assert.DoesNotContain(path,json,StringComparison.OrdinalIgnoreCase);
        var restored=ClipboardObjectService.Deserialize(json); Assert.Equal(expectedVersion,restored.Version);
        var content=Assert.IsType<ContentObject>(restored.Object); var copy=Assert.Single(content.Edits.Annotations);
        Assert.Equal(new RectD(5,10,300,230),content.Edits.Crop); Assert.Equal(3,content.Edits.QuarterTurns);
        Assert.Equal(annotation.Color,copy.Color); Assert.Equal(annotation.FillColor,copy.FillColor); Assert.Equal(annotation.Opacity,copy.Opacity);
        Assert.Equal(annotation.Points.ToArray(),copy.Points.ToArray()); Assert.Equal(content.AssetId,Assert.Single(restored.Materials).Asset.Id);
        Assert.Same(snapshot,page.Document.Snapshot()); Assert.Equal(sha,SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory] [InlineData(0,false)] [InlineData(-1,false)] [InlineData(6,false)] [InlineData(6,true)]
    public void Clipboard_FutureOrInvalidVersionsAreRejectedWithoutAppearanceDowngrade(int version,bool newAppearance)
    {
        var annotation=Drawing(newAppearance,newAppearance);
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Serialize(Packet(annotation,version)));
        var valid=JsonNode.Parse(ClipboardObjectService.Serialize(Packet(annotation)))!.AsObject(); valid["version"]=version;
        Assert.Throws<InvalidDataException>(()=>ClipboardObjectService.Deserialize(valid.ToJsonString()));
    }

    [Theory] [InlineData(-0.01)] [InlineData(1.01)]
    public void Clipboard_RejectsOpacityOutsideUnitIntervalInBothWriteAndRead(double opacity)
    {
        var annotation=Drawing(true,true); Assert.Throws<ArgumentException>(()=>ClipboardObjectService.Serialize(Packet(annotation with {Opacity=opacity})));
        var valid=JsonNode.Parse(ClipboardObjectService.Serialize(Packet(annotation)))!.AsObject(); valid["annotation"]!["opacity"]=opacity;
        Assert.Throws<ArgumentException>(()=>ClipboardObjectService.Deserialize(valid.ToJsonString()));
    }

    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void Clipboard_RejectsNonFiniteOpacityBeforeJsonSerialization(double opacity)
        =>Assert.Throws<ArgumentException>(()=>ClipboardObjectService.Serialize(Packet(Drawing(true,true) with {Opacity=opacity})));

    [Fact]
    public void Deserialize_LegacyVersionOneMarkerWithoutNewFieldsKeepsExactDefaultOpacity()
    {
        const string legacy="""
            {"version":1,"object":null,"materials":[],"annotationSourceSize":{"width":400,"height":300},
             "annotation":{"id":"f12a7b75-2d89-4f34-93d7-e38ab07ccf17","kind":"Marker","start":{"x":10,"y":20},
               "end":{"x":150,"y":100},"color":"#204080","thickness":4,"text":"","fontSize":36,
               "points":[{"x":10,"y":20},{"x":100,"y":80},{"x":150,"y":100}],"textFormat":null}}
            """;
        var restored=ClipboardObjectService.Deserialize(legacy); var annotation=restored.Annotation!;
        Assert.Equal(1,restored.Version); Assert.Null(annotation.Opacity); Assert.Null(annotation.FillColor);
        Assert.Equal(85d/255,annotation.EffectiveOpacity); Assert.Equal(AnnotationKind.Marker,annotation.Kind);
        Assert.Equal(new[] {new PointD(10,20),new PointD(100,80),new PointD(150,100)},annotation.Points.ToArray());
        Assert.Equal(1,JsonNode.Parse(ClipboardObjectService.Serialize(restored))!["version"]!.GetValue<int>());
    }

    [Fact]
    public void ClipboardFormatNameRemainsStableWhileVersionFieldGatesNewAppearance()
    {
        Assert.Equal("PrintMaster.Object.v1",ClipboardService.ObjectFormat);
        var json=ClipboardObjectService.Serialize(Packet(Drawing(true,true)));
        Assert.Equal(2,JsonNode.Parse(json)!["version"]!.GetValue<int>());
        // The unchanged private format still reaches an older reader, which must reject this version.
        Assert.NotEqual(1,ClipboardObjectService.Deserialize(json).Version);
    }

    public void Dispose()
    {
        if(!Directory.Exists(_root)) return;
        var target=Path.GetFullPath(_root); var allowed=Path.GetFullPath(Fixtures.Root)+Path.DirectorySeparatorChar;
        if(!target.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Небезопасный путь временного тестового каталога.");
        Directory.Delete(target,true);
    }
}
