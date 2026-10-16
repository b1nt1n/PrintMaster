using PrintMaster.Services;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class ViewportTests
{
    [Fact]
    public void WheelZoomSupportsHighResolutionDeltasAndClampsItsLimits()
    {
        Assert.Equal(115,ViewportZoom.FromWheel(100,120),6);
        Assert.Equal(100,ViewportZoom.FromWheel(ViewportZoom.FromWheel(100,120),-120),6);
        Assert.InRange(ViewportZoom.FromWheel(100,1),100,101);
        Assert.Equal(800,ViewportZoom.FromWheel(800,120)); Assert.Equal(5,ViewportZoom.FromWheel(5,-120));
        Assert.Equal(100,ViewportZoom.Clamp(double.NaN)); Assert.Equal(100,ViewportZoom.Clamp(double.PositiveInfinity));
    }
    [Fact]
    public void IconPackageContainsAllWindowsSizesAndTheSourceArtworkIsUnchanged()
    {
        var project=Directory.GetParent(Directory.GetParent(Fixtures.Root)!.FullName)!.FullName;
        var source=Path.Combine(project,"ico.png"); var logo=Path.Combine(project,"src","PrintMaster","Assets","Brand","app-logo.png");
        Assert.Equal(File.ReadAllBytes(source),File.ReadAllBytes(logo));
        using var stream=File.OpenRead(Path.Combine(project,"src","PrintMaster","Assets","Brand","app.ico")); using var reader=new BinaryReader(stream);
        Assert.Equal(0,reader.ReadUInt16()); Assert.Equal(1,reader.ReadUInt16()); Assert.Equal(7,reader.ReadUInt16());
        var dimensions=new List<int>();
        for(var i=0;i<7;i++)
        {
            var dimension=reader.ReadByte(); dimensions.Add(dimension==0 ? 256 : dimension); reader.ReadByte(); reader.ReadByte(); reader.ReadByte();
            Assert.Equal(1,reader.ReadUInt16()); Assert.Equal(32,reader.ReadUInt16()); var bytes=reader.ReadUInt32(); var offset=reader.ReadUInt32();
            Assert.True(bytes>100); Assert.True(offset+bytes<=stream.Length);
        }
        Assert.Equal(new[] {16,24,32,48,64,128,256},dimensions);
    }
}
