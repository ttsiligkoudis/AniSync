using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    public class AuthController : Controller
    {
        private readonly ITokenService _tokenService;

        public AuthController(ITokenService tokenService)
        {
            _tokenService = tokenService;
        }

        [HttpGet]
        public async Task<IActionResult> Login(AnimeService? animeService = null, string username = null, string password = null)
        {
            if (animeService == AnimeService.Anilist)
            {
                return Redirect($"https://anilist.co/api/v2/oauth/authorize?client_id=20850&response_type=code");
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
            await _tokenService.RemoveCachedUser();

            return RedirectToAction("Index", "Home");
        }
    }
}