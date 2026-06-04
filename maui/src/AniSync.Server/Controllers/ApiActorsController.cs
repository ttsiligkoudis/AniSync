using AnimeList.Models;
using AnimeList.Models.Api;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnimeList.Controllers
{
    /// <summary>
    /// JSON actor-directory + filmography endpoints for the thin client's Discover
    /// "browse by actor" surface. The MVC <see cref="DiscoverController"/> renders
    /// the same data into views; this exposes it over /api/v1 so the Blazor heads
    /// (which don't host the MVC views) can reach it. Directory = TMDB popular /
    /// search people; filmography bridges a TMDB id to a Trakt slug then returns
    /// that person's movie + series credits. Public (no config required).
    /// </summary>
    [ApiController]
    [Route("api/v1")]
    [EnableRateLimiting("api")]
    [Tags("Discover")]
    [Produces("application/json")]
    public class ApiActorsController : ControllerBase
    {
        private readonly ITmdbService _tmdb;
        private readonly ITraktService _trakt;
        private readonly ILogger<ApiActorsController> _logger;

        public ApiActorsController(ITmdbService tmdb, ITraktService trakt, ILogger<ApiActorsController> logger)
        {
            _tmdb = tmdb;
            _trakt = trakt;
            _logger = logger;
        }

        /// <summary>One page of popular people (or a name search). Same source the
        /// /discover/actors directory renders.</summary>
        [HttpGet("actors")]
        [ProducesResponseType(typeof(ActorsListResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Actors(int page = 1, string search = null)
        {
            try
            {
                var (people, hasNext) = !string.IsNullOrWhiteSpace(search)
                    ? await _tmdb.SearchPeopleAsync(search.Trim(), page)
                    : await _tmdb.GetPopularPeopleAsync(page);
                return new JsonResult(new ActorsListResponse(people ?? [], hasNext));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Actors failed (page={Page}).", page);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>A person's filmography (movies + series), keyed by TMDB id. The
        /// directory is TMDB-sourced, so we bridge to a Trakt slug first; an empty
        /// result (no slug) returns empty credit lists rather than a 404.</summary>
        [HttpGet("actors/tmdb/{tmdbId:int}")]
        [ProducesResponseType(typeof(ActorCreditsResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ActorByTmdb(int tmdbId)
        {
            try
            {
                var slug = await _trakt.ResolveSlugByTmdbAsync(tmdbId);
                if (string.IsNullOrEmpty(slug))
                    return new JsonResult(new ActorCreditsResponse(tmdbId.ToString(), null, null, [], []));

                var (name, image, items) = await _trakt.GetPersonCreditsAsync(slug);
                var metas = items.ToVideoMetas();
                return new JsonResult(new ActorCreditsResponse(
                    slug, name, image,
                    metas.Where(m => m.type == MetaType.movie.ToString()).ToList(),
                    metas.Where(m => m.type == MetaType.series.ToString()).ToList()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API ActorByTmdb failed (tmdbId={TmdbId}).", tmdbId);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }
    }

    /// <summary>One page of the actor directory.</summary>
    public record ActorsListResponse(List<ActorSummary> Actors, bool HasNextPage);

    /// <summary>A person's filmography split into movies + series.</summary>
    public record ActorCreditsResponse(string Slug, string Name, string Image, List<Meta> Movies, List<Meta> Series);
}
