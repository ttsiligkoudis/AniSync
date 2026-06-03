using System.Text.Json.Serialization;

namespace AniSync.Client.Models;

// DTOs for the existing AniSync /api/v1 JSON surface (ApiController). Property
// names are camelCase on the wire (ASP.NET Core default), matched here via the
// JsonPropertyName attributes so we can keep idiomatic C# PascalCase.

/// <summary>One result row in the search typeahead (/api/v1/suggest).</summary>
public sealed class SuggestMatch
{
    [JsonPropertyName("id")]     public string Id { get; set; } = "";
    [JsonPropertyName("name")]   public string Name { get; set; } = "";
    [JsonPropertyName("poster")] public string? Poster { get; set; }
    /// <summary>"movie" / "series" / "anime" — drives the badge + link target.</summary>
    [JsonPropertyName("type")]   public string? Type { get; set; }
}

public sealed class SuggestResponse
{
    [JsonPropertyName("query")]   public string? Query { get; set; }
    [JsonPropertyName("matches")] public List<SuggestMatch> Matches { get; set; } = new();
}

/// <summary>Poster-card shape returned by the catalog/discover endpoints.</summary>
public sealed class MetaCard
{
    [JsonPropertyName("id")]     public string Id { get; set; } = "";
    [JsonPropertyName("name")]   public string Name { get; set; } = "";
    [JsonPropertyName("poster")] public string? Poster { get; set; }
    [JsonPropertyName("type")]   public string? Type { get; set; }
    [JsonPropertyName("year")]   public int? Year { get; set; }
    [JsonPropertyName("score")]  public double? Score { get; set; }
}
