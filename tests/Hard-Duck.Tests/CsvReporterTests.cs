using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HardDuck;
using Xunit;

namespace HardDuck.Tests;

public sealed class CsvReporterTests : IDisposable
{
    private readonly string _tempDir;

    public CsvReporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HardDuckTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        CsvReporter.Directory = _tempDir;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Append_CreatesFileWithHeader_WhenNotExists()
    {
        // Arrange
        var values = new Dictionary<string, string>
        {
            ["Computer"] = "TEST-PC",
            ["Timestamp"] = "2026-07-16T12:00:00"
        };

        // Act
        var path = CsvReporter.Append(values);

        // Assert
        Assert.True(File.Exists(path));
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Computer,Timestamp,SecureBoot", lines[0]);
        Assert.StartsWith("TEST-PC,2026-07-16T12:00:00,", lines[1]);
    }

    [Fact]
    public void Append_EscapesSpecialCharacters()
    {
        // Arrange
        var values = new Dictionary<string, string>
        {
            ["Computer"] = "TEST-PC",
            ["SecureBoot"] = "OK, with comma",
            ["TPM"] = "Value \"with quotes\"",
            ["BitLockerPin"] = "Value\nwith newline"
        };

        // Act
        var path = CsvReporter.Append(values);

        // Assert
        var text = File.ReadAllText(path);
        Assert.Contains("\"OK, with comma\"", text);
        Assert.Contains("\"Value \"\"with quotes\"\"\"", text);
        Assert.Contains("\"Value\nwith newline\"", text);
    }

    [Fact]
    public void Append_ArchivesOldFile_WhenHeaderDiffers()
    {
        // Arrange
        var path = CsvReporter.CsvPath;
        File.WriteAllText(path, "Old,Header,Fields\nold,val,1");

        var values = new Dictionary<string, string>
        {
            ["Computer"] = "NEW-PC"
        };

        // Act
        CsvReporter.Append(values);

        // Assert
        // The original CsvPath should now contain the new format (Header + 1 data line)
        var lines = File.ReadAllLines(path);
        Assert.StartsWith("Computer,Timestamp", lines[0]);

        // There should be an archived file in the directory
        var files = Directory.GetFiles(_tempDir, "harden-status-old-*.csv");
        Assert.Single(files);
        var oldLines = File.ReadAllLines(files[0]);
        Assert.Equal("Old,Header,Fields", oldLines[0]);
        Assert.Equal("old,val,1", oldLines[1]);
    }
}
