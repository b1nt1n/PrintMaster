using PrintMaster.Models;
using PrintMaster.Controls;
using PrintMaster.ViewModels;
using PrintMaster.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;

namespace PrintMaster.Tests;

public sealed partial class WpfSmokeTests
{
    private static void VerifyQueueContextTargetAfterFlowCommit(MainWindow window,PrintSessionViewModel vm)
    {
        foreach(var targetKind in new[]{"flow","member","outside","empty"})
        {
            var first=PrintPageItem.CreateBlank(SheetPaper.A4);var second=PrintPageItem.CreateBlank(SheetPaper.A4);var outside=PrintPageItem.CreateBlank(SheetPaper.A4);
            vm.AddPage(first);vm.AddPage(second);vm.AddPage(outside);vm.SelectedPage=first;vm.ShowPrintPreview=false;
            vm.BeginFlowTextCommand.Execute(null);Assert.True(vm.IsEditingText);vm.EditingText="Текст сохраняется перед открытием меню очереди.";
            vm.SetQueueSelection([first,second],first);
            var target=targetKind switch{"flow"=>first,"member"=>second,"outside"=>outside,_=>null};
            window.SelectQueueContextTarget(target);Assert.False(vm.IsEditingText);
            var live=vm.Pages.Single(p=>p.Id==first.Id);Assert.NotSame(first,live);
            Assert.Equal("Текст сохраняется перед открытием меню очереди.",Assert.Single(live.Document.Objects.OfType<TextObject>()).Text);
            var expected=targetKind switch{"flow" or "member"=>new[]{first.Id,second.Id},"outside"=>new[]{outside.Id},_=>Array.Empty<Guid>()};
            Assert.Equal(expected,vm.HighlightedPages.Select(p=>p.Id));
            if(target!=null)Assert.Same(vm.Pages.Single(p=>p.Id==target.Id),vm.SelectedPage);
            Assert.All(vm.HighlightedPages,p=>Assert.Contains(p,vm.Pages));Assert.Equal(vm.Pages.Count,vm.Pages.Select(p=>p.Id).Distinct().Count());
            foreach(var page in vm.Pages.Where(p=>p.Id==first.Id || p.Id==second.Id || p.Id==outside.Id).ToArray())vm.RemovePageCommand.Execute(page);
        }
    }

    private static async Task VerifyQueueRightClickAfterFlowCommitAsync(MainWindow window,PrintSessionViewModel vm)
    {
        var first=PrintPageItem.CreateBlank(SheetPaper.A4);var second=PrintPageItem.CreateBlank(SheetPaper.A4);
        vm.AddPage(first);vm.AddPage(second);vm.SelectedPage=first;vm.ShowPrintPreview=false;
        vm.BeginFlowTextCommand.Execute(null);vm.EditingText="ПКМ сохраняет группу после завершения ввода.";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
        var input=((InlineTextEditor)window.FindName("TextEditor")).TextInputBox;Assert.True(input.Focus());
        vm.SetQueueSelection([first,second],first);var queue=(ListBox)window.FindName("QueueList");queue.ScrollIntoView(first);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();Assert.True(vm.IsEditingText);
        var container=Assert.IsType<ListBoxItem>(queue.ItemContainerGenerator.ContainerFromItem(first));Assert.Same(first,container.DataContext);
        // PreviewMouseDown tunnels through the real container and re-raises WPF's
        // right-button event, as native input does. The button-specific event itself
        // is direct and would not reach the QueueList from a synthetic child raise.
        var click=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Right){RoutedEvent=Mouse.PreviewMouseDownEvent};container.RaiseEvent(click);
        Assert.True(click.Handled);Assert.False(vm.IsEditingText);
        var live=vm.Pages.Single(p=>p.Id==first.Id);Assert.NotSame(first,live);Assert.Same(live,vm.SelectedPage);
        Assert.Equal(new[]{first.Id,second.Id},vm.HighlightedPages.Select(p=>p.Id));Assert.All(vm.HighlightedPages,p=>Assert.Contains(p,vm.Pages));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
        Assert.Equal(new[]{first.Id,second.Id},vm.HighlightedPages.Select(p=>p.Id));
        Assert.Equal("ПКМ сохраняет группу после завершения ввода.",Assert.Single(live.Document.Objects.OfType<TextObject>()).Text);
        foreach(var page in vm.Pages.Where(p=>p.Id==first.Id || p.Id==second.Id).ToArray())vm.RemovePageCommand.Execute(page);
    }
}
