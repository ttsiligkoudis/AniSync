
namespace AnimeList.Models
{
    public class TokenData
    {
        public string access_token { get; set; }

        public string refresh_token { get; set; }

        public long? expires_in { get; set; }

        public DateTime? expiration_date { get; set; }

        public string user_id { get; set; }

        public string username { get; set; }

        public AnimeService anime_service { get; set; }

        // Kitsu uses a password grant (so we treat "no username" as anonymous); the OAuth-based
        // services (AniList, MyAnimeList) only have a username after the callback, so we key off
        // the access_token instead.
        public bool anonymousUser => anime_service == AnimeService.Kitsu
            ? string.IsNullOrEmpty(username)
            : string.IsNullOrEmpty(access_token);

        public TokenData Clone()
        {
            return new TokenData
            {
                access_token = this.access_token,
                refresh_token = this.refresh_token,
                expires_in = this.expires_in,
                expiration_date = this.expiration_date,
                user_id = this.user_id,
                username = this.username,
                anime_service = this.anime_service
            };
        }
    }
}
