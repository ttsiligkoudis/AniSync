
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

        public string password { get; set; }

        public AnimeService anime_service { get; set; }

        public bool anonymousUser => (anime_service == AnimeService.Kitsu && string.IsNullOrEmpty(username)) || (anime_service == AnimeService.Anilist && string.IsNullOrEmpty(access_token));

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
                password = this.password,
                anime_service = this.anime_service
            };
        }
    }
}
