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

    /// <summary>
    /// State of the inline edit pane. Null = pane is closed OR open in
    /// create mode; non-null = open in edit mode against this job id.
    /// EditCreate_Click branches on this to choose POST vs PATCH.
    /// </summary>
    private string? _editingJobId;

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
        ShowEditView(editingJob: null);
        // Kick off the models load the first time the form opens; cheap
        // enough that we don't bother reloading on subsequent opens.
        if (!_modelsLoaded)
        {
            _modelsLoaded = true;
            await LoadModelsAsync();
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        // Walk the rendered rows for the live `Source` snapshot. Using the
        // cached row vm avoids a refetch and keeps the form responsive even
        // if the gateway is briefly slow.
        Job? job = null;
        foreach (var row in Jobs)
        {
            if (row.Id == id) { job = row.Source; break; }
        }
        if (job is null) return;

        ShowEditView(editingJob: job);
        if (!_modelsLoaded)
        {
            _modelsLoaded = true;
            await LoadModelsAsync();
        }
    }

    private void ShowEditView(Job? editingJob)
    {
        // Reset state on every open so a previous attempt doesn't leak
        // through (especially the error bar, which would otherwise read
        // like a stale failure on a fresh form).
        EditErrorBar.IsOpen = false;
        SetEditBusy(false);

        _editingJobId = editingJob?.Id;
        var isEdit = editingJob is not null;

        // Field prefill — create mode clears everything, edit mode loads
        // the current server values verbatim.
        EditNameBox.Text = editingJob?.Name ?? "";
        // Prefer the structured expression for round-trippability: the
        // user typed e.g. `0 9 * * *` and the server stored `expr` =
        // `0 9 * * *`. `schedule_display` is the same string for cron,
        // but for intervals it can be pretty-printed differently.
        EditScheduleBox.Text = editingJob?.Schedule?.Expr ?? editingJob?.ScheduleDisplay ?? "";
        EditPromptBox.Text = editingJob?.Prompt ?? "";
        EditDeliverBox.Text = editingJob?.Deliver ?? "";
        EditEnabledSwitch.IsOn = editingJob?.Enabled ?? true;
        UpdatePromptCounter();

        // Model selection: match by id if the server reports one and it's
        // already in the options list (which is the case after the lazy
        // models load completes). If not, sit on the server-default
        // sentinel — that's the safer fallback than picking a wrong row.
        EditModelCombo.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(editingJob?.Model))
        {
            EditModelCombo.SelectedValue = editingJob!.Model;
            if (EditModelCombo.SelectedItem is null) EditModelCombo.SelectedIndex = 0;
        }

        // Swap titles + button labels for the mode. Subtle but the only
        // visual signal that distinguishes create-vs-edit at a glance.
        EditTitle.Text = isEdit ? "Edit job" : "New job";
        EditCreateLabel.Text = isEdit ? "Save" : "Create";

        ListView.Visibility = Visibility.Collapsed;
        EditView.Visibility = Visibility.Visible;
        // Drop the user straight into the first field so they can start
        // typing (or editing) without an extra click.
        EditNameBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Server-side cap on the prompt body. Discovered empirically (the
    /// gateway returns <c>{"error":"Prompt must be \u2264 5000 characters"}</c>
    /// on a 400 when it's exceeded). Mirrored here so the user sees the
    /// limit as they type instead of via a server round-trip.
    /// </summary>
    private const int PromptMaxChars = 5000;

    private void EditPromptBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePromptCounter();

    private void UpdatePromptCounter()
    {
        var len = EditPromptBox.Text?.Length ?? 0;
        EditPromptCounter.Text = $"{len} / {PromptMaxChars}";
        // Use the system error brush past the cap so it's obvious before
        // the user even tries to submit. Stay tertiary while under-cap so
        // it doesn't compete visually with the prompt body.
        var brushKey = len > PromptMaxChars
            ? "SystemFillColorCriticalBrush"
            : "TextFillColorTertiaryBrush";
        EditPromptCounter.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brushKey];
    }

    private void ShowListView()
    {
        EditView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
        _editingJobId = null;
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
        else if (prompt.Length > PromptMaxChars) validation = $"Prompt is {prompt.Length:N0} characters — Hermes caps prompts at {PromptMaxChars:N0}. Trim it down.";

        if (validation is not null)
        {
            ShowEditError(validation);
            return;
        }

        var selectedModel = EditModelCombo.SelectedValue as string;
        var deliver = EditDeliverBox.Text?.Trim();
        var isEdit = _editingJobId is not null;

        SetEditBusy(true);
        try
        {
            if (isEdit)
            {
                // PATCH: send the full editable form. The server treats
                // omitted fields as "no change", but we want a Save button
                // to mean "make the server match the form". For Enabled we
                // pass the explicit toggle state rather than the omit-when-
                // true shortcut used on create.
                var update = new UpdateJobRequest(
                    Name: name,
                    Schedule: schedule,
                    Prompt: prompt,
                    Model: string.IsNullOrEmpty(selectedModel) ? null : selectedModel,
                    Deliver: string.IsNullOrEmpty(deliver) ? null : deliver,
                    Enabled: EditEnabledSwitch.IsOn);
                await _api.UpdateJobAsync(_editingJobId!, update, CancellationToken.None);
            }
            else
            {
                var req = new CreateJobRequest(
                    Name: name,
                    Schedule: schedule,
                    Prompt: prompt,
                    Model: string.IsNullOrEmpty(selectedModel) ? null : selectedModel,
                    Deliver: string.IsNullOrEmpty(deliver) ? null : deliver,
                    // Only send `enabled` when the user explicitly turned
                    // it off. The server defaults to enabled; omitting is
                    // less ambiguous than echoing `true` back.
                    Enabled: EditEnabledSwitch.IsOn ? null : false);
                await _api.CreateJobAsync(req, CancellationToken.None);
            }

            ShowListView();
            // Reload from the server rather than splicing the local list —
            // ensures we pick up server-computed fields like next_run_at
            // (which the server recomputes when the schedule changes), and
            // avoids drift if the server normalised any input.
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
