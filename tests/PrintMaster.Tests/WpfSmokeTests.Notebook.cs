using PrintMaster.Models;
using PrintMaster.Controls;
using PrintMaster.Services;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static async Task VerifyNotebookPickerBindings(MainWindow owner)
    {
            var dialog=new NewSheetDialog(new() { PrinterName="HP LaserJet Professional P1102" }) { Owner=owner };
            dialog.Show(); dialog.UpdateLayout();
            try
            {
                dialog.ViewModel.Paper=PaperPreset.Notebook; await Task.Delay(80);
                foreach(var variant in new[] { NotebookVariant.GridSpread,NotebookVariant.RuledSingle,NotebookVariant.RuledSpread,NotebookVariant.GridSingle })
                {
                    dialog.ViewModel.NotebookVariant=variant; await Task.Delay(80); dialog.UpdateLayout();
                    Assert.NotNull(dialog.ViewModel.SelectedNotebookProfile);
                    Assert.Equal(NotebookProfiles.Default(variant).NativeSize,dialog.ViewModel.Result!.OrientedSizeMm);
                }
            }
            finally { dialog.Close(); }

        var profile=new NotebookProfileDialog(NotebookProfiles.Default(NotebookVariant.GridSpread),true) { Owner=owner };
        profile.Show(); profile.UpdateLayout();
        try
        {
            var number=Descendants(profile).OfType<NumericField>().Single(t=>AutomationProperties.GetName(t)=="Левое красное поле"); var input=number.Input;
            var apply=Descendants(profile).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Применить");
            input.Text="19,5"; Assert.True(number.Commit()); await Task.Delay(80);
            Assert.Equal(19.5,profile.Result!.LeftMarginMm); Assert.True(apply.IsEnabled);
            Assert.NotEqual(NotebookProfiles.Default(NotebookVariant.GridSpread).Id,profile.Result.Id);
            input.Text="NaN"; Assert.False(number.Commit()); apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(80); Assert.True(number.HasError); Assert.True(profile.IsVisible); Assert.Equal(19.5,profile.Result!.LeftMarginMm);
            input.Text="20.5"; Assert.True(number.Commit()); await Task.Delay(80); Assert.True(apply.IsEnabled); Assert.Equal(20.5,profile.Result!.LeftMarginMm);
            profile.Width=420; profile.Height=440; profile.UpdateLayout();
            Assert.Contains(Descendants(profile).OfType<ScrollViewer>(),s=>s.ScrollableHeight>0);
            var button=apply.TransformToAncestor(profile).TransformBounds(new Rect(apply.RenderSize));
            Assert.InRange(button.Bottom,0,profile.ActualHeight); Capture(profile,"nb001-owned-profile-small.png");
        }
        finally { profile.Close(); }

        var settings=new PrintSettings { PrinterName="HP LaserJet Professional P1102",Paper=PaperPreset.Notebook,Notebook=NotebookProfiles.Default(NotebookVariant.GridSpread),Landscape=true }.Snapshot();
        var guideModel=new PaperGuideViewModel(settings,2,PaperGuideStep.Reload);
        var guide=new PaperGuideDialog(guideModel) { Owner=owner,Width=480,Height=440 };
        guide.Show(); guide.UpdateLayout();
        try
        {
            var confirm=Descendants(guide).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Печатать оборот");
            var hidden=Descendants(guide).OfType<CheckBox>().Single(); Assert.False(hidden.IsVisible);
            Descendants(guide).OfType<Expander>().Single().IsExpanded=true; guide.UpdateLayout();
            var number=Descendants(guide).OfType<NumericField>().Single(t=>AutomationProperties.GetName(t)=="Поправка печати по X"); var input=number.Input;
            input.Text="NaN"; Assert.False(number.Commit()); confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(80); Assert.True(number.HasError); Assert.True(guide.IsVisible);
            input.Text="0,5"; Assert.True(number.Commit()); await Task.Delay(80); Assert.True(confirm.IsEnabled); Assert.Equal(0.5,guideModel.Calibration!.OffsetXmm);
            var button=confirm.TransformToAncestor(guide).TransformBounds(new Rect(confirm.RenderSize));
            Assert.InRange(button.Bottom,0,guide.ActualHeight); Capture(guide,"nb001-owned-reload-small.png");
        }
        finally { guide.Close(); }

        using var vm=new PrintSessionViewModel(new TestDialogs(),System.IO.Path.Combine(Fixtures.Root,"nb-rotated-editor-"+Guid.NewGuid().ToString("N")));
        var page=PrintPageItem.CreateBlank(NotebookProfiles.Default(NotebookVariant.RuledSpread).ToPaper(landscape:false));
        vm.AddPage(page); vm.SetNotebookStory(page,"Поворот связанного текста",new());
        var editingWindow=new MainWindow(vm) { Owner=owner }; editingWindow.Show();
        try
        {
            await WaitUntil(()=>vm.EditorPreview!=null);
            vm.BeginTextEditing(new(20,10),page.Document.Objects.OfType<TextObject>().First());
            await Task.Delay(80); editingWindow.UpdateLayout();
            var inline=(InlineTextEditor)editingWindow.FindName("TextEditor");
            Assert.True(inline.IsVisible); Assert.Equal(90,Assert.IsType<RotateTransform>(inline.RenderTransform).Angle);
            vm.FinishTextEditing(false);
        }
        finally { await vm.PrepareCloseAsync(); editingWindow.Close(); }
    }
}
