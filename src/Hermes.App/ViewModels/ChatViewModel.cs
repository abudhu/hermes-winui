using System;
using System.Collections.ObjectModel;
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
    private readonly DispatcherQueue _dispatcher;

    /// <summary>Independent CTS for the active stream — cancelled by Stop or NewChat.</summary>
    private CancellationTokenSource? _streamCts;

    /// <summary>
    /// Coalesces assistant token flushes. We append to <c>MessageVm.Buffer</c>
    /// on the streaming worker and trigger a UI-thread flush every ~60ms;
    /// raising INPC on every token here would peg the UI thread and is a
    /// well-known footgun.
    /// </summary>
    private DispatcherQueueTimer? _flushTimer;

    /// <summary>Reference to the currently-streaming assistant message (so flushes don't have to search).</summary>
    private MessageVm? _currentAssistant;

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

    public ChatViewModel(HermesApiClient api, HermesStreamingClient stream, DispatcherQueue dispatcher)
    {
        _api = api;
        _stream = stream;
        _dispatcher = dispatcher;
        Messages.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasMessages));
    }

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(Composer);

    partial void OnComposerChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
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

        // Lazily create a session on first send. We don't pass a title — let the
        // gateway auto-generate one from the first message so we never collide
        // with an existing session (titles are server-enforced unique).
        if (string.IsNullOrEmpty(SessionId))
        {
            try
            {
                var sess = await _api.CreateSessionAsync(null, CancellationToken.None);
                SessionId = sess?.Id;
                SessionTitle = sess?.Title;
            }
            catch (Exception ex)
            {
                FinishAssistant(MessageState.Failed, $"\n\n*Could not create session: {ex.Message}*");
                IsBusy = false;
                StatusText = "Error";
                return;
            }
            if (string.IsNullOrEmpty(SessionId))
            {
                FinishAssistant(MessageState.Failed, "\n\n*Server did not return a session id.*");
                IsBusy = false;
                StatusText = "Error";
                return;
            }
        }

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
        StatusText = "Ready";
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
                case AssistantDeltaEvent d:
                    msg.Buffer.Append(d.Text);
                    // Flush timer pulls .Content from .Buffer on a cadence.
                    break;

                case ToolStartedEvent ts:
                    msg.ToolCalls.Add(new ToolCallVm
                    {
                        CallId = ts.CallId,
                        // Fall back to "(unknown)" only if every name field was
                        // missing — this used to be "tool" but that hid the
                        // bug where the parser was looking at the wrong field.
                        Name = !string.IsNullOrEmpty(ts.Name) ? ts.Name! : "(unknown)",
                        ArgumentsJson = PrettyJson(ts.ArgumentsJson),
                        Preview = ts.Preview,
                        IsRunning = true,
                    });
                    break;

                case ToolProgressEvent tp:
                    // "_thinking" is reasoning, not a real tool — accumulate to the
                    // reasoning channel so the message has a single collapsible
                    // "Thinking" trail rather than a never-completing tool card.
                    if (tp.Name == "_thinking" && !string.IsNullOrEmpty(tp.Message))
                    {
                        msg.ReasoningBuffer.Append(tp.Message);
                        break;
                    }
                    var pcard = FindCard(msg, tp.CallId, tp.Name);
                    if (pcard is not null) pcard.Progress = tp.Message;
                    break;

                case ToolCompletedEvent tc:
                    var ccard = FindCard(msg, tc.CallId, tc.Name);
                    if (ccard is not null)
                    {
                        // Some gateway paths don't emit an output payload at all
                        // (the api_server direct path only sends tool/duration/error).
                        // Don't blank a previously-set Output in that case.
                        var newOutput = tc.OutputText ?? PrettyJson(tc.OutputJson);
                        if (!string.IsNullOrEmpty(newOutput)) ccard.Output = newOutput;
                        ccard.IsRunning = false;
                        ccard.IsError = tc.IsError;
                        ccard.DurationSeconds = tc.DurationSeconds;
                        // Late-bind the name if tool.started didn't carry one
                        // but tool.completed did — keeps the card from being
                        // stuck at "(unknown)" forever.
                        if (ccard.Name == "(unknown)" && !string.IsNullOrEmpty(tc.Name))
                            ccard.Name = tc.Name!;
                    }
                    break;

                case AssistantCompletedEvent ac:
                    // The terminal assistant.completed event carries the FULL
                    // final content. Treat it as authoritative — overwrite our
                    // (possibly-lossy) delta buffer so the bubble is never
                    // blank even if some delta events were dropped/raced.
                    if (!string.IsNullOrEmpty(ac.Content))
                    {
                        msg.Buffer.Clear();
                        msg.Buffer.Append(ac.Content);
                        msg.Content = ac.Content!;
                    }
                    break;

                case RunCompletedEvent rc:
                    // Belt-and-braces: run.completed also carries the final
                    // assistant content in its messages[] array. Use it as a
                    // last-resort fallback if everything before us was empty.
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
                    break;

                case StreamErrorEvent err:
                    msg.Buffer.Append("\n\n*Server error: ").Append(err.Message).Append('*');
                    msg.State = MessageState.Failed;
                    break;

                case UnknownStreamEvent _:
                    // No-op; logged via raw fields for diagnostics if needed.
                    break;
            }
        });
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
        var buf = msg.Buffer.ToString();
        if (!ReferenceEquals(buf, msg.Content) && buf != msg.Content) msg.Content = buf;
        var rbuf = msg.ReasoningBuffer.ToString();
        if (!ReferenceEquals(rbuf, msg.Reasoning) && rbuf != msg.Reasoning) msg.Reasoning = rbuf;
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
