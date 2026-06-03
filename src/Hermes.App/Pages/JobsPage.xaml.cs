using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;
using Hermes.App.Services;
using Hermes.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Hermes.App.Pages;

public sealed partial class JobsPage : Page
{
    private readonly HermesApiClient _api;
    private readonly NotificationService _notifications;
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

    // -------------------------------------------------------------------
    // Selection + polling state
    // -------------------------------------------------------------------

    /// <summary>Id of the currently-selected row, or null. Preserved across
    /// list refreshes so the detail pane doesn't flicker / lose context
    /// while polling.</summary>
    private string? _selectedJobId;

    /// <summary>Tracks (state, last_status, last_run_at) per id so the
    /// polling loop can detect Running→done/failed/cancelled transitions
    /// without relying on state alone — a job can return to `scheduled`
    /// post-run and the completion only surfaces in last_status.</summary>
    private readonly Dictionary<string, (string? State, string? LastStatus, string? LastRunAt)> _prevSnapshots = [];

    /// <summary>Cleared on first refresh — until then we suppress all
    /// toasts, since we have no baseline to know if a job "just" finished
    /// vs has been done since before the page opened.</summary>
    private bool _hasBaseline;

    private DispatcherTimer? _pollTimer;
    private DateTimeOffset _lastTransitionAt = DateTimeOffset.MinValue;

    /// <summary>Re-entry guard: tick handler is async-void so a slow
    /// refresh + manual refresh + action-refresh could otherwise stack.
    /// Set inside RefreshAsync; checked at entry.</summary>
    private bool _refreshing;

    /// <summary>Incremented on OnNavigatedFrom so any pending RefreshAsync
    /// continuations can no-op on UI mutation rather than touching an
    /// inactive page.</summary>
    private int _navigationGeneration;

    /// <summary>3s while a job is actively running OR transitions are
    /// happening; 10s once we've been idle for a minute. The DispatcherTimer
    /// reads this each tick, so changes take effect on the next interval
    /// without restarting.</summary>
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleBackoffAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Cross-page deep-link channel. Set by App.HandleJobNotificationActivated
    /// before flipping NavView selection to the Jobs item; consumed by
    /// OnNavigatedTo after the first refresh resolves the row. Static so
    /// the cold-start handler can write it before any JobsPage instance
    /// exists.
    /// </summary>
    public static string? PendingSelectedJobId { get; set; }

    public JobsPage()
    {
        _api = App.Services.GetRequiredService<HermesApiClient>();
        _notifications = App.Services.GetRequiredService<NotificationService>();
        InitializeComponent();

        // Always include the "(server default)" entry so the combo is
        // never empty even before /v1/models lands.
        ModelOptions.Add(ModelOptionVm.ServerDefault());
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Drain a pending deep-link first so the initial refresh
        // already finds the right row to select.
        var pending = PendingSelectedJobId;
        PendingSelectedJobId = null;
        if (!string.IsNullOrEmpty(pending))
        {
            _selectedJobId = pending;
        }

        await RefreshAsync();
        EvaluatePollingNeed();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Bump generation so any in-flight RefreshAsync continuation
        // sees it doesn't own the UI anymore.
        _navigationGeneration++;
        StopPolling();
    }

    /// <summary>
    /// Called by <see cref="App.HandleJobNotificationActivated"/> when a
    /// toast deep-link fires while JobsPage is already the visible page.
    /// </summary>
    public void SelectJobById(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return;
        _selectedJobId = jobId;
        ApplySelectionToRows();
        UpdateDetailPane();
        EvaluatePollingNeed();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void DetailRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedJobId is null) { await RefreshAsync(); EvaluatePollingNeed(); return; }

