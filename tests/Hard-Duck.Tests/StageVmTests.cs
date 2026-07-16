using System.Collections.Generic;
using System.Windows.Media;
using HardDuck;
using MaterialDesignThemes.Wpf;
using Xunit;

namespace HardDuck.Tests;

public class StageVmTests
{
    [Fact]
    public void Constructor_SetsInitialValues()
    {
        // Act
        var vm = new StageVm("TestKey", "Test Title");

        // Assert
        Assert.Equal("TestKey", vm.Key);
        Assert.Equal("Test Title", vm.Title);
        Assert.Equal(StageStatus.Pending, vm.Status);
        Assert.Equal("очікує", vm.Summary);
    }

    [Theory]
    [InlineData(StageStatus.Ok, PackIconKind.CheckCircle)]
    [InlineData(StageStatus.Warn, PackIconKind.Alert)]
    [InlineData(StageStatus.Fail, PackIconKind.CloseCircle)]
    [InlineData(StageStatus.Running, PackIconKind.Sync)]
    [InlineData(StageStatus.Skip, PackIconKind.MinusCircle)]
    [InlineData(StageStatus.Pending, PackIconKind.CircleOutline)]
    public void StatusIcon_MapsCorrectly(StageStatus status, PackIconKind expectedIcon)
    {
        // Arrange
        var vm = new StageVm("K", "T");

        // Act
        vm.Status = status;

        // Assert
        Assert.Equal(expectedIcon, vm.StatusIcon);
    }

    [Fact]
    public void StatusBrush_MapsCorrectColors()
    {
        // Arrange
        var vm = new StageVm("K", "T");

        // Act & Assert for Ok
        vm.Status = StageStatus.Ok;
        var okBrush = Assert.IsType<SolidColorBrush>(vm.StatusBrush);
        Assert.Equal(Color.FromRgb(0x3F, 0xB6, 0x6E), okBrush.Color);

        // Act & Assert for Fail
        vm.Status = StageStatus.Fail;
        var failBrush = Assert.IsType<SolidColorBrush>(vm.StatusBrush);
        Assert.Equal(Color.FromRgb(0xE0, 0x4F, 0x4F), failBrush.Color);
    }

    [Fact]
    public void StatusText_FormatCheck()
    {
        // Arrange
        var vm = new StageVm("K", "T");

        // Act & Assert default pending
        Assert.Equal("очікує", vm.StatusText);

        // Act & Assert OK with standard message
        vm.Status = StageStatus.Ok;
        vm.Summary = "OK";
        Assert.Equal("OK", vm.StatusText);

        // Act & Assert OK with custom message
        vm.Summary = "Custom Ok Detail";
        Assert.Equal("OK — Custom Ok Detail", vm.StatusText);

        // Act & Assert Fail with message
        vm.Status = StageStatus.Fail;
        vm.Summary = "Critical Error";
        Assert.Equal("Critical Error", vm.StatusText);
    }

    [Fact]
    public void PropertyChanged_FiresCorrectly_OnStatusChange()
    {
        // Arrange
        var vm = new StageVm("K", "T");
        var firedProperties = new List<string>();
        vm.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName != null)
            {
                firedProperties.Add(e.PropertyName);
            }
        };

        // Act
        vm.Status = StageStatus.Running;

        // Assert
        Assert.Contains("Status", firedProperties);
        Assert.Contains("StatusText", firedProperties);
        Assert.Contains("StatusBrush", firedProperties);
        Assert.Contains("StatusIcon", firedProperties);
    }

    [Fact]
    public void PropertyChanged_FiresCorrectly_OnSummaryChange()
    {
        // Arrange
        var vm = new StageVm("K", "T");
        var firedProperties = new List<string>();
        vm.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName != null)
            {
                firedProperties.Add(e.PropertyName);
            }
        };

        // Act
        vm.Summary = "In progress...";

        // Assert
        Assert.Contains("Summary", firedProperties);
        Assert.Contains("StatusText", firedProperties);
    }
}
