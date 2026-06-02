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
        };
        Messages.Add(assistant);
        _currentAssistant = assistant;

        IsBusy = true;
        StatusText = "Streaming…";

        // Lazily create a session on first send. Reusing it across turns means
        // the conversation shows up in the Sessions page and Hermes maintains
        // context across turns.
        if (string.IsNullOrEmpty(SessionId))
        {
            try
            {
                var sess = await _api.CreateSessionAsync(SessionTitle ?? "WinUI chat", CancellationToken.None);
                SessionId = sess?.Id;
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
                    HandleEvent(evt);
                }
            }, token);

            FinalFlush();
            if (assistant.State == MessageState.Streaming) assistant.State = MessageState.Completed;
            StatusText = "Ready";
        }
        catch (OperationCanceledException)
        {
            FinalFlush();
            if (assistant.State == MessageState.Streaming) assistant.State = MessageState.Stopped;
            StatusText = "Stopped";
        }
        catch (Exception ex)
        {
            FinalFlush();
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

    /// <summary>Marshals one parsed SSE event onto the UI thread.</summary>
    private void HandleEvent(ChatStreamEvent evt)
    {
        _dispatcher.TryEnqueue(() =>
        {
            var msg = _currentAssistant;
            if (msg is null) return;

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
                        Name = ts.Name ?? "tool",
                        ArgumentsJson = PrettyJson(ts.ArgumentsJson),
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
                        ccard.Output = tc.OutputText ?? PrettyJson(tc.OutputJson);
                        ccard.IsRunning = false;
                        ccard.IsError = tc.IsError;
                    }
                    break;

                case RunCompletedEvent rc:
                    if (!string.IsNullOrEmpty(rc.Output) && msg.Buffer.Length == 0)
                    {
                        msg.Buffer.Append(rc.Output);
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
            _flushTimer.Tick += (s, e) => FlushBufferOnce();
        }
        _flushTimer.Start();
    }

    private void StopFlushTimer() => _flushTimer?.Stop();

    private void FlushBufferOnce()
    {
        var msg = _currentAssistant;
        if (msg is null) return;
        var buf = msg.Buffer.ToString();
        if (buf.Length != msg.Content.Length) msg.Content = buf;
        var rbuf = msg.ReasoningBuffer.ToString();
        if (rbuf.Length != msg.Reasoning.Length) msg.Reasoning = rbuf;
    }

    private void FinalFlush()
    {
        _dispatcher.TryEnqueue(FlushBufferOnce);
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
