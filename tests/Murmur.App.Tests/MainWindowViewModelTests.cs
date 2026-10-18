using System.ComponentModel;
using Murmur.App.ViewModels;

namespace Murmur.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Defaults_AreEmptySearchAndZeroTimer()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal("00:00:00", vm.TimerSample);
    }

    [Fact]
    public void SearchText_RaisesPropertyChanged()
    {
        var vm = new MainWindowViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SearchText = "standup";

        Assert.Equal([nameof(MainWindowViewModel.SearchText)], raised);
        Assert.Equal("standup", vm.SearchText);
    }

    [Fact]
    public void SearchText_SettingSameValue_DoesNotRaisePropertyChanged()
    {
        var vm = new MainWindowViewModel();
        var count = 0;
        PropertyChangedEventHandler handler = (_, _) => count++;
        vm.PropertyChanged += handler;

        vm.SearchText = vm.SearchText;

        Assert.Equal(0, count);
    }

    [Fact]
    public void PlaceholderCommands_AreExecutableNoOps()
    {
        var vm = new MainWindowViewModel();

        Assert.True(vm.StartRecordingCommand.CanExecute(null));
        Assert.True(vm.OpenSettingsCommand.CanExecute(null));
        vm.StartRecordingCommand.Execute(null);
        vm.OpenSettingsCommand.Execute(null);
    }
}
