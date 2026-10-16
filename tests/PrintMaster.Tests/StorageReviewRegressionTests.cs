using PrintMaster.Models;
using PrintMaster.Services;
using System.IO;
using System.Text.Json;
using Xunit;

namespace PrintMaster.Tests;

public sealed class StorageReviewRegressionTests
{
    [Fact]
    public async Task RemovedLibreOfficeInstallationDoesNotPreventSavingUnrelatedPreferences()
    {
        var path=Path.Combine(Fixtures.Root,"preferences-removed-lo-"+Guid.NewGuid().ToString("N")+".json");
        var missing=Path.Combine(Fixtures.Root,"removed-libreoffice-"+Guid.NewGuid().ToString("N"),"soffice.com");
        var expected=new ApplicationPreferences {LibreOfficePath=missing,Dpi=300,Threshold=117,AutosaveEnabled=false};
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(expected));
        var store=new ApplicationPreferencesStore(path); var loaded=await store.LoadAsync(default);
        Assert.Null(loaded.LibreOfficePath); Assert.Equal(expected with {LibreOfficePath=null},loaded);
        await store.SaveAsync(loaded with {Threshold=181},default);
        Assert.Equal(181,(await store.LoadAsync(default)).Threshold);
        Assert.Equal(300,(await store.LoadAsync(default)).Dpi);
    }

    [Theory] [InlineData(".png")] [InlineData(".pdf")]
    public async Task FailedImportDiscardsOnlyItsOwnTemporaryDirectory(string extension)
    {
        using var workspace=new SessionWorkspace(); var good=workspace.NewImportDirectory();
        var marker=Path.Combine(good,"keep.txt"); await File.WriteAllTextAsync(marker,"successful import");
        var source=Path.Combine(Fixtures.Root,"invalid-import-"+Guid.NewGuid().ToString("N")+extension);
        var bytes="invalid source must be retained"u8.ToArray(); await File.WriteAllBytesAsync(source,bytes);
        await Assert.ThrowsAnyAsync<Exception>(()=>new FileImportService(workspace,new(),new()).ImportAsync(source,default));
        Assert.Equal(new[] {good},Directory.GetDirectories(workspace.Root));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(source)); Assert.Equal("successful import",await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task CancelledImportLeavesNoPartialMaterialAndDoesNotDeleteSource()
    {
        using var workspace=new SessionWorkspace(); var source=Fixtures.CreateImage("cancelled-import-"+Guid.NewGuid().ToString("N")+".png");
        var bytes=await File.ReadAllBytesAsync(source); using var cancel=new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new FileImportService(workspace,new(),new()).ImportAsync(source,cancel.Token));
        Assert.Empty(Directory.GetDirectories(workspace.Root)); Assert.Equal(bytes,await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task ClipboardMaterialWithWrongAspectRatioDoesNotRetainItsCopiedBytes()
    {
        using var workspace=new SessionWorkspace(); var source=Fixtures.CreateImage("clipboard-import-review-"+Guid.NewGuid().ToString("N")+".png");
        var material=new ClipboardMaterial(new(Guid.NewGuid(),SourceKind.Image,null,null,0,1,new(1000,1000),new(100,100)),".png",await File.ReadAllBytesAsync(source));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new FileImportService(workspace,new(),new()).ImportClipboardMaterialAsync(material,default));
        Assert.Empty(Directory.GetDirectories(workspace.Root)); Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task SuccessfulImportsKeepAllOwnedMaterialUntilWorkspaceCloses()
    {
        using var workspace=new SessionWorkspace(); var source=Fixtures.CreateImage("successful-import-review-"+Guid.NewGuid().ToString("N")+".png");
        var page=Assert.Single(await new FileImportService(workspace,new(),new()).ImportAsync(source,default));
        Assert.True(File.Exists(page.WorkingPath)); Assert.Single(Directory.GetDirectories(workspace.Root));
        Assert.Equal(await File.ReadAllBytesAsync(source),await File.ReadAllBytesAsync(page.WorkingPath));
    }

    [Fact]
    public async Task CancelledClipboardImageImportDoesNotLeaveAnEmptyTransaction()
    {
        using var workspace=new SessionWorkspace(); using var cancel=new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new FileImportService(workspace,new(),new()).ImportClipboardImageAsync(ClipboardTests.Image(),cancel.Token));
        Assert.Empty(Directory.GetDirectories(workspace.Root));
    }

    [Fact]
    public void FailedImportCleanupRejectsWorkspaceRootAndUnownedDirectories()
    {
        using var workspace=new SessionWorkspace(); var owned=workspace.NewImportDirectory();
        var outside=Path.Combine(Fixtures.Root,"unowned-import-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outside);
        var marker=Path.Combine(outside,"keep.txt"); File.WriteAllText(marker,"user material");
        Assert.Throws<ArgumentException>(()=>workspace.DiscardFailedImport(workspace.Root));
        Assert.Throws<ArgumentException>(()=>workspace.DiscardFailedImport(outside));
        Assert.Throws<ArgumentException>(()=>workspace.DiscardFailedImport(Path.Combine(workspace.Root,"not-a-generated-directory")));
        Assert.True(Directory.Exists(owned)); Assert.Equal("user material",File.ReadAllText(marker));
    }

    [Fact]
    public async Task ExifPortraitImportNormalizesOwnedPixelsAndPreservesTheOriginal()
    {
        using var workspace=new SessionWorkspace(); var source=Path.Combine(Fixtures.Root,"exif-import-review-"+Guid.NewGuid().ToString("N")+".jpg");
        ImageImportOrientationTests.CreateJpeg(source,6); var bytes=await File.ReadAllBytesAsync(source);
        var page=Assert.Single(await new FileImportService(workspace,new(),new()).ImportAsync(source,default));
        Assert.Equal(source,page.OriginalPath); Assert.Equal(".png",Path.GetExtension(page.WorkingPath));
        Assert.Equal(1000,page.SourceWidth); Assert.Equal(1500,page.SourceHeight);
        using var normalized=new System.Drawing.Bitmap(page.WorkingPath);
        Assert.Equal(80,normalized.Width); Assert.Equal(120,normalized.Height);
        Assert.InRange(normalized.HorizontalResolution,149.5,150.5); Assert.InRange(normalized.VerticalResolution,299.5,300.5);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(source)); Assert.Single(Directory.GetDirectories(workspace.Root));
    }
}
