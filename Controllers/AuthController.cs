using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    public class AuthController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfigStore _configStore;

        public AuthController(ITokenService tokenService, IHttpContextAccessor httpContextAccessor, IConfigStore configStore)
        {
            _tokenService = tokenService;
            _httpContextAccessor = httpContextAccessor;
            _configStore = configStore;
        }

        [HttpGet]
        public async Task<IActionResult> Login(AnimeService? animeService = null, string username = null, string password = null, bool anonymous = false)
        {
            if (anonymous)
            {
                var tokenData = new TokenData
                {
                    anime_service = animeService ?? AnimeService.Kitsu
                };

                _httpContextAccessor.HttpContext.Session.SetString("AccessToken", SerializeObject(tokenData));

                return RedirectToAction("Index", "Home");
            }
            else if (animeService == AnimeService.Anilist)
            {
                return Redirect($"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=code");
            }
            else
            {
                await _tokenService.GetAccessTokenByCredsAsync(username, password, true);

                return RedirectToAction("Index", "Home");
            }
        }

        [HttpGet]
        public async Task<IActionResult> Callback(string code)
        {
            HttpContext.Session.Remove("AccessToken");

            var tokenData = await _tokenService.GetAccessTokenByCodeAsync(code);

            return RedirectToAction("Index", "Home");
        }

        public async Task<IActionResult> Logout()
        {
            // Read the session token directly so we can identify the user without going
            // through GetAccessTokenAsync (which would refresh the upstream token and write
            // back to the row we're about to delete).
            var sessionStr = HttpContext.Session.GetString("AccessToken");
            if (!string.IsNullOrEmpty(sessionStr))
            {
                var tokenData = DeserializeObject<TokenData>(sessionStr);
                if (tokenData != null && !tokenData.anonymousUser)
                    await _configStore.DeleteByUserAsync(tokenData);
            }

            await _tokenService.RemoveCachedUser();

            return RedirectToAction("Index", "Home");
        }
    }
}