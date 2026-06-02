using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hermes.App.Pages;

public sealed partial class SkillsPage : Page
{
    private readonly HermesApiClient _api;
    public ObservableCollection<SkillGroupVm> Groups { get; } = [];

    public SkillsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        Subtitle.Text = "Loading…";
        try
        {
            var list = await _api.GetSkillsAsync(CancellationToken.None);
            Groups.Clear();
            var skills = list?.Data ?? [];
            // Group "null"/empty category last under "Uncategorized" so it doesn't
            // crowd the top of the page.
            var grouped = skills
                .GroupBy(s => string.IsNullOrWhiteSpace(s.Category) ? "Uncategorized" : s.Category!)
                .OrderBy(g => g.Key == "Uncategorized" ? 1 : 0)
                .ThenBy(g => g.Key);
            foreach (var g in grouped)
            {
                var grp = new SkillGroupVm(g.Key);
                foreach (var s in g.OrderBy(s => s.Name))
                {
                    grp.Skills.Add(new SkillCardVm(s.Name, s.Description ?? ""));
                }
                Groups.Add(grp);
            }
            Subtitle.Text = $"{skills.Count} skill{(skills.Count == 1 ? "" : "s")} in {Groups.Count} categor{(Groups.Count == 1 ? "y" : "ies")}";
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Error: {ex.Message}";
        }
    }
}
