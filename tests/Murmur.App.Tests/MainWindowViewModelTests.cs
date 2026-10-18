using System.ComponentModel;
using Murmur.App.ViewModels;

namespace Murmur.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Status_DefaultsToReady()
    {
        Assert.Equal("Ready", new MainWindowViewModel().Status);
    }

    [Fact]
    public void Status_RaisesPropertyChanged()
    {
        var vm = new MainWindowViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Status = "Recording";

        Assert.Equal([nameof(MainWindowViewModel.Status)], raised);
        Assert.Equal("Recording", vm.Status);
    }

    [Fact]
    public void Status_SettingSameValue_DoesNotRaisePropertyChanged()
    {
        var vm = new MainWindowViewModel();
        var count = 0;
        PropertyChangedEventHandler handler = (_, _) => count++;
        vm.PropertyChanged += handler;

        vm.Status = vm.Status;

        Assert.Equal(0, count);
    }
}
