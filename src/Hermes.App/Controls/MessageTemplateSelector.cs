using Hermes.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hermes.App.Controls;

/// <summary>
/// Picks user vs assistant vs system bubble template based on
/// <see cref="MessageVm.Role"/>. Templates are wired up in
/// <c>ChatPage.xaml</c>.
/// </summary>
public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? SystemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is not MessageVm m) return AssistantTemplate;
        return m.Role switch
        {
            MessageRole.User => UserTemplate,
            MessageRole.System => SystemTemplate ?? AssistantTemplate,
            _ => AssistantTemplate,
        };
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
