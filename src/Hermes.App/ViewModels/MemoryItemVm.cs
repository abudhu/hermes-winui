using System;
using System.IO;

namespace Hermes.App.ViewModels;

public sealed class MemoryItemVm
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string WhenText { get; set; } = "";

    public static MemoryItemVm FromFile(FileInfo f, string root)
    {
        var rel = Path.GetRelativePath(root, f.FullName);
        return new MemoryItemVm
        {
            Name = f.Name,
            RelativePath = rel,
            FullPath = f.FullName,
            SizeText = FormatBytes(f.Length),
            WhenText = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
        };
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        };
    }
}
