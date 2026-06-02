using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hermes.App.Pages;

public sealed partial class MemoriesPage : Page
{
    private readonly HermesApiClient _api;
    private string _memoryRoot = "";

    public ObservableCollection<MemoryItemVm> Items { get; } = [];

    public MemoriesPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_memoryRoot)) return;
        try
        {
            // explorer.exe gracefully handles a path that doesn't exist by
            // surfacing the right error to the user — we just open the parent
            // when ours is missing so they don't see nothing happen.
            var target = Directory.Exists(_memoryRoot) ? _memoryRoot : _api.Config.ConfigDirectory ?? _memoryRoot;
            Process.Start("explorer.exe", $"\"{target}\"");
        }
        catch { /* swallow — user will see explorer error if any */ }
    }

    private void Refresh()
    {
        _memoryRoot = Path.Combine(_api.Config.ConfigDirectory ?? "", "memories");
        MemoryPathLabel.Text = _memoryRoot;
        Items.Clear();

        if (!Directory.Exists(_memoryRoot))
        {
            Subtitle.Text = "Memories directory not found";
            EmptyHelp.Text = $"Looked for: {_memoryRoot}";
            EmptyState.Visibility = Visibility.Visible;
            ContentGrid.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var files = new DirectoryInfo(_memoryRoot)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var f in files)
            {
                Items.Add(MemoryItemVm.FromFile(f, _memoryRoot));
            }

            Subtitle.Text = files.Count == 0
                ? "Empty — no memory files yet"
                : $"{files.Count} file{(files.Count == 1 ? "" : "s")}";

            EmptyState.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ContentGrid.Visibility = files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Error: {ex.Message}";
            EmptyState.Visibility = Visibility.Visible;
            EmptyHelp.Text = ex.Message;
        }
    }
}
