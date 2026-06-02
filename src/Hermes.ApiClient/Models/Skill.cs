using System.Text.Json.Serialization;

namespace Hermes.ApiClient.Models;

public sealed record SkillList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] List<Skill> Data
);

public sealed record Skill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("category")] string? Category
);
