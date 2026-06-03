using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Hermes.App.Pages;

public sealed partial class JobsPage : Page
{
    private readonly HermesApiClient _api;
    public ObservableCollection<JobRowVm> Jobs { get; } = [];

    /// <summary>
    /// Backs the EditView's model picker. Seeded with the server-default
    /// sentinel up front; populated lazily on first NewJob_Click.
    /// </summary>
    public ObservableCollection<ModelOptionVm> ModelOptions { get; } = [];

    private bool _modelsLoaded;

    public JobsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        InitializeComponent();

        // Always include the "(server default)" entry so the combo is
        // never empty even before /v1/models lands.
        ModelOptions.Add(ModelOptionVm.ServerDefault());
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
            var jobsResponse = await _api.GetJobsAsync(CancellationToken.None);
            Jobs.Clear();
            foreach (var j in jobsResponse?.Jobs ?? [])
            {
                Jobs.Add(JobRowVm.FromJob(j));
            }
            Subtitle.Text = $"{Jobs.Count} job{(Jobs.Count == 1 ? "" : "s")}";
            EmptyState.Visibility = Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ListScroll.Visibility = Jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Subtitle absorbs load failures (they're rare and persistent —
            // a banner would draw too much attention). Action-failure errors
            // get the InfoBar treatment instead.
            Subtitle.Text = $"Error: {ex.Message}";
        }
    }

    private async void NewJob_Click(object sender, RoutedEventArgs e)
    {
        ShowEditView();
        // Kick off the models load the first time the form opens; cheap
        // enough that we don't bother reloading on subsequent opens.
        if (!_modelsLoaded)
        {
            _modelsLoaded = true;
            await LoadModelsAsync();
        }
    }

    private void ShowEditView()
    {
        // Reset form state on every open so the previous attempt doesn't
        // leak through (especially the error bar, which would otherwise
        // look like a stale failure on a fresh form).
        EditErrorBar.IsOpen = false;
        EditNameBox.Text = "";
        EditScheduleBox.Text = "";
        EditPromptBox.Text = "";
        EditDeliverBox.Text = "";
        EditEnabledSwitch.IsOn = true;
        EditModelCombo.SelectedIndex = 0;
        SetEditBusy(false);

        ListView.Visibility = Visibility.Collapsed;
        EditView.Visibility = Visibility.Visible;
        // Drop the user straight into the first field so they can start
        // typing without an extra click.
        EditNameBox.Focus(FocusState.Programmatic);
    }

    private void ShowListView()
    {
        EditView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
    }

    private void EditCancel_Click(object sender, RoutedEventArgs e) => ShowListView();

    private async void EditCreate_Click(object sender, RoutedEventArgs e)
    {
        EditErrorBar.IsOpen = false;

        var name = EditNameBox.Text?.Trim() ?? "";
        var schedule = EditScheduleBox.Text?.Trim() ?? "";
        var prompt = EditPromptBox.Text?.Trim() ?? "";

        // Client-side gate on the required trio. Matches what the server
        // enforces; catching them here avoids a network round-trip just
        // to show the same message back.
        string? validation = null;
        if (string.IsNullOrEmpty(name)) validation = "Name is required.";
        else if (string.IsNullOrEmpty(schedule)) validation = "Schedule is required.";
        else if (string.IsNullOrEmpty(prompt)) validation = "Prompt is required (this is the task the agent will run).";

        if (validation is not null)
        {
            ShowEditError(validation);
            return;
        }

        var selectedModel = EditModelCombo.SelectedValue as string;
        var deliver = EditDeliverBox.Text?.Trim();

        var req = new CreateJobRequest(
            Name: name,
            Schedule: schedule,
            Prompt: prompt,
            Model: string.IsNullOrEmpty(selectedModel) ? null : selectedModel,
            Deliver: string.IsNullOrEmpty(deliver) ? null : deliver,
            // Only send `enabled` when the user explicitly turned it off.
            // The server defaults to enabled; omitting is less ambiguous
            // than echoing `true` back.
            Enabled: EditEnabledSwitch.IsOn ? null : false);

        SetEditBusy(true);
        try
        {
            await _api.CreateJobAsync(req, CancellationToken.None);
            ShowListView();
            // Reload from the server rather than splicing the local list —
            // ensures we pick up server-computed fields like next_run_at,
            // and avoids drift if the server normalised any input.
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowEditError(ex.Message);
        }
        finally
        {
            SetEditBusy(false);
        }
    }

    private void SetEditBusy(bool busy)
    {
        EditCreateButton.IsEnabled = !busy;
        EditCreateSpinner.IsActive = busy;
        EditCreateSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEditError(string message)
    {
        EditErrorBar.Message = message;
        EditErrorBar.IsOpen = true;
    }

    private async Task LoadModelsAsync()
    {
        EditModelHint.Visibility = Visibility.Visible;
        EditModelHint.Text = "Loading models…";
        try
        {
            var resp = await _api.GetModelsAsync(CancellationToken.None);
            // Capture current selection so we restore it after mutating the
            // collection (Add reshuffles SelectedIndex on some WinUI builds).
            var selectedId = EditModelCombo.SelectedValue as string;
            foreach (var m in resp?.Data ?? [])
            {
                if (m.Id is null) continue;
                if (ModelOptions.Any(x => x.Id == m.Id)) continue;
                ModelOptions.Add(ModelOptionVm.FromModel(m));
            }
            // Re-resolve selection by id rather than index — Add can shift
            // SelectedIndex even when the underlying item is unchanged.
            EditModelCombo.SelectedValue = selectedId;
            if (EditModelCombo.SelectedItem is null) EditModelCombo.SelectedIndex = 0;
            EditModelHint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Don't block the user — the field stays usable with just the
            // server-default option, and the inline hint explains why no
            // other choices appeared. Reset the latched flag so a retry
            // happens next time the form opens.
            _modelsLoaded = false;
            EditModelHint.Text = $"Couldn't load models: {ex.Message}";
            EditModelHint.Visibility = Visibility.Visible;
        }
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e) =>
        await RunMutation(sender, "run", (api, id, ct) => api.RunJobAsync(id, ct));

    private async void Pause_Click(object sender, RoutedEventArgs e) =>
        await RunMutation(sender, "pause", (api, id, ct) => api.PauseJobAsync(id, ct));

    private async void Resume_Click(object sender, RoutedEventArgs e) =>
        await RunMutation(sender, "resume", (api, id, ct) => api.ResumeJobAsync(id, ct));

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        // Find the row's friendly name so the confirm dialog says something
        // human-readable. Falls back to the id if the row vm is missing.
        var rowName = id;
        foreach (var row in Jobs)
        {
            if (row.Id == id) { rowName = row.DisplayName; break; }
        }

        var confirm = new ContentDialog
        {
            Title = "Delete job?",
            Content = $"Permanently delete \"{rowName}\"? This can't be undone.",
            PrimaryButtonText = "Delete",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = this.XamlRoot,
        };
        var result = await confirm.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            ErrorBar.IsOpen = false;
            await _api.DeleteJobAsync(id, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't delete job: {ex.Message}");
        }
    }

    /// <summary>
    /// Shared path for run/pause/resume — they all share the same Tag→id
    /// extraction, the same try/catch wrapper, and the same "refresh on
    /// success, banner on failure" flow.
    /// </summary>
    private async Task RunMutation(object sender, string label,
        Func<HermesApiClient, string, CancellationToken, Task<ApiClient.Models.Job?>> op)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        try
        {
            ErrorBar.IsOpen = false;
            await op(_api, id, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't {label} job: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
