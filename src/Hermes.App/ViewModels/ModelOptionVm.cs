using Hermes.ApiClient.Models;

namespace Hermes.App.ViewModels;

/// <summary>
/// Display-layer wrapper around a model option in the chat header
/// picker. Separated from <see cref="ModelInfo"/> (which is an API DTO)
/// because the picker needs concepts the API doesn't have:
///
/// <list type="bullet">
///   <item><description><b>Synthetic entries</b> — when a resumed session
///   was created with a model that no longer appears in
///   <c>/v1/models</c>, we still need to render <i>something</i> for the
///   selected value or the ComboBox shows blank. We add a synthetic entry
///   with <see cref="IsAvailable"/> = false and the original id as
///   <see cref="DisplayName"/>.</description></item>
///   <item><description><b>"Model not reported"</b> — a resumed session
///   whose <c>SessionDetail.Model</c> is null gets a synthetic entry
///   with <see cref="Id"/> = null and a human-readable label, because
///   silently showing the global default would misrepresent what the
///   session is actually using.</description></item>
/// </list>
/// </summary>
public sealed class ModelOptionVm
{
    /// <summary>The model id sent to the gateway. <see langword="null"/>
    /// only for the special "Model not reported" synthetic.</summary>
    public string? Id { get; init; }

    /// <summary>What the user sees in the ComboBox / read-only chip.
    /// For real entries this is the same as <see cref="Id"/>; for
    /// synthetics it's a human-friendly label.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>False for synthetic / placeholder entries. The picker
    /// could use this to grey out unavailable options if we ever want to
    /// surface "this model is no longer available" visually.</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>True for entries the picker invented (not from
    /// <c>/v1/models</c>). Useful so <see cref="ViewModels.ChatViewModel"/>
    /// can scrub these on NewChat without scrubbing real entries.</summary>
    public bool IsSynthetic { get; init; }

    public static ModelOptionVm FromModel(ModelInfo info) => new()
    {
        Id = info.Id,
        DisplayName = info.Id ?? "(unnamed)",
        IsAvailable = true,
        IsSynthetic = false,
    };

    /// <summary>Synthetic for "session was created with a model not in
    /// the current list". <paramref name="id"/> is the original id so
    /// SelectedValue binding still resolves.</summary>
    public static ModelOptionVm Unavailable(string id) => new()
    {
        Id = id,
        DisplayName = $"{id} (unavailable)",
        IsAvailable = false,
        IsSynthetic = true,
    };

    /// <summary>Synthetic for "resumed session didn't report which
    /// model it used". Id is null so nothing tries to send it.</summary>
    public static ModelOptionVm Unreported() => new()
    {
        Id = null,
        DisplayName = "Model not reported",
        IsAvailable = false,
        IsSynthetic = true,
    };
}
