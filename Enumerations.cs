namespace AnimeList
{
    public class Enumerations
    {
        public enum AnimeService
        {
            Kitsu,
            Anilist,
            MyAnimeList,
            // Trakt joins the provider enum as a first-class peer (it tracks anime,
            // movies, and series). The name "AnimeService" is now a slight misnomer —
            // it's really "tracker service" — but every identity / session / linked-token
            // / fan-out switch keys off this single enum, so riding it (rather than a
            // parallel concept) is what lets a user sign in with *only* Trakt and lets
            // Trakt be a fan-out target. Appended last so the existing persisted ints
            // (Kitsu=0, Anilist=1, MyAnimeList=2) are untouched.
            Trakt
        }

        public enum MetaType
        {
            anime,
            movie,
            series
        }

        public enum ListType
        {
            Current,
            Completed,
            Trending_Desc,
            Seasonal,
            Planning,
            Paused,
            Dropped,
            Repeating,
            Search,
            Airing,
        }

        public enum LinkCategory
        {
            none,
            follow,
            actor,
            director,
            writer,
            imdb,
            share,
            similar
        }
    }
}
