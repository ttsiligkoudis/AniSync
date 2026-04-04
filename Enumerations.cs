namespace AnimeList
{
    public class Enumerations
    {
        public enum AnimeService
        {
            Kitsu,
            Anilist
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
            Seasonal
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
