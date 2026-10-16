using PrintMaster.Models;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using System.IO;
using Xunit;

namespace PrintMaster.Tests;

public sealed class TextColorTests
{
    [Theory]
    [InlineData("2a73c5","#2A73C5")] [InlineData(" #0080ff ","#0080FF")]
    [InlineData("000000","#000000")] [InlineData("#ffffff","#FFFFFF")]
    public void HexAcceptsEveryRgbWithOptionalHash(string input,string expected)
    { Assert.True(TextColorService.TryNormalize(input,out var result)); Assert.Equal(expected,result); }
    [Theory]
    [InlineData("")] [InlineData("#FFF")] [InlineData("1234567")] [InlineData("GG0033")] [InlineData("#12345Z")] [InlineData("#80123456")]
    public void InvalidAndAlphaInputsCannotBecomeADifferentColor(string input)
    { Assert.False(TextColorService.TryNormalize(input,out var result)); Assert.Empty(result); }
    [Theory]
    [InlineData("#FF0000",0x0000FFu)] [InlineData("#0000FF",0xFF0000u)] [InlineData("#2A73C5",0xC5732Au)]
    public void NativeColorRefKeepsRgbChannels(string color,uint native)
    { Assert.Equal(native,TextColorService.ToColorRef(color)); Assert.Equal(color,TextColorService.FromColorRef(native)); }
    [Fact]
    public async Task FullRgbTextColorHasUndoAndSurvivesPortableProject()
    {
        using var vm=new PrintSessionViewModel(new ClipboardTests.TestDialogs(),Path.Combine(Fixtures.Root,"color-recovery-"+Guid.NewGuid().ToString("N")),new FakeClipboard());
        var page=PrintPageItem.CreateBlank(SheetPaper.A4);
        var text=SheetEditing.CreateText("Полная палитра — проверка",new(Color:"#111827"),new(20,25),page.Document.Paper.SafeAreaMm);
        page.Document.AddObject(text); vm.AddPage(page); vm.SelectedSheetObjectId=text.Id; var count=vm.UndoCount;
        vm.TextColor="#2A73C5";
        Assert.Equal("#2A73C5",page.Document.Objects.OfType<TextObject>().Single().Style.Color); Assert.Equal(count+1,vm.UndoCount);
        vm.UndoCommand.Execute(null); Assert.Equal("#111827",page.Document.Objects.OfType<TextObject>().Single().Style.Color);
        vm.RedoCommand.Execute(null); Assert.Equal("#2A73C5",page.Document.Objects.OfType<TextObject>().Single().Style.Color);
        var path=Path.Combine(Fixtures.Root,"color-"+Guid.NewGuid().ToString("N")+".printmaster");
        await new ProjectFileService().SaveAsync(vm.CaptureProject(),path,true,default);
        using var workspace=new SessionWorkspace(); var restored=await new ProjectFileService().LoadAsync(path,workspace,default);
        Assert.Equal("#2A73C5",restored.Session.Pages[0].Sheet.Objects.OfType<TextObject>().Single().Style.Color);
    }
}
