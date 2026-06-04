using CommunityToolkit.Mvvm.ComponentModel;

namespace Hermes.App.ViewModels.Settings;

/// <summary>
/// One row in the MCP server editor's "Args" list. Each row is just a
/// string — the editor binds <c>Value</c> two-way to a TextBox, and a
/// per-row delete button removes the row from the parent collection.
/// </summary>
public sealed partial class EditableStringVm : ObservableObject
{
    [ObservableProperty]
    public partial string Value { get; set; }

    public EditableStringVm(string value)
    {
        Value = value ?? "";
    }
}
