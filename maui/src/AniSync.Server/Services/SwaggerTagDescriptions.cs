using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AnimeList.Services
{
    /// <summary>
    /// Adds the section descriptions Swagger UI shows under each tag heading.
    /// Tag names match the <c>[Tags(...)]</c> attributes on each controller.
    /// </summary>
    public class TagDescriptionsFilter : IDocumentFilter
    {
        public void Apply(OpenApiDocument doc, DocumentFilterContext context)
        {
            doc.Tags ??= [];
            doc.Tags.Add(new OpenApiTag
            {
                Name = "Public",
                Description = "Anonymous read-only endpoints. No config needed; cross-service " +
                              "anime mapping, unified anime detail, search, discovery, " +
                              "recommendations, streaming links, AniSkip and AnimeFillerList.",
            });
            doc.Tags.Add(new OpenApiTag
            {
                Name = "User-scoped",
                Description = "Endpoints scoped to one Stremio install (Config UID). Library " +
                              "export, single-entry CRUD, linked-account management, primary " +
                              "swap, and sync diff / streaming sync.",
            });
        }
    }
}
