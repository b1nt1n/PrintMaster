using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static BitmapSource RasterForCanvas(PrintPageItem page)
    { using var image=new PageRasterizer(new()).RenderSheet(page.Document.Snapshot(),96,false,default,1600); return BitmapBridge.ToWpf(image); }
    private static RenderTargetBitmap CanvasPixels(SheetCanvas canvas)
    {
        canvas.UpdateLayout(); var bitmap=new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth),(int)Math.Ceiling(canvas.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(canvas); return bitmap;
    }
    private static int GrayAt(BitmapSource bitmap,PointD mm)
    {
        var pixel=new byte[4]; bitmap.CopyPixels(new Int32Rect((int)(mm.X*SheetEditing.DipPerMm),(int)(mm.Y*SheetEditing.DipPerMm),1,1),pixel,4,0); return (pixel[0]+pixel[1]+pixel[2])/3;
    }
    private static async Task VerifyDragDoesNotRevertWhilePreviewIsPending(MainWindow owner)
    {
        foreach(var kind in new[] { "rectangle","ellipse","photo","ink","group" })
        {
            var page=PrintPageItem.CreateBlank(SheetPaper.A4);
            SheetObject shape=new ShapeObject(Guid.NewGuid(),new(new(20,30,30,20)),kind=="ellipse" ? SheetShapeKind.Ellipse : SheetShapeKind.Rectangle,new("#000000",0.4,1,"#000000"));
            if(kind=="photo")
            {
                var path=System.IO.Path.Combine(Fixtures.Root,"fix016-black-photo.png");
                using(var bitmap=new System.Drawing.Bitmap(60,40)) { using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(System.Drawing.Color.Black); bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png); }
                var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(60,40),new(30,20));
                shape=new ContentObject(shape.Id,shape.Placement,asset.Id,ContentEdits.Empty(asset.LogicalSize)); page.Document.AddAsset(asset);
            }
            if(kind=="ink") shape=new InkObject(shape.Id,shape.Placement,[new(0,0),new(1,1)],new("#000000",2));
            page.Document.AddObject(shape);
            var selected=new List<Guid> {shape.Id};
            if(kind=="group") { var second=new ShapeObject(Guid.NewGuid(),new(new(65,30,20,20)),SheetShapeKind.Rectangle,new("#000000",0.4,1,"#000000")); page.Document.AddObject(second); selected.Add(second.Id); }
            var initialRevision=page.Document.Revision; var initial=RasterForCanvas(page);
            var canvas=new SheetCanvas { SnappingEnabled=false,Width=210*SheetEditing.DipPerMm,Height=297*SheetEditing.DipPerMm,Page=page,Preview=initial,PreviewFrame=new(page.Id,initialRevision,initial),Tool=EditorTool.Select,SafeArea=new(19,19,750,1080),SelectedObjectId=shape.Id,SelectedObjectIds=selected.ToArray() };
            var host=new Window { Title="FIX-016 — "+kind,Owner=owner,Content=canvas,Width=820,Height=650 }; host.Show(); host.UpdateLayout();
            try
            {
                canvas.BeginGesture(new(35,40)); await WaitUntil(()=>canvas.MovePreviewReady); canvas.UpdateGesture(new(105,100));
                Assert.True(GrayAt(CanvasPixels(canvas),new(105,100))<30);
                canvas.CommitGesture();
                Assert.Equal(new RectD(90,90,30,20),page.Document.Objects[0].Placement.BoundsMm);
                var immediate=CanvasPixels(canvas);
                Assert.True(GrayAt(immediate,new(105,100))<30,kind+": object jumped after mouse release.");
                Assert.True(GrayAt(immediate,new(35,40))>240,kind+": old position reappeared.");
                Assert.True(canvas.HasCommittedPresentation);
                canvas.PreviewFrame=new(page.Id,initialRevision,initial); // Late old render must not release the handoff frame.
                Assert.True(canvas.HasCommittedPresentation); Assert.True(GrayAt(CanvasPixels(canvas),new(105,100))<30);
                canvas.PreviewFrame=new(Guid.NewGuid(),page.Document.Revision,initial);
                Assert.True(canvas.HasCommittedPresentation); Assert.True(GrayAt(CanvasPixels(canvas),new(105,100))<30);
                // Start a second move before the first final render arrived.
                canvas.BeginGesture(new(105,100)); await WaitUntil(()=>canvas.MovePreviewReady); canvas.UpdateGesture(new(125,120)); canvas.CommitGesture();
                Assert.True(GrayAt(CanvasPixels(canvas),new(125,120))<30); Assert.True(GrayAt(CanvasPixels(canvas),new(105,100))>240);
                var final=RasterForCanvas(page); canvas.PreviewFrame=new(page.Id,page.Document.Revision,final);
                Assert.False(canvas.HasCommittedPresentation); Assert.True(GrayAt(CanvasPixels(canvas),new(125,120))<30);
                page.Document.Undo(); canvas.PreviewFrame=new(page.Id,page.Document.Revision,RasterForCanvas(page));
                Assert.True(GrayAt(CanvasPixels(canvas),new(105,100))<30);
                canvas.BeginGesture(new(105,100)); await WaitUntil(()=>canvas.MovePreviewReady); canvas.UpdateGesture(new(155,140)); canvas.CancelGesture();
                Assert.False(canvas.HasCommittedPresentation); Assert.True(GrayAt(CanvasPixels(canvas),new(105,100))<30);
                canvas.Page=PrintPageItem.CreateBlank(SheetPaper.A4); Assert.False(canvas.HasCommittedPresentation);
            }
            finally { host.Close(); }
        }
    }
}
