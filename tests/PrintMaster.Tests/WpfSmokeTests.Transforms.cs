using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.Views;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyObjectTransformGestures(MainWindow owner)
    {
        foreach(var kind in new[] {"rectangle","ellipse","photo","ink"})
        {
            var page=PrintPageItem.CreateBlank(SheetPaper.A4);
            SheetObject item=new ShapeObject(Guid.NewGuid(),new(new(20,30,40,20)),kind=="ellipse" ? SheetShapeKind.Ellipse : SheetShapeKind.Rectangle,new("#000000",0.4,1,"#000000"));
            if(kind=="photo")
            {
                var path=Path.Combine(Fixtures.Root,"ux009-black-photo.png");
                using(var bitmap=new System.Drawing.Bitmap(60,30)) { using(var g=System.Drawing.Graphics.FromImage(bitmap)) g.Clear(System.Drawing.Color.Black); bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png); }
                var asset=new SourceAsset(Guid.NewGuid(),SourceKind.Image,path,path,0,1,new(60,30),new(40,20)); page.Document.AddAsset(asset);
                item=new ContentObject(item.Id,item.Placement,asset.Id,ContentEdits.Empty(asset.LogicalSize));
            }
            if(kind=="ink") item=new InkObject(item.Id,item.Placement,[new(0,0),new(1,1)],new("#000000",2));
            page.Document.AddObject(item); var raster=RasterForCanvas(page); var revision=page.Document.Revision;
            var canvas=new SheetCanvas { Width=210*SheetEditing.DipPerMm,Height=297*SheetEditing.DipPerMm,Page=page,Preview=raster,PreviewFrame=new(page.Id,revision,raster),Tool=EditorTool.Select,
                SafeArea=new(19,19,750,1080),SelectedObjectId=item.Id,SelectedObjectIds=[item.Id],ViewScale=1 };
            var host=new Window { Owner=owner,Title="UX-009 — "+kind,Content=canvas,Width=820,Height=650 }; host.Show(); host.UpdateLayout();
            try
            {
                var start=canvas.HandleLocation(item,ObjectHandle.SouthEast);
                canvas.BeginGesture(start); Assert.Equal(ObjectHandle.SouthEast,canvas.ActiveHandle); await WaitUntil(()=>canvas.MovePreviewReady);
                canvas.UpdateGesture(new(start.X+20,start.Y+10)); Assert.Equal(revision,page.Document.Revision); Assert.Equal(item,page.Document.Objects.Single());
                canvas.CommitGesture(); var resized=page.Document.Objects.Single(); Assert.Equal(new RectD(20,30,60,30),resized.Placement.BoundsMm);
                Assert.True(canvas.HasCommittedPresentation); var held=CanvasPixels(canvas);
                var probe=kind=="ink" ? new PointD(70,55) : new(65,50);
                Assert.True(GrayAt(held,probe)<80,kind+": transform was lost after mouse release.");
                canvas.PreviewFrame=new(page.Id,revision,raster); Assert.True(canvas.HasCommittedPresentation);
                var knob=canvas.HandleLocation(resized,ObjectHandle.Rotate); var center=ObjectTransform.Center(resized);
                var rotated=ObjectTransform.RotateVector(new(knob.X-center.X,knob.Y-center.Y),90);
                canvas.BeginGesture(knob); Assert.Equal(ObjectHandle.Rotate,canvas.ActiveHandle); await WaitUntil(()=>canvas.MovePreviewReady);
                canvas.UpdateGesture(new(center.X+rotated.X,center.Y+rotated.Y),ModifierKeys.Shift); canvas.CommitGesture();
                Assert.Equal(90,page.Document.Objects.Single().Placement.RotationDegrees,6); Assert.True(canvas.HasCommittedPresentation);
                var rendered=RasterForCanvas(page); canvas.PreviewFrame=new(page.Id,page.Document.Revision,rendered); Assert.False(canvas.HasCommittedPresentation);
                if(kind is "rectangle" or "photo") Assert.True(GrayAt(CanvasPixels(canvas),new(50,70))<30);
                var state=page.Document.Snapshot(); var current=page.Document.Objects.Single();
                canvas.BeginGesture(canvas.HandleLocation(current,ObjectHandle.SouthEast)); canvas.UpdateGesture(new(130,140)); canvas.CancelGesture();
                Assert.Equal(state,page.Document.Snapshot()); Assert.False(canvas.IsGestureActive);
                // Constant screen hit area, independent of the parent's zoom.
                foreach(var scale in new[] {0.25,0.5,1d,2d,4d})
                {
                    canvas.ViewScale=scale; var point=canvas.HandleLocation(current,ObjectHandle.East);
                    Assert.NotEqual(ObjectHandle.None,canvas.HitHandle(current,new(point.X+3/scale/SheetEditing.DipPerMm,point.Y)));
                }
            }
            finally { host.Close(); }
        }
        // Commit before any background preview was ready, and cancel a text-width draft.
        var shortPage=PrintPageItem.CreateBlank(SheetPaper.A4); var shape=new ShapeObject(Guid.NewGuid(),new(new(20,30,40,20)),SheetShapeKind.Rectangle,new("#000000",0.4,1,"#000000")); shortPage.Document.AddObject(shape);
        var preview=RasterForCanvas(shortPage); var shortCanvas=new SheetCanvas { Width=210*SheetEditing.DipPerMm,Height=297*SheetEditing.DipPerMm,Page=shortPage,Preview=preview,PreviewFrame=new(shortPage.Id,shortPage.Document.Revision,preview),SelectedObjectId=shape.Id,SelectedObjectIds=[shape.Id] };
        var shortHost=new Window { Owner=owner,Content=shortCanvas,Width=820,Height=650 }; shortHost.Show(); shortHost.UpdateLayout();
        try
        {
            shortCanvas.BeginGesture(new(60,50)); shortCanvas.UpdateGesture(new(80,60)); shortCanvas.CommitGesture();
            Assert.Equal(new RectD(20,30,60,30),shortPage.Document.Objects.Single().Placement.BoundsMm); Assert.True(GrayAt(CanvasPixels(shortCanvas),new(65,50))<30);
            var text=SheetEditing.CreateText(string.Join(" ",Enumerable.Repeat("Полный текст для переноса строк",8)),new(),new(25,95),SheetPaper.A4.SafeAreaMm); shortPage.Document.AddObject(text);
            shortCanvas.SelectedObjectId=text.Id; shortCanvas.SelectedObjectIds=[text.Id]; var before=shortPage.Document.Snapshot();
            var handle=shortCanvas.HandleLocation(text,ObjectHandle.TextWidth); shortCanvas.BeginGesture(handle); shortCanvas.UpdateGesture(new(handle.X-70,handle.Y));
            Assert.True(shortCanvas.GestureDraft is TextObject); shortCanvas.CancelGesture(); Assert.Equal(before,shortPage.Document.Snapshot());
            shortCanvas.BeginGesture(handle); shortCanvas.UpdateGesture(new(handle.X-70,handle.Y)); shortCanvas.CommitGesture();
            var updated=shortPage.Document.Objects.OfType<TextObject>().Single(); Assert.Equal(text.Text,updated.Text); Assert.True(updated.Placement.BoundsMm.Height>text.Placement.BoundsMm.Height);
            Assert.False(TextLayoutService.Overflows(updated));
            var edge=shape with { Placement=new(new(5,5,40,20)) }; shortCanvas.ViewScale=0.25;
            var rotation=shortCanvas.HandleLocation(edge,ObjectHandle.Rotate); Assert.True(rotation.X>=0 && rotation.Y>=0);
            var fullSheet=shape with { Placement=new(new(5,8,200,280)) }; shortCanvas.ViewScale=0.44;
            var fullKnob=shortCanvas.HandleLocation(fullSheet,ObjectHandle.Rotate); var nearest=new[] {ObjectHandle.North,ObjectHandle.East,ObjectHandle.South,ObjectHandle.West}.Select(h=>ObjectTransform.HandlePoint(fullSheet,h)).Min(p=>Math.Sqrt(Math.Pow(p.X-fullKnob.X,2)+Math.Pow(p.Y-fullKnob.Y,2)));
            Assert.True(nearest*SheetEditing.DipPerMm*shortCanvas.ViewScale>=15,"Rotation knob overlaps a size handle for a full-sheet photo.");
        }
        finally { shortHost.Close(); }
        var vm=owner.ViewModel; var layered=PrintPageItem.CreateBlank(SheetPaper.A4);
        var behind=new ShapeObject(Guid.NewGuid(),new(new(20,30,40,20)),SheetShapeKind.Rectangle,new()); var front=behind with { Id=Guid.NewGuid() };
        layered.Document.AddObjects([behind,front]); vm.AddPage(layered); vm.SelectedPage=layered; vm.Tool=EditorTool.Select;
        var editor=(SheetCanvas)owner.FindName("SheetEditor"); owner.UpdateLayout();
        var menu=owner.PrepareCanvasContextMenu(editor,new(35*SheetEditing.DipPerMm,40*SheetEditing.DipPerMm));
        var pick=menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m=>m.Name=="SelectUnder");
        Assert.Equal(2,pick.Items.Count);
        var lower=(System.Windows.Controls.MenuItem)pick.Items[1]; Assert.True(lower.Command.CanExecute(null)); lower.Command.Execute(null);
        Assert.Equal(behind.Id,vm.SelectedSheetObjectId);
    }
}
