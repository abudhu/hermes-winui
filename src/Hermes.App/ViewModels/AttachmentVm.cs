using CommunityToolkit.Mvvm.ComponentModel;
using Hermes.ApiClient;

namespace Hermes.App.ViewModels;

/// <summary>
/// View-model for one chip in the composer's attached-files strip. Wraps
/// the underlying <see cref="AttachmentReadResult"/> so the UI can render
/// success and error states without re-reading the file or re-running the
/// binary-sniff. <see cref="Result"/> is the source of truth fed into
/// <c>AttachmentFormatter.BuildMessage</c> on send.
/// </summary>
public sealed partial class AttachmentVm : ObservableObject
{
    public AttachmentReadResult Result { get; }

    public AttachmentVm(AttachmentReadResult result)
    {
        Result = result;
    }

    public string FileName => Result.FileName;
    public string Path => Result.Path;
    public string SizeLabel => AttachmentFormatter.FormatSize(Result.Size);
    public bool IsSuccess => Result.IsSuccess;
    public bool IsImage => Result.IsImage;
    public bool IsFile => Result.IsFile;
    public bool HasError => !Result.IsSuccess;
    public string? ErrorMessage => Result.ErrorMessage;

    /// <summary>One-line tooltip the chip surfaces on hover. Always
    /// includes the full path; appends the error message when present
    /// so the user knows what's special about this chip without
    /// expanding anything.</summary>
    public string Tooltip => HasError ? $"{Path}\n{ErrorMessage}" : Path;
}
