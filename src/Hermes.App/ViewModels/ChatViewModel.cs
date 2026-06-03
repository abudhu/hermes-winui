using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hermes.ApiClient;
using Hermes.ApiClient.Models;
using Microsoft.UI.Dispatching;

namespace Hermes.App.ViewModels;

/// <summary>
/// Chat surface state machine. Owns the transcript, the active session id,
/// and the two cancellation tokens that drive Send vs Stop independently.
///
/// <para>
/// Threading model: every public method (Send / Stop / NewChat) is invoked
/// from the UI thread. The streaming loop runs on a worker via
/// <c>Task.Run</c>; every mutation it makes is marshalled back to the UI
/// thread via <see cref="_dispatcher"/>.
/// </para>
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly HermesApiClient _api;
    private readonly HermesStreamingClient _stream;
    private readonly HermesConfig _config;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>Independent CTS for the active stream — cancelled by Stop or NewChat.</summary>
    private CancellationTokenSource? _streamCts;

    /// <summary>
    /// The currently-running SendAsync task, captured so other operations
    /// (ResumeSessionAsync, NewChat) can cancel the stream AND wait for the
    /// finally block to fully unwind before mutating shared state. Without
    /// this, Send's finally would race Resume's setup and overwrite IsBusy /
    /// _currentAssistant mid-load.
    /// </summary>
    private Task? _currentStreamTask;

    /// <summary>
    /// Coalesces assistant token flushes. We append to <c>MessageVm.Buffer</c>
    /// on the streaming worker and trigger a UI-thread flush every ~60ms;
    /// raising INPC on every token here would peg the UI thread and is a
    /// well-known footgun.
    /// </summary>
    private DispatcherQueueTimer? _flushTimer;

    /// <summary>Reference to the currently-streaming assistant message (so flushes don't have to search).</summary>
    private MessageVm? _currentAssistant;

    /// <summary>
    /// Run ids we've already accounted for in <see cref="SessionUsage"/>.
    /// Guards against the gateway re-emitting <c>run.completed</c> (network
    /// retry, reconnect, transient bug) and double-counting tokens into the
    /// header chip. Cleared on <see cref="NewChat"/> and at the top of
    /// <see cref="ResumeSessionAsync"/> since both transition to a fresh
    /// session context.
    /// </summary>
    private readonly HashSet<string> _seenRunIds = new(StringComparer.Ordinal);

    /// <summary>
    /// True only when the user actually changed the ComboBox selection.
    /// Programmatic seeding (constructor default, post-resume, post-create
    /// echo from the server) leaves this <see langword="false"/> so we
    /// don't accidentally start passing <c>model</c> in the create-session
    /// request just because we read the config default.
    /// </summary>
    private bool _userExplicitlyPicked;

    /// <summary>
    /// Wraps assignments to <see cref="SelectedModelId"/> that come from
    /// our own code (ctor seed, post-resume seed, post-create echo) so the
    /// <c>OnSelectedModelIdChanged</c> partial doesn't flip
    /// <see cref="_userExplicitlyPicked"/> to true. Set/cleared inside the
    /// same UI-thread call so we don't need a lock.
    /// </summary>
    private bool _suppressModelPickFlag;

    /// <summary>The in-flight model-list load, kept so concurrent
    /// <see cref="EnsureModelsLoadedAsync"/> callers coalesce onto one
    /// network request. Replaced (not awaited) by
    /// <see cref="ReloadModelsAsync"/> so the user can force a retry.</summary>
    private Task? _modelsLoadTask;

    public ObservableCollection<MessageVm> Messages { get; } = [];

    /// <summary>Wraps Messages.Count so the empty-state visibility binding can OneWay-bind to it.</summary>
    public bool HasMessages => Messages.Count > 0;

    [ObservableProperty]
    public partial string Composer { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial string? SessionId { get; set; }

    [ObservableProperty]
    public partial string? SessionTitle { get; set; }

    /// <summary>
    /// Running total of token usage for the active session. Updated when
    /// each <c>run.completed</c> arrives (adding to whatever <see cref="ResumeSessionAsync"/>
    /// seeded from the persistent session detail). <see langword="null"/>
    /// for fresh chats that haven't completed a turn yet.
    /// </summary>
    [ObservableProperty]
    public partial UsageStats? SessionUsage { get; set; }

    /// <summary>Optional dollar-cost estimate for the session, surfaced
    /// from <see cref="SessionDetail.EstimatedCostUsd"/> on Resume. Updated
    /// only when the gateway provides it.</summary>
    [ObservableProperty]
    public partial double? SessionCostUsd { get; set; }

    /// <summary>Formatted one-liner shown in the chat header strip.
    /// Empty when there's no usage to display.</summary>
    public string SessionUsageLine
    {
        get
        {
            var line = MessageVm.FormatUsage(SessionUsage, compact: true);
            if (SessionCostUsd is double c && c > 0)
            {
                var cost = c < 0.01
                    ? $"${c:F4}"
                    : $"${c:F2}";
                line = line.Length > 0 ? $"{line} \u00b7 {cost}" : cost;
            }
            return line;
        }
    }

    public bool HasSessionUsage => SessionUsage is { HasAny: true } || SessionCostUsd is > 0;

    /// <summary>Tooltip text for the session-total chip. Conditional on
    /// whether <see cref="SessionCostUsd"/> is actually rendered alongside
    /// the token count — saying "and estimated cost" when no cost is
    /// shown would be a lie.</summary>
    public string SessionUsageTooltip => SessionCostUsd is > 0
        ? "Total tokens and estimated cost for this session"
        : "Total tokens used in this session";

    // -------------------------------------------------------------------
    // Model picker
    // -------------------------------------------------------------------

    /// <summary>Lifecycle state of the <c>/v1/models</c> fetch. Drives
    /// the picker's visual mode (loading spinner, editable, or
    /// fallback chip).</summary>
    public enum ModelLoadState
    {
        NotLoaded,
        Loading,
        Loaded,
        Failed,
    }

    /// <summary>Available models from <c>/v1/models</c>, plus any
    /// synthetic entries that <see cref="ResumeSessionAsync"/> needed to
    /// add for sessions that reference models not in the live list.</summary>
    public ObservableCollection<ModelOptionVm> AvailableModels { get; } = [];

    [ObservableProperty]
    public partial string? SelectedModelId { get; set; }

    [ObservableProperty]
    public partial ModelLoadState ModelLoadStatus { get; set; } = ModelLoadState.NotLoaded;

    /// <summary>True when the picker is editable: a fresh (un-created)
    /// session AND the send pipeline isn't busy AND models have loaded.
    /// Once any of those flips, the picker swaps to the read-only chip.</summary>
    public bool CanPickModel => SessionId is null
                             && !IsBusy
                             && ModelLoadStatus == ModelLoadState.Loaded;

    /// <summary>Convenience inverse for the read-only chip's visibility
    /// binding — saves a converter on the XAML side.</summary>
    public bool IsModelLocked => !CanPickModel;

    /// <summary>True while <see cref="EnsureModelsLoadedAsync"/> is
    /// fetching. Drives a small inline progress indicator next to the
    /// picker.</summary>
    public bool ModelsAreLoading => ModelLoadStatus == ModelLoadState.Loading;

    /// <summary>True when <c>/v1/models</c> failed. The picker falls
    /// back to the global default; user can retry via
    /// <see cref="ReloadModelsCommand"/>.</summary>
    public bool ModelsFailed => ModelLoadStatus == ModelLoadState.Failed;

    /// <summary>What the read-only chip renders. Prefers the matched
    /// option's display name (handles synthetic "(unavailable)" suffix
    /// and "Model not reported"); falls back to the raw id, then to a
    /// generic label.</summary>
    public string SelectedModelDisplay
    {
        get
        {
            var match = AvailableModels.FirstOrDefault(m => m.Id == SelectedModelId);
            if (match is not null) return match.DisplayName;
            if (!string.IsNullOrWhiteSpace(SelectedModelId)) return SelectedModelId!;
            return "(default)";
        }
    }

    partial void OnSessionUsageChanged(UsageStats? value)
    {
        OnPropertyChanged(nameof(SessionUsageLine));
        OnPropertyChanged(nameof(HasSessionUsage));
    }

    partial void OnSessionCostUsdChanged(double? value)
    {
        OnPropertyChanged(nameof(SessionUsageLine));
        OnPropertyChanged(nameof(HasSessionUsage));
        OnPropertyChanged(nameof(SessionUsageTooltip));
    }

    partial void OnSessionIdChanged(string? value)
    {
        OnPropertyChanged(nameof(CanPickModel));
        OnPropertyChanged(nameof(IsModelLocked));
    }

    partial void OnSelectedModelIdChanged(string? value)
    {
        if (!_suppressModelPickFlag)
        {
            // The UI raised this — the user actually picked something.
            // From here on we'll pass the model on create-session.
            _userExplicitlyPicked = true;
        }
        OnPropertyChanged(nameof(SelectedModelDisplay));
    }

    partial void OnModelLoadStatusChanged(ModelLoadState value)
    {
        OnPropertyChanged(nameof(CanPickModel));
        OnPropertyChanged(nameof(IsModelLocked));
        OnPropertyChanged(nameof(ModelsAreLoading));
        OnPropertyChanged(nameof(ModelsFailed));
        ReloadModelsCommand.NotifyCanExecuteChanged();
    }

    public ChatViewModel(HermesApiClient api, HermesStreamingClient stream, HermesConfig config, DispatcherQueue dispatcher)
    {
        _api = api;
        _stream = stream;
        _config = config;
        _dispatcher = dispatcher;
        Messages.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasMessages));
        // SelectedModelDisplay reads from AvailableModels — re-raise on
        // membership change so the read-only chip's text refreshes when
        // EnsureSelectedModelPresent adds a synthetic entry.
        AvailableModels.CollectionChanged += (_, __) => OnPropertyChanged(nameof(SelectedModelDisplay));

        // Seed picker to the global default, marked as programmatic so the
        // _userExplicitlyPicked flag stays false.
        _suppressModelPickFlag = true;
        SelectedModelId = _config.ModelName;
        _suppressModelPickFlag = false;
    }

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(Composer);

    partial void OnComposerChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        // Picker locks while a send is in flight so the user can't change
        // model between EnsureSessionAsync read and the server response.
        OnPropertyChanged(nameof(CanPickModel));
        OnPropertyChanged(nameof(IsModelLocked));
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        // Capture the inner task so ResumeSessionAsync / NewChat can await
        // it after cancelling, ensuring the finally block has fully torn
        // down state before they start setting up theirs.
        _currentStreamTask = SendCoreAsync();
        try { await _currentStreamTask; }
        finally { _currentStreamTask = null; }
    }

    private async Task SendCoreAsync()
    {
        // Snapshot model intent BEFORE we mutate any UI state. The picker
        // is supposed to lock once IsBusy=true, but if the user managed to
        // change it between this read and EnsureSessionAsync's create call
        // the snapshot still captures their original intent.
        var modelOverride = _userExplicitlyPicked ? SelectedModelId : null;

        var text = (Composer ?? string.Empty).Trim();
        if (text.Length == 0) return;

        Composer = string.Empty;
        Messages.Add(new MessageVm
        {
            Role = MessageRole.User,
            Content = text,
            State = MessageState.Completed,
        });

        var assistant = new MessageVm
        {
            Role = MessageRole.Assistant,
            State = MessageState.Streaming,
            ThinkingLabel = ThinkingLabels.Pick(),
        };
        Messages.Add(assistant);
        _currentAssistant = assistant;

        IsBusy = true;
        StatusText = "Streaming…";

        if (!await EnsureSessionAsync(assistant, modelOverride)) return;

        StartFlushTimer();

        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;

        try
        {
            await Task.Run(async () =>
            {
                await foreach (var evt in _stream.StreamSessionChatAsync(SessionId!, text, token))
                {
                    HandleEvent(assistant, evt);
                }
            }, token);

            // Give any TryEnqueue'd handlers from the worker a chance to run
            // BEFORE we tear down `_currentAssistant`. Without this, late deltas
            // posted near the end of the stream race the finally block and get
            // dropped silently.
            await DrainDispatcherAsync();
            FinalFlush(assistant);
            if (assistant.State == MessageState.Streaming) assistant.State = MessageState.Completed;
            StatusText = "Ready";
        }
        catch (OperationCanceledException)
        {
            await DrainDispatcherAsync();
            FinalFlush(assistant);
            if (assistant.State == MessageState.Streaming) assistant.State = MessageState.Stopped;
            StatusText = "Stopped";
        }
        catch (Exception ex)
        {
            await DrainDispatcherAsync();
            FinalFlush(assistant);
            assistant.Buffer.Append("\n\n*Stream error: ").Append(ex.Message).Append('*');
            assistant.Content = assistant.Buffer.ToString();
            assistant.State = MessageState.Failed;
            StatusText = "Error";
        }
        finally
        {
            StopFlushTimer();
            _currentAssistant = null;
            IsBusy = false;
        }
    }

    public bool CanStop => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        // Cancel the stream FIRST so the IAsyncEnumerable unwinds promptly.
        // The Stop POST below needs its own (uncancelled) token — passing the
        // stream token would have us cancelling the very request we're trying
        // to send.
        _streamCts?.Cancel();
        StatusText = "Stopping…";
    }

    [RelayCommand]
    private void NewChat()
    {
        _streamCts?.Cancel();
        Messages.Clear();
        SessionId = null;
        SessionTitle = null;
        SessionUsage = null;
        SessionCostUsd = null;
        _seenRunIds.Clear();

        // Drop any synthetic entries left over from a prior resume; the
        // picker is now re-enabled for a fresh chat and stale "(unavailable)"
        // / "Model not reported" rows have no meaning here.
        for (int i = AvailableModels.Count - 1; i >= 0; i--)
        {
            if (AvailableModels[i].IsSynthetic) AvailableModels.RemoveAt(i);
        }

        // Reset picker to global default. Programmatic — don't mark as
        // user-picked.
        _suppressModelPickFlag = true;
        SelectedModelId = _config.ModelName;
        _suppressModelPickFlag = false;
        _userExplicitlyPicked = false;

        // If /v1/models hasn't loaded yet OR loaded but the configured
        // default isn't in the list, add a synthetic so the read-only
        // chip can still render something sensible.
        EnsureSelectedModelPresent();

        StatusText = "Ready";
    }

    /// <summary>
    /// Loads a server-side session's history into the transcript and adopts
    /// its <see cref="SessionId"/> so subsequent sends continue the same
    /// conversation. Safe to call mid-stream — it cancels the in-flight
    /// stream and waits for its finally block to unwind before mutating
    /// shared state (otherwise Send's finally races Resume's setup).
    /// </summary>
    /// <param name="sessionId">The server session id to resume.</param>
    public async Task ResumeSessionAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        // 1. Cancel any in-flight stream AND wait for it to fully unwind so
        //    its finally block (which mutates IsBusy / _currentAssistant)
        //    runs before we start our own setup.
        _streamCts?.Cancel();
        if (_currentStreamTask is { } prior)
        {
            try { await prior.ConfigureAwait(true); }
            catch { /* swallowed: SendAsync owns its own error reporting */ }
        }

        IsBusy = true;
        StatusText = "Loading session…";

        // Reset the dedupe set now that we're transitioning to a different
        // session context — RunIds aren't unique across sessions, and we
        // want any future run.completed on the resumed session to count
        // even if its id happened to collide with one we saw earlier.
        _seenRunIds.Clear();

        // 2. Fetch into LOCAL variables first so a fetch failure doesn't
        //    leave the UI showing a cleared transcript bound to a session
        //    that we couldn't actually load.
        SessionDetailEnvelope? detail;
        SessionMessageList? msgs;
        try
        {
            var detailTask = _api.GetSessionAsync(sessionId, CancellationToken.None);
            var msgsTask = _api.GetSessionMessagesAsync(sessionId, CancellationToken.None);
            await Task.WhenAll(detailTask, msgsTask).ConfigureAwait(true);
            detail = detailTask.Result;
            msgs = msgsTask.Result;
        }
        catch (Exception ex)
        {
            // Leave current transcript / SessionId untouched on failure so
            // the user's prior chat state is preserved and they can retry.
            StatusText = $"Could not load session: {ex.Message}";
            IsBusy = false;
            return;
        }

        // 3. Hydrate VMs from server messages. Only user/assistant turns are
        //    rendered: the message-list API doesn't carry enough info
        //    (original tool args) to faithfully reconstruct tool cards yet.
        //    Reasoning, where present, is preserved on the assistant VM.
        var hydrated = HydrateMessagesFromServer(msgs);

        // 4. Atomic swap on the UI thread (we're already on it — public
        //    method called from a UI handler).
        Messages.Clear();
        foreach (var vm in hydrated) Messages.Add(vm);
        SessionId = sessionId;
        SessionTitle = detail?.Session?.Title;

        // 5. Seed session totals from the persistent detail so the header
        //    chip reflects accumulated cost the moment Resume completes,
        //    not just whatever future runs add.
        if (detail?.Session is { } d)
        {
            var seeded = new UsageStats(
                InputTokens: d.InputTokens,
                OutputTokens: d.OutputTokens,
                CachedReadTokens: d.CacheReadTokens,
                CachedWriteTokens: d.CacheWriteTokens,
                ReasoningTokens: d.ReasoningTokens);
            SessionUsage = seeded.HasAny ? seeded : null;
            SessionCostUsd = d.EstimatedCostUsd;
        }
        else
        {
            SessionUsage = null;
            SessionCostUsd = null;
        }

        // 6. Seed model picker from the session's effective model. If the
        //    server didn't populate it (older session, gateway gap, etc.)
        //    show a synthetic "Model not reported" rather than silently
        //    falling back to global default — that would misrepresent
        //    what the session is actually running. Picker is locked
        //    anyway (SessionId is non-null), so the synthetic is just a
        //    display artifact.
        _suppressModelPickFlag = true;
        _userExplicitlyPicked = false;
        // Scrub any synthetics from a prior resume so they don't pile up.
        for (int i = AvailableModels.Count - 1; i >= 0; i--)
        {
            if (AvailableModels[i].IsSynthetic) AvailableModels.RemoveAt(i);
        }
        var sessionModel = detail?.Session?.Model;
        if (!string.IsNullOrWhiteSpace(sessionModel))
        {
            SelectedModelId = sessionModel;
            EnsureSelectedModelPresent();
        }
        else
        {
            // Add the "Model not reported" placeholder and select it (id null).
            AvailableModels.Insert(0, ModelOptionVm.Unreported());
            SelectedModelId = null;
        }
        _suppressModelPickFlag = false;

        IsBusy = false;
        StatusText = "Ready";
    }

    // -------------------------------------------------------------------
    // Model picker — load lifecycle
    // -------------------------------------------------------------------

    /// <summary>
    /// Loads <c>/v1/models</c> into <see cref="AvailableModels"/> if not
    /// already loaded / loading. Safe to call repeatedly from page
    /// navigation handlers: concurrent calls coalesce onto the same
    /// in-flight task. Never throws — fire-and-forget callers see a
    /// completed task even on failure (state goes to
    /// <see cref="ModelLoadState.Failed"/>).
    /// </summary>
    public Task EnsureModelsLoadedAsync()
    {
        if (ModelLoadStatus == ModelLoadState.Loaded) return Task.CompletedTask;
        if (_modelsLoadTask is { IsCompleted: false } inflight) return inflight;
        _modelsLoadTask = LoadModelsAsync();
        return _modelsLoadTask;
    }

    /// <summary>Force-restarts the model fetch even from the
    /// <see cref="ModelLoadState.Loaded"/> state. Bound to a refresh
    /// button next to the picker when the prior load failed.</summary>
    [RelayCommand(CanExecute = nameof(CanReloadModels))]
    private Task ReloadModelsAsync()
    {
        _modelsLoadTask = LoadModelsAsync();
        return _modelsLoadTask;
    }

    public bool CanReloadModels => ModelLoadStatus != ModelLoadState.Loading;

    private async Task LoadModelsAsync()
    {
        ModelLoadStatus = ModelLoadState.Loading;
        try
        {
            var list = await _api.GetModelsAsync(CancellationToken.None).ConfigureAwait(true);
            if (list?.Data is { } data)
            {
                // Preserve any synthetic entries (from a prior Resume) so
                // they survive the refresh — EnsureSelectedModelPresent
                // below will re-add the one for SelectedModelId if it
                // got wiped, but a "Model not reported" placeholder
                // (Id=null) needs to be re-added explicitly because the
                // lookup keys on Id.
                var hadUnreported = AvailableModels.Any(m => m.IsSynthetic && m.Id is null);

                AvailableModels.Clear();
                foreach (var m in data)
                {
                    AvailableModels.Add(ModelOptionVm.FromModel(m));
                }
                if (hadUnreported)
                {
                    AvailableModels.Insert(0, ModelOptionVm.Unreported());
                }
                EnsureSelectedModelPresent();
                ModelLoadStatus = ModelLoadState.Loaded;
            }
            else
            {
                ModelLoadStatus = ModelLoadState.Failed;
            }
        }
        catch
        {
            // Fire-and-forget contract: callers (OnNavigatedTo) discard
            // this task, so an unhandled throw here would surface as an
            // unobserved-task exception. Token-telemetry and model-list
            // failures are non-fatal — fall back to the global default.
            ModelLoadStatus = ModelLoadState.Failed;
        }
    }

    /// <summary>
    /// If <see cref="SelectedModelId"/> is non-null but not present in
    /// <see cref="AvailableModels"/>, inject a synthetic
    /// <see cref="ModelOptionVm.Unavailable"/> entry so the ComboBox /
    /// read-only chip can render the selected value. Called from every
    /// path that mutates either the list or the selection — including
    /// post-Resume seed, post-Create-session echo, and post-LoadModels
    /// refresh.
    /// </summary>
    private void EnsureSelectedModelPresent()
    {
        if (string.IsNullOrEmpty(SelectedModelId)) return;
        if (AvailableModels.Any(m => m.Id == SelectedModelId)) return;
        AvailableModels.Insert(0, ModelOptionVm.Unavailable(SelectedModelId));
    }

    /// <summary>Marshals one parsed SSE event onto the UI thread. The target
    /// <paramref name="msg"/> is captured explicitly per turn so the dispatched
    /// closure never has to read shared state (which can be reset by the time
    /// the closure actually runs).</summary>
    private void HandleEvent(MessageVm msg, ChatStreamEvent evt)
    {
        _dispatcher.TryEnqueue(() =>
        {
            switch (evt)
            {
                case AssistantDeltaEvent d:      OnAssistantDelta(msg, d); break;
                case ToolStartedEvent ts:        OnToolStarted(msg, ts); break;
                case ToolProgressEvent tp:       OnToolProgress(msg, tp); break;
                case ToolCompletedEvent tc:      OnToolCompleted(msg, tc); break;
                case AssistantCompletedEvent ac: OnAssistantCompleted(msg, ac); break;
                case RunCompletedEvent rc:       OnRunCompleted(msg, rc); break;
                case StreamErrorEvent err:       OnStreamError(msg, err); break;
                case UnknownStreamEvent _:       /* logged via raw fields if needed */ break;
            }
        });
    }

    private static void OnAssistantDelta(MessageVm msg, AssistantDeltaEvent d)
    {
        // Append-only into the buffer; the flush timer pulls Content from Buffer
        // on a 60ms cadence so we're not raising INPC per token.
        msg.Buffer.Append(d.Text);
    }

    private static void OnToolStarted(MessageVm msg, ToolStartedEvent ts)
    {
        msg.ToolCalls.Add(new ToolCallVm
        {
            CallId = ts.CallId,
            // Fall back to "(unknown)" only if every name field was missing —
            // this used to be "tool" but that hid the bug where the parser was
            // looking at the wrong field.
            Name = !string.IsNullOrEmpty(ts.Name) ? ts.Name! : "(unknown)",
            ArgumentsJson = PrettyJson(ts.ArgumentsJson),
            Preview = ts.Preview,
            IsRunning = true,
        });
    }

    private static void OnToolProgress(MessageVm msg, ToolProgressEvent tp)
    {
        // "_thinking" is reasoning, not a real tool — accumulate to the
        // reasoning channel so the message has a single collapsible
        // "Thinking" trail rather than a never-completing tool card.
        if (tp.Name == "_thinking" && !string.IsNullOrEmpty(tp.Message))
        {
            msg.ReasoningBuffer.Append(tp.Message);
            return;
        }
        var card = FindCard(msg, tp.CallId, tp.Name);
        if (card is not null) card.Progress = tp.Message;
    }

    private static void OnToolCompleted(MessageVm msg, ToolCompletedEvent tc)
    {
        var card = FindCard(msg, tc.CallId, tc.Name);
        if (card is null) return;

        // Some gateway paths don't emit an output payload at all (the
        // api_server direct path only sends tool/duration/error). Don't
        // blank a previously-set Output in that case.
        var newOutput = tc.OutputText ?? PrettyJson(tc.OutputJson);
        if (!string.IsNullOrEmpty(newOutput)) card.Output = newOutput;
        card.IsRunning = false;
        card.IsError = tc.IsError;
        card.DurationSeconds = tc.DurationSeconds;
        // Late-bind the name if tool.started didn't carry one but
        // tool.completed did — keeps the card from being stuck at
        // "(unknown)" forever.
        if (card.Name == "(unknown)" && !string.IsNullOrEmpty(tc.Name))
            card.Name = tc.Name!;
    }

    private static void OnAssistantCompleted(MessageVm msg, AssistantCompletedEvent ac)
    {
        // The terminal assistant.completed event carries the FULL final
        // content. Treat it as authoritative — overwrite our (possibly-lossy)
        // delta buffer so the bubble is never blank even if some delta events
        // were dropped/raced.
        if (string.IsNullOrEmpty(ac.Content)) return;
        msg.Buffer.Clear();
        msg.Buffer.Append(ac.Content);
        msg.Content = ac.Content!;
    }

    private void OnRunCompleted(MessageVm msg, RunCompletedEvent rc)
    {
        // Idempotency guard: if the gateway re-emits run.completed (network
        // retry, reconnect, etc.) we'd otherwise double-count tokens AND
        // could clobber Usage/Content with stale data from the re-emit.
        // Key on run_id when present; bail entirely. If run_id is missing
        // we fall through and process normally — losing dedupe is better
        // than dropping a real terminal event.
        if (!string.IsNullOrEmpty(rc.RunId) && !_seenRunIds.Add(rc.RunId))
        {
            return;
        }

        // Belt-and-braces: run.completed also carries the final assistant
        // content in its messages[] array. Use it as a last-resort fallback
        // if everything before us was empty.
        if (!string.IsNullOrEmpty(rc.FinalAssistantContent) && msg.Buffer.Length == 0)
        {
            msg.Buffer.Append(rc.FinalAssistantContent);
            msg.Content = rc.FinalAssistantContent!;
        }
        else if (!string.IsNullOrEmpty(rc.Output) && msg.Buffer.Length == 0)
        {
            msg.Buffer.Append(rc.Output);
            msg.Content = rc.Output!;
        }

        // Token accounting. Per-turn lands on the bubble; the session total
        // in the header rolls in whatever new numbers arrived (preserving
        // anything Resume seeded).
        if (rc.Usage is { HasAny: true } u)
        {
            msg.Usage = u;
            SessionUsage = SessionUsage is null ? u : SessionUsage.Add(u);
        }
    }

    private static void OnStreamError(MessageVm msg, StreamErrorEvent err)
    {
        msg.Buffer.Append("\n\n*Server error: ").Append(err.Message).Append('*');
        msg.State = MessageState.Failed;
    }

    /// <summary>
    /// Lazily creates a server session on first send and adopts its id/title.
    /// Reports failures inline on the assistant message and resets transient
    /// status so the caller can just <c>return</c> on a false result.
    /// </summary>
    /// <param name="assistant">The streaming-target message — used for
    /// inline error reporting if create fails.</param>
    /// <param name="modelOverride">Caller-snapshotted model id (or null to
    /// let the server pick its current default). Snapshotted in
    /// <c>SendCoreAsync</c> so picker edits during the in-flight create
    /// can't swap it.</param>
    /// <returns><see langword="false"/> if a session couldn't be obtained and
    /// the send should be aborted; <see langword="true"/> if SessionId is
    /// populated and streaming may proceed.</returns>
    private async Task<bool> EnsureSessionAsync(MessageVm assistant, string? modelOverride)
    {
        if (!string.IsNullOrEmpty(SessionId)) return true;

        // We don't pass a title — let the gateway auto-generate one from the
        // first message so we never collide with an existing session (titles
        // are server-enforced unique).
        try
        {
            var sess = await _api.CreateSessionAsync(null, modelOverride, CancellationToken.None);
            SessionId = sess?.Id;
            SessionTitle = sess?.Title;

            // Mirror the server's effective model back into the picker so
            // the read-only chip shows what's actually running (server may
            // have normalized our id or filled the default when we passed
            // null). Programmatic — don't flip _userExplicitlyPicked.
            if (!string.IsNullOrWhiteSpace(sess?.Model))
            {
                _suppressModelPickFlag = true;
                SelectedModelId = sess.Model;
                _suppressModelPickFlag = false;
                EnsureSelectedModelPresent();
            }
        }
        catch (Exception ex)
        {
            FinishAssistant(MessageState.Failed, $"\n\n*Could not create session: {ex.Message}*");
            IsBusy = false;
            StatusText = "Error";
            return false;
        }

        if (string.IsNullOrEmpty(SessionId))
        {
            FinishAssistant(MessageState.Failed, "\n\n*Server did not return a session id.*");
            IsBusy = false;
            StatusText = "Error";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a server message list into render-ready <see cref="MessageVm"/>s.
    /// Only user / assistant turns are produced — the message-list API doesn't
    /// carry enough information (original tool arguments) to faithfully
    /// reconstruct tool cards yet, and rows with other roles are skipped.
    /// Reasoning and best-effort per-turn token usage are preserved when
    /// the server persisted them.
    /// </summary>
    private static System.Collections.Generic.List<MessageVm> HydrateMessagesFromServer(SessionMessageList? msgs)
    {
        var hydrated = new System.Collections.Generic.List<MessageVm>();
        if (msgs?.Data is not { } rows) return hydrated;

        foreach (var m in rows.OrderBy(r => r.Timestamp ?? 0))
        {
            MessageRole role;
            if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                role = MessageRole.User;
            else if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                role = MessageRole.Assistant;
            else
                continue;

            var ts = m.Timestamp is double t
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000))
                : DateTimeOffset.Now;

            var vm = new MessageVm
            {
                Role = role,
                Content = m.Content ?? string.Empty,
                State = MessageState.Completed,
                Timestamp = ts,
            };
            // Preserve reasoning if the server persisted it (matches the
            // chat UX's existing reasoning plumbing — still hidden by
            // default per the earlier UX decision but data is intact).
            var reason = m.ReasoningContent ?? m.Reasoning;
            if (!string.IsNullOrEmpty(reason))
            {
                vm.ReasoningBuffer.Append(reason);
                vm.Reasoning = reason;
            }
            // Hydrate a best-effort per-turn Usage from the persisted
            // token_count. The server stores a single total per message
            // (no in/out split for individual turns), so we put user
            // counts on the input side and assistant counts on the
            // output side — close enough to make the footer informative.
            if (m.TokenCount is int tc && tc > 0)
            {
                vm.Usage = role == MessageRole.User
                    ? new UsageStats(InputTokens: tc)
                    : new UsageStats(OutputTokens: tc);
            }
            hydrated.Add(vm);
        }
        return hydrated;
    }

    private static ToolCallVm? FindCard(MessageVm msg, string? callId, string? name)
    {
        if (!string.IsNullOrEmpty(callId))
        {
            for (int i = msg.ToolCalls.Count - 1; i >= 0; i--)
                if (msg.ToolCalls[i].CallId == callId) return msg.ToolCalls[i];
        }
        if (!string.IsNullOrEmpty(name))
        {
            for (int i = msg.ToolCalls.Count - 1; i >= 0; i--)
                if (msg.ToolCalls[i].IsRunning && msg.ToolCalls[i].Name == name) return msg.ToolCalls[i];
        }
        return msg.ToolCalls.Count > 0 ? msg.ToolCalls[^1] : null;
    }

    private void StartFlushTimer()
    {
        if (_flushTimer is null)
        {
            _flushTimer = _dispatcher.CreateTimer();
            _flushTimer.Interval = TimeSpan.FromMilliseconds(60);
            _flushTimer.Tick += (s, e) =>
            {
                var cur = _currentAssistant;
                if (cur is not null) FlushBufferOnce(cur);
            };
        }
        _flushTimer.Start();
    }

    private void StopFlushTimer() => _flushTimer?.Stop();

    /// <summary>Copies the StringBuilder buffers into the bound .Content /
    /// .Reasoning properties (which raise INPC). Always overwrites — the prior
    /// length-compare optimization was a footgun because equal lengths don't
    /// imply equal content.</summary>
    private static void FlushBufferOnce(MessageVm msg)
    {
        // string != string already short-circuits on reference equality
        // before doing value compare, so no need to hand-roll ReferenceEquals.
        var buf = msg.Buffer.ToString();
        if (buf != msg.Content) msg.Content = buf;
        var rbuf = msg.ReasoningBuffer.ToString();
        if (rbuf != msg.Reasoning) msg.Reasoning = rbuf;
    }

    /// <summary>Final synchronous flush against an explicit message — called
    /// from SendAsync's UI-thread continuation so it can't race with the
    /// finally block clearing <see cref="_currentAssistant"/>.</summary>
    private static void FinalFlush(MessageVm msg) => FlushBufferOnce(msg);

    /// <summary>Yields long enough for any pending dispatcher work queued
    /// by the streaming worker to run before we proceed. Two yields back to
    /// back gives WinUI's pump a chance to drain both the queued delta
    /// callbacks and any continuations they spawned.</summary>
    private async Task DrainDispatcherAsync()
    {
        await Task.Yield();
        var tcs = new TaskCompletionSource();
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => tcs.TrySetResult());
        await tcs.Task;
    }

    private void FinishAssistant(MessageState state, string trailingMarkdown)
    {
        var msg = _currentAssistant;
        if (msg is null) return;
        msg.Buffer.Append(trailingMarkdown);
        msg.Content = msg.Buffer.ToString();
        msg.State = state;
    }

    private static string? PrettyJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return raw;
        }
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _flushTimer?.Stop();
    }
}
