using System.Collections.ObjectModel;

namespace Hermes.App.ViewModels;

public sealed class SkillGroupVm(string category)
{
    public string Category { get; } = category;
    public ObservableCollection<SkillCardVm> Skills { get; } = [];
}

public sealed class SkillCardVm(string name, string description)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
}
