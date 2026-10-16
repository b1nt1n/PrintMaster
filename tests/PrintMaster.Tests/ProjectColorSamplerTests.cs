using PrintMaster.Models;
using PrintMaster.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ProjectColorSamplerTests : IDisposable
{
    private readonly string _root=Path.Combine(Fixtures.Root,"pm010-sampler-"+Guid.NewGuid().ToString("N"));
    private readonly ProjectColorSampler _sampler=new();
    private static readonly string[] QuadrantColors=["#FF0000","#00FF00","#0000FF","#FFD700"];
    private static readonly PointD[] QuadrantPoints=[new(100,50),new(300,50),new(100,150),new(300,150)];

    private string Image(bool quadrants=true,Color? solid=null)
    {
        Directory.CreateDirectory(_root); var path=Path.Combine(_root,Guid.NewGuid().ToString("N")+".png");
        using var bitmap=new Bitmap(400,200,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(bitmap))
        {
            g.Clear(solid ?? Color.White);
            if(quadrants)
            {
                g.FillRectangle(Brushes.Red,0,0,200,100); g.FillRectangle(Brushes.Lime,200,0,200,100);
                g.FillRectangle(Brushes.Blue,0,100,200,100); g.FillRectangle(Brushes.Gold,200,100,200,100);
            }
        }
        bitmap.Save(path,ImageFormat.Png); return path;
    }
    private PrintPageItem Photo(string path)=>PrintPageItem.FromSource(new(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(400,200),new(100,50)));
    private static ShapeObject Rectangle(RectD bounds,string color,double opacity=1,bool visible=true)
        =>new(Guid.NewGuid(),new(bounds),SheetShapeKind.Rectangle,new(color,0.2,opacity,color),visible);

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SampleSource_CroppedQuarterTurnsUseOriginalLogicalCoordinates(int turns)
    {
        var page=Photo(Image()); page.Edits.SetCrop(new(40,20,320,160)); page.RotationQuarterTurns=turns;
        var snapshot=page.Snapshot(); Assert.False(snapshot.IsComposed);
        for(var i=0;i<QuadrantPoints.Length;i++) Assert.Equal(QuadrantColors[i],_sampler.SampleSource(snapshot,QuadrantPoints[i],default));
    }

    [Fact]
    public void SampleSource_IncludesAnnotationsButNeverChangesCropRotationHistoryOrMaterial()
    {
        var page=Photo(Image()); page.Edits.SetCrop(new(40,20,320,160)); page.RotationQuarterTurns=3;
        page.Edits.Add(Annotation.Create(AnnotationKind.Rectangle,new(60,30),new(140,80),"#239ACD",2) with { FillColor="#239ACD",Opacity=1 });
        var before=page.Document.Snapshot(); var revision=page.Edits.Revision; var bytes=SHA256.HashData(File.ReadAllBytes(page.WorkingPath));
        Assert.Equal("#239ACD",_sampler.SampleSource(page.Snapshot(),new(100,50),default));
        Assert.Equal("#FFD700",_sampler.SampleSource(page.Snapshot(),new(300,150),default));
        Assert.Same(before,page.Document.Snapshot()); Assert.Equal(revision,page.Edits.Revision);
        Assert.Equal(new RectD(40,20,320,160),page.Edits.Crop); Assert.Equal(3,page.RotationQuarterTurns);
        Assert.Equal(bytes,SHA256.HashData(File.ReadAllBytes(page.WorkingPath)));
        var moved=page.WorkingPath+".moved"; File.Move(page.WorkingPath,moved); // The sampler releases every image stream.
        Assert.True(File.Exists(moved));
    }

    [Theory]
    [InlineData(0,0)] [InlineData(1,90)] [InlineData(2,180)] [InlineData(3,270)]
    public void SampleSheet_RotatedStretchedPhotoKeepsAllFourColorRegions(int turns,double angle)
    {
        var page=Photo(Image()); var document=page.Document; var content=document.PrimaryContent!;
        var bounds=new RectD(50,70,120,80); document.SetPaper(new(new(250,240),false,SheetMargins.SafeDefault));
        document.SetPlacement(content.Id,new(bounds,angle)); document.SetContentRotation(turns);
        var snapshot=document.Snapshot(); Assert.True(snapshot.IsComposed);
        var radians=angle*Math.PI/180; var cx=bounds.X+bounds.Width/2; var cy=bounds.Y+bounds.Height/2;
        for(var i=0;i<QuadrantPoints.Length;i++)
        {
            var u=QuadrantPoints[i].X/400; var v=QuadrantPoints[i].Y/200;
            (u,v)=turns switch {1=>(1-v,u),2=>(1-u,1-v),3=>(v,1-u),_=>(u,v)};
            var x=bounds.X+u*bounds.Width; var y=bounds.Y+v*bounds.Height;
            var location=new PointD(cx+(x-cx)*Math.Cos(radians)-(y-cy)*Math.Sin(radians),
                cy+(x-cx)*Math.Sin(radians)+(y-cy)*Math.Cos(radians));
            Assert.Equal(QuadrantColors[i],_sampler.SampleSheet(snapshot,location,default));
        }
        Assert.Equal("#FFFFFF",_sampler.SampleSheet(snapshot,new(10,10),default));
    }

    [Fact]
    public void SampleSheet_UsesZOrderAndOpacityWithoutApplyingPrintMonochrome()
    {
        var doc=SheetDocument.CreateBlank(new(new(100,100),false,SheetMargins.SafeDefault));
        var red=Rectangle(new(10,10,80,80),"#FF0000"); var blue=Rectangle(new(30,30,40,40),"#0000FF",0.5);
        doc.AddObjects([red,blue,Rectangle(new(35,35,20,20),"#00FF00",visible:false)]);
        Assert.Equal("#FF0000",_sampler.SampleSheet(doc.Snapshot(),new(20,20),default));
        Assert.Equal("#7F0080",_sampler.SampleSheet(doc.Snapshot(),new(50,50),default));
        doc.SetObjectOrder(red.Id,1);
        Assert.Equal("#FF0000",_sampler.SampleSheet(doc.Snapshot(),new(50,50),default));
    }

    [Theory]
    [InlineData(NotebookVariant.GridSingle)] [InlineData(NotebookVariant.GridSpread)]
    [InlineData(NotebookVariant.RuledSingle)] [InlineData(NotebookVariant.RuledSpread)]
    public void SampleSheet_GridRedMarginAndFoldGuidesNeverBecomeSampledContent(NotebookVariant variant)
    {
        var paper=NotebookProfiles.Default(variant).ToPaper(); var sheet=SheetDocument.CreateBlank(paper).Snapshot();
        var guides=NotebookGeometry.Guides(paper).ToArray(); Assert.NotEmpty(guides);
        foreach(var guide in guides.Where(g=>g.IsMargin || g.IsFold).Concat(guides.Take(3)))
        {
            var point=new PointD((guide.Start.X+guide.End.X)/2,(guide.Start.Y+guide.End.Y)/2);
            if(point.X>=0 && point.Y>=0 && point.X<paper.OrientedSizeMm.Width && point.Y<paper.OrientedSizeMm.Height)
                Assert.Equal("#FFFFFF",_sampler.SampleSheet(sheet,point,default));
        }
    }

    [Theory]
    [InlineData(210.37,297.19,false)] [InlineData(210.37,297.19,true)] [InlineData(780.13,991.27,false)]
    public void SampleSheet_MapsActualRasterDimensionsOnFractionalAndCappedPaper(double width,double height,bool landscape)
    {
        var paper=new SheetPaper(new(width,height),landscape,new(0,0,0,0)); var size=paper.OrientedSizeMm;
        var doc=SheetDocument.CreateBlank(paper); doc.AddObject(Rectangle(new(size.Width*0.6,size.Height*0.6,size.Width*0.39,size.Height*0.39),"#1267AB"));
        Assert.Equal("#1267AB",_sampler.SampleSheet(doc.Snapshot(),new(size.Width*0.93,size.Height*0.91),default));
        Assert.Equal("#FFFFFF",_sampler.SampleSheet(doc.Snapshot(),new(size.Width*0.1,size.Height*0.1),default));
        Assert.Equal("#FFFFFF",_sampler.SampleSheet(doc.Snapshot(),new(size.Width-1e-5,size.Height-1e-5),default));
    }

    [Theory]
    [InlineData(0,0)] [InlineData(128,255)] [InlineData(255,255)]
    public void SampleSource_CompositesTransparentPhotoOverWhitePaper(int alpha,int red)
    {
        var page=Photo(Image(false,Color.FromArgb(alpha,red,0,0)));
        var expected=alpha switch {0=>"#FFFFFF",128=>"#FF7F7F",_=>"#FF0000"};
        Assert.Equal(expected,_sampler.SampleSource(page.Snapshot(),new(200,100),default));
    }

    [Fact]
    public void SampleSheet_CompositesTransparentPhotoOverWhitePaper()
    {
        var page=Photo(Image(false,Color.FromArgb(128,255,0,0))); page.Document.SetPlacement(page.Document.PrimaryContent!.Id,new(new(30,30,100,50)));
        Assert.Equal("#FF7F7F",_sampler.SampleSheet(page.Document.Snapshot(),new(80,55),default));
        Assert.Equal("#FFFFFF",_sampler.SampleSheet(page.Document.Snapshot(),new(10,10),default));
    }

    [Fact]
    public void SampleSource_CapsRenderingForHugeLogicalAndPhysicalDimensions()
    {
        var snapshot=Photo(Image()).Snapshot() with { SourceWidth=40000,SourceHeight=20000,WidthMm=200000,HeightMm=100000,
            Edits=new(new(0,0,40000,20000),[]),Sheet=null };
        Assert.Equal("#FF0000",_sampler.SampleSource(snapshot,new(10000,5000),default));
        Assert.Equal("#FFD700",_sampler.SampleSource(snapshot,new(30000,15000),default));
    }

    [Theory]
    [InlineData(-0.1,10)] [InlineData(10,-0.1)] [InlineData(210,10)] [InlineData(10,297)]
    [InlineData(double.NaN,10)] [InlineData(10,double.NaN)] [InlineData(double.PositiveInfinity,10)] [InlineData(10,double.NegativeInfinity)]
    public void SampleSheet_RejectsOutsideAndNonFiniteLocations(double x,double y)
        =>Assert.Throws<ArgumentOutOfRangeException>(()=>_sampler.SampleSheet(SheetDocument.CreateBlank(SheetPaper.A4).Snapshot(),new(x,y),default));

    [Theory]
    [InlineData(39.999,50)] [InlineData(100,19.999)] [InlineData(360,50)] [InlineData(100,180)]
    [InlineData(400,50)] [InlineData(100,200)] [InlineData(double.NaN,50)] [InlineData(100,double.PositiveInfinity)]
    public void SampleSource_RejectsOutsideSavedCropAndNonFiniteLocations(double x,double y)
    {
        var snapshot=Photo(Image()).Snapshot() with { Edits=new(new(40,20,320,160),[]) };
        Assert.Throws<ArgumentOutOfRangeException>(()=>_sampler.SampleSource(snapshot,new(x,y),default));
    }

    [Fact]
    public void SampleSource_AllowsSavedCropTopLeftAndLastVisibleInterior()
    {
        var snapshot=Photo(Image()).Snapshot() with { Edits=new(new(40,20,320,160),[]) };
        Assert.Equal("#FF0000",_sampler.SampleSource(snapshot,new(40,20),default));
        Assert.Equal("#FFD700",_sampler.SampleSource(snapshot,new(359.999,179.999),default));
    }

    [Theory]
    [InlineData(SourceKind.Pdf)] [InlineData(SourceKind.Docx)] [InlineData(SourceKind.Blank)]
    public void SampleSource_RejectsNonPhotoModes(SourceKind kind)
    {
        var snapshot=Photo(Image()).Snapshot() with { Kind=kind,Sheet=null };
        Assert.Throws<InvalidOperationException>(()=>_sampler.SampleSource(snapshot,new(100,50),default));
    }

    [Fact]
    public void SampleSource_RejectsComposedSheetInsteadOfIgnoringItsOtherObjects()
    {
        var page=Photo(Image()); page.Document.AddObject(Rectangle(new(10,10,30,30),"#FFFFFF"));
        Assert.Throws<InvalidOperationException>(()=>_sampler.SampleSource(page.Snapshot(),new(100,50),default));
    }

    [Theory]
    [InlineData(0,200,100,50)] [InlineData(400,double.NaN,100,50)]
    [InlineData(400,200,-1,50)] [InlineData(400,200,100,double.PositiveInfinity)]
    public void SampleSource_RejectsInvalidLogicalOrPhysicalDimensions(double width,double height,double mmWidth,double mmHeight)
    {
        var snapshot=Photo(Image()).Snapshot() with { SourceWidth=width,SourceHeight=height,WidthMm=mmWidth,HeightMm=mmHeight };
        Assert.Throws<ArgumentException>(()=>_sampler.SampleSource(snapshot,new(100,50),default));
    }

    [Theory]
    [InlineData(-1,0,400,200)] [InlineData(0,0,401,200)] [InlineData(0,0,400,201)]
    [InlineData(0,0,0,200)] [InlineData(0,0,double.NaN,200)]
    public void SampleSource_RejectsInvalidSavedCrop(double x,double y,double width,double height)
    {
        var snapshot=Photo(Image()).Snapshot() with { Edits=new(new(x,y,width,height),[]) };
        Assert.Throws<ArgumentException>(()=>_sampler.SampleSource(snapshot,new(100,50),default));
    }

    [Fact]
    public void SampleSheet_RejectsInvalidPaperBeforeRendering()
    {
        var valid=SheetDocument.CreateBlank(SheetPaper.A4).Snapshot(); var invalid=valid with { Paper=new(new(double.NaN,297),false,SheetMargins.SafeDefault) };
        Assert.Throws<ArgumentException>(()=>_sampler.SampleSheet(invalid,new(10,10),default));
    }

    [Fact]
    public void Sampling_MissingVisibleMaterialFailsAndLeavesSnapshotUntouched()
    {
        var page=Photo(Image()); var before=page.Document.Snapshot(); File.Delete(page.WorkingPath);
        Assert.Throws<FileNotFoundException>(()=>_sampler.SampleSource(page.Snapshot(),new(100,50),default));
        Assert.Throws<FileNotFoundException>(()=>_sampler.SampleSheet(before,new(100,100),default));
        Assert.Same(before,page.Document.Snapshot());
    }

    [Fact]
    public void Sampling_PreCanceledTokenStopsBeforeOpeningMaterial()
    {
        var page=Photo(Image()); var before=page.Document.Snapshot(); File.Delete(page.WorkingPath);
        using var cts=new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>_sampler.SampleSource(page.Snapshot(),new(100,50),cts.Token));
        Assert.Throws<OperationCanceledException>(()=>_sampler.SampleSheet(before,new(100,100),cts.Token));
        Assert.Same(before,page.Document.Snapshot());
    }

    [Fact]
    public void Sampling_NullSnapshotsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(()=>_sampler.SampleSheet(null!,new(10,10),default));
        Assert.Throws<ArgumentNullException>(()=>_sampler.SampleSource(null!,new(10,10),default));
    }

    public void Dispose()
    {
        if(!Directory.Exists(_root)) return;
        var target=Path.GetFullPath(_root); var allowed=Path.GetFullPath(Fixtures.Root)+Path.DirectorySeparatorChar;
        if(!target.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Небезопасный путь временного тестового каталога.");
        Directory.Delete(target,true);
    }
}
