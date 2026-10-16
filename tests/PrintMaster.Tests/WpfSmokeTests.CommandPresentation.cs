using PrintMaster.Controls;
using PrintMaster.Models;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static void VerifyReviewCommandPresentation(MainWindow window,PrintSessionViewModel vm)
    {
        var sharedStyle=Assert.IsType<Style>(window.FindResource(typeof(Window)));
        Assert.Same(sharedStyle,window.Style);Assert.NotNull(window.Icon);Assert.Equal(13,window.FontSize);
        var creation=new NewSheetDialog(vm.Settings){Owner=window};
        Assert.Same(sharedStyle,creation.Style);Assert.NotNull(creation.Icon);Assert.Equal(13,creation.FontSize);creation.Close();
        var oldWidth=window.Width;var oldHeight=window.Height;
        foreach(var dimensions in new[]{(1460d,940d),(900d,720d),(720d,500d)})
        {
            window.Width=dimensions.Item1;window.Height=dimensions.Item2;window.UpdateLayout();
            var client=(FrameworkElement)window.Content;
            foreach(var name in new[]{"QueueToggle","AddFilesButton","CreateSheetButton","PasteButton","InspectorToggle"})
            {
                var button=Assert.IsType<Button>(window.FindName(name));var label=Assert.IsType<IconLabel>(button.Content);
                Assert.False(string.IsNullOrWhiteSpace(label.Text));Assert.False(string.IsNullOrWhiteSpace(label.Icon));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                var bounds=button.TransformToAncestor(client).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.X>=-0.5 && bounds.Y>=-0.5 && bounds.Right<=client.ActualWidth+.5 && bounds.Bottom<=client.ActualHeight+.5,$"{name}: {bounds}, client {client.RenderSize}");
                Assert.True(button.ActualWidth>=label.DesiredSize.Width+button.Padding.Left+button.Padding.Right-1,$"Clipped command: {name}");
            }
        }
        var paper=PrintPageItem.CreateBlank(SheetPaper.A4);vm.AddPage(paper);vm.SetQueueSelection([paper],paper);window.UpdateLayout();
        var menu=window.PrepareQueueContextMenu();
        foreach(var item in menu.Items.OfType<MenuItem>())Assert.IsType<LucideIcon>(item.Icon);
        var scopeButton=(Button)window.FindName("QueueToggle");Assert.True(scopeButton.Focus());
        Assert.Equal(ShortcutScope.None,window.CurrentShortcutScope);var previousTool=vm.Tool;
        Assert.False(window.HandleShortcut(Key.A,ModifierKeys.Control));Assert.Equal(previousTool,vm.Tool);
        vm.RemovePageCommand.Execute(paper);
        window.Width=oldWidth;window.Height=oldHeight;window.UpdateLayout();
    }
}
