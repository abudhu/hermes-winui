using CommunityToolkit.Mvvm.ComponentModel;

namespace Hermes.App.ViewModels.Settings;

/// <summary>
/// One row in the MCP server editor's "Env vars" or "Headers" list.
/// Two strings, two TextBoxes, a delete button. Used identically for
/// stdio env vars (KEY=VALUE) and HTTP headers (Header-Name: Header-Value).
/// </summary>
public sealed partial class EditableKeyValueVm : ObservableObject
{
    [ObservableProperty]
    public partial string Key { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    public EditableKeyValueVm(string key, string value)
    {
        Key = key ?? "";
        Value = value ?? "";
    }
}