        SetDetailRefreshBusy(true);
        try
        {
            // Hit the single-job endpoint when we have a selection — fewer
            // bytes on the wire and the user is asking for that one row.
            // We still drop the result into the polling pipeline via
            // RefreshAsync's snapshot path by doing a full list refresh
            // afterward, so transition detection stays consistent.
            ErrorBar.IsOpen = false;
            await _api.GetJobAsync(_selectedJobId, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't refresh job: {ex.Message}");
        }
        finally
        {
            SetDetailRefreshBusy(false);
            EvaluatePollingNeed();
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        var generation = _navigationGeneration;
        try
        {
            Subtitle.Text = "Loading…";
            var jobsResponse = await _api.GetJobsAsync(CancellationToken.None);
            if (generation != _navigationGeneration) return;

            // Snapshot diff: walk the new payload, compare to _prevSnapshots,
            // queue toasts for any Running→non-running transition where the
            // last_status / last_run_at advanced. Built BEFORE we replace
            // Jobs so order-of-operations is obvious.
            var transitions = DetectCompletions(jobsResponse?.Jobs ?? []);

            Jobs.Clear();
            foreach (var j in jobsResponse?.Jobs ?? [])
            {
                Jobs.Add(JobRowVm.FromJob(j));
            }

            // Restore selection by id if the row still exists; otherwise
            // clear it so the detail pane returns to its empty state.
            ApplySelectionToRows();

            Subtitle.Text = $"{Jobs.Count} job{(Jobs.Count == 1 ? "" : "s")}";
            EmptyState.Visibility = Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MasterDetail.Visibility = Jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ListScroll.Visibility = Jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            UpdateDetailPane();

            // Snapshots updated AFTER UI commit — if Jobs.Clear ever throws,
            // we'd rather miss one transition than fire a toast for a row
            // the user can't see.
            RebuildSnapshots(jobsResponse?.Jobs ?? []);

            // Fire toasts only once we've seen at least one prior refresh.
            // Avoids "you finished 12 jobs!" spam the first time a user
            // opens the page on a long-running install.
            if (_hasBaseline)
            {
                foreach (var t in transitions)
                {
                    _notifications.NotifyJobCompleted(t.Id, t.DisplayName, t.StatusLabel, t.ErrorMessage);
                    _lastTransitionAt = DateTimeOffset.UtcNow;
                }
            }
            _hasBaseline = true;

            EvaluatePollingNeed();
        }
        catch (OperationCanceledException)
        {
            // Polling timer raced with navigation — swallow quietly.
        }
        catch (Exception ex)
        {
            if (generation != _navigationGeneration) return;
            // Subtitle absorbs load failures (they're rare and persistent —
            // a banner would draw too much attention). Action-failure errors
            // get the InfoBar treatment instead.
            Subtitle.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    // -------------------------------------------------------------------
    // Selection
    // -------------------------------------------------------------------

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        // Toggle off if they click the already-selected row — gives the
        // user an explicit "deselect" gesture without an X button.
        _selectedJobId = string.Equals(_selectedJobId, id, StringComparison.Ordinal) ? null : id;
        ApplySelectionToRows();
        UpdateDetailPane();
        EvaluatePollingNeed();
    }

    private void ApplySelectionToRows()
    {
        var found = false;
        foreach (var row in Jobs)
        {
            var isMatch = !string.IsNullOrEmpty(_selectedJobId)
                          && string.Equals(row.Id, _selectedJobId, StringComparison.Ordinal);
            row.IsSelected = isMatch;
            if (isMatch) found = true;
        }
        // Selection points at a row that vanished (deleted on the server,
        // or hasn't loaded yet) — clear so the detail pane shows empty
        // state rather than stale data.
        if (!found && !string.IsNullOrEmpty(_selectedJobId)) _selectedJobId = null;
    }

    private JobRowVm? SelectedRow()
    {
        if (string.IsNullOrEmpty(_selectedJobId)) return null;
        foreach (var row in Jobs)
        {
            if (row.Id == _selectedJobId) return row;
        }
        return null;
    }

    private void UpdateDetailPane()
    {
        var row = SelectedRow();
        if (row is null)
        {
            DetailPane.Visibility = Visibility.Collapsed;
            DetailEmptyState.Visibility = Visibility.Visible;
            return;
        }

        DetailPane.Visibility = Visibility.Visible;
        DetailEmptyState.Visibility = Visibility.Collapsed;

        DetailNameText.Text = row.DisplayName;
        DetailStatusText.Text = row.StatusLabel;
        DetailStatusBadge.Background = row.StatusBackground;
        DetailStatusText.Foreground = row.StatusForeground;
        DetailLastRunText.Text = row.LastRunLabel;
        ToolTipService.SetToolTip(DetailLastRunText, string.IsNullOrEmpty(row.LastRunTooltip) ? null : row.LastRunTooltip);

        DetailScheduleText.Text = row.ScheduleLabel;
        DetailNextRunText.Text = row.NextRunLabel;
        ToolTipService.SetToolTip(DetailNextRunText, string.IsNullOrEmpty(row.NextRunTooltip) ? null : row.NextRunTooltip);

        DetailDeliverText.Text = row.DeliverLabel;
        DetailModelText.Text = string.IsNullOrEmpty(row.ModelLabel) ? "default model" : row.ModelLabel!;

        DetailLastErrorBar.IsOpen = row.HasLastError;
        DetailLastErrorBar.Message = row.LastErrorLabel ?? "";
        DetailDeliveryErrorBar.IsOpen = row.HasLastDeliveryError;
        DetailDeliveryErrorBar.Message = row.LastDeliveryErrorLabel ?? "";

        DetailPromptText.Text = string.IsNullOrEmpty(row.Prompt) ? "(no prompt)" : row.Prompt;

        // Pause vs Resume are mutually exclusive — show only the one
        // that would do something useful.
        DetailPauseButton.Visibility = row.IsPaused ? Visibility.Collapsed : Visibility.Visible;
        DetailResumeButton.Visibility = row.IsPaused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDetailRefreshBusy(bool busy)
    {
        DetailRefreshButton.IsEnabled = !busy;
        DetailRefreshSpinner.IsActive = busy;
        DetailRefreshSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DetailRefreshIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    // -------------------------------------------------------------------
    // Polling lifecycle
    // -------------------------------------------------------------------

    /// <summary>
    /// Starts polling if any job is running OR a row is selected; otherwise
    /// stops it. Called after every refresh / selection change so the timer
    /// stays in sync with the page state without bookkeeping at each
    /// callsite.
    /// </summary>
    private void EvaluatePollingNeed()
    {
        var anyRunning = false;
        foreach (var row in Jobs)
        {
            if (row.IsRunning) { anyRunning = true; break; }
        }
        var needsPolling = anyRunning || !string.IsNullOrEmpty(_selectedJobId);
        if (needsPolling)
        {
            StartOrAdjustPolling();
        }
        else
        {
            StopPolling();
        }
    }

    private void StartOrAdjustPolling()
    {
        if (_pollTimer is null)
        {
            _pollTimer = new DispatcherTimer { Interval = ActivePollInterval };
            _pollTimer.Tick += PollTimer_Tick;
        }
        // Recompute interval each call so a stale "active" timer falls
        // back to idle cadence once nothing has happened for a minute.
        var idleFor = DateTimeOffset.UtcNow - _lastTransitionAt;
        var interval = idleFor > IdleBackoffAfter ? IdlePollInterval : ActivePollInterval;
        if (_pollTimer.Interval != interval) _pollTimer.Interval = interval;
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer is null) return;
        if (_pollTimer.IsEnabled) _pollTimer.Stop();
    }

    private async void PollTimer_Tick(object? sender, object e)
    {
        // Tick may fire after StopPolling on some WinUI builds if the
        // tick was already queued — guard with the generation token too.
        var generation = _navigationGeneration;
        await RefreshAsync();
        if (generation != _navigationGeneration) return;
        // EvaluatePollingNeed inside RefreshAsync handles cadence
        // adjustment, so no extra work here.
    }

    // -------------------------------------------------------------------
    // Transition detection → toast
    // -------------------------------------------------------------------

    /// <summary>
    /// Walks the new job list, returns a list of jobs that just transitioned
    /// from running → done/failed/cancelled (per the snapshot tuple). State
    /// alone is too noisy because the gateway re-marks finished jobs as
    /// `scheduled` for the next fire — we cross-check against last_status
    /// and last_run_at to make sure the completion is "new".
    /// </summary>
    private List<JobCompletion> DetectCompletions(IEnumerable<Job> newJobs)
    {
        var completions = new List<JobCompletion>();
        foreach (var job in newJobs)
        {
            if (!_prevSnapshots.TryGetValue(job.Id, out var prev)) continue;

            var prevWasRunning = string.Equals(prev.State, "running", StringComparison.OrdinalIgnoreCase);
            if (!prevWasRunning) continue;

            var nowRunning = string.Equals(job.State, "running", StringComparison.OrdinalIgnoreCase);
            if (nowRunning) continue;

            // Treat it as a real completion if last_status changed OR
            // last_run_at advanced. Either signal is sufficient — some
            // gateway revs may bump one but not the other depending on
            // whether the delivery side errored.
            var statusChanged = !string.Equals(prev.LastStatus, job.LastStatus, StringComparison.Ordinal);
            var lastRunChanged = !string.Equals(prev.LastRunAt, job.LastRunAt, StringComparison.Ordinal);
            if (!statusChanged && !lastRunChanged) continue;

            var statusLabel = string.IsNullOrEmpty(job.LastStatus)
                ? (string.IsNullOrEmpty(job.State) ? "finished" : job.State!)
                : job.LastStatus!;
            var errorMessage = job.LastError ?? job.LastDeliveryError;
            var displayName = !string.IsNullOrWhiteSpace(job.Name) ? job.Name! : job.Id;
            completions.Add(new JobCompletion(job.Id, displayName, statusLabel, errorMessage));
        }
        return completions;
    }

    private void RebuildSnapshots(IEnumerable<Job> jobs)
    {
        _prevSnapshots.Clear();
        foreach (var job in jobs)
        {
            _prevSnapshots[job.Id] = (job.State, job.LastStatus, job.LastRunAt);
        }
    }

    private readonly record struct JobCompletion(string Id, string DisplayName, string StatusLabel, string? ErrorMessage);

    // -------------------------------------------------------------------
    // Detail-pane action handlers (replace per-row buttons)
    // -------------------------------------------------------------------

    private async void DetailRun_Click(object sender, RoutedEventArgs e) =>
        await RunMutationOnSelected("run", (api, id, ct) => api.RunJobAsync(id, ct));

    private async void DetailPause_Click(object sender, RoutedEventArgs e) =>
        await RunMutationOnSelected("pause", (api, id, ct) => api.PauseJobAsync(id, ct));

    private async void DetailResume_Click(object sender, RoutedEventArgs e) =>
        await RunMutationOnSelected("resume", (api, id, ct) => api.ResumeJobAsync(id, ct));

    private async void DetailEdit_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow();
        if (row?.Source is null) return;

        ShowEditView(editingJob: row.Source);
        if (!_modelsLoaded)
        {
            _modelsLoaded = true;
            await LoadModelsAsync();
        }
    }

    private async void DetailDelete_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow();
        if (row is null) return;

        var confirm = new ContentDialog
        {
            Title = "Delete job?",
            Content = $"Permanently delete \"{row.DisplayName}\"? This can't be undone.",
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
            await _api.DeleteJobAsync(row.Id, CancellationToken.None);
            _selectedJobId = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't delete job: {ex.Message}");
        }
    }

    private async Task RunMutationOnSelected(string label,
        Func<HermesApiClient, string, CancellationToken, Task<Job?>> op)
    {
        var row = SelectedRow();
        if (row is null) return;
        try
        {
            ErrorBar.IsOpen = false;
            await op(_api, row.Id, CancellationToken.None);
            // Hand the user immediate feedback — the run mutation in
            // particular flips state to "running", which kicks off
            // polling via EvaluatePollingNeed.
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't {label} job: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // Create / edit form (unchanged from the prior CRUD ship)
    // -------------------------------------------------------------------

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
        EditPromptCounter.Foreground = (Brush)Application.Current.Resources[brushKey];
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

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
