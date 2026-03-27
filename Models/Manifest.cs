namespace AnimeList.Models
{
    public class Manifest
    {
        public string id { get; set; }
        public string version { get; set; }
        public string name { get; set; }
        public string description { get; set; }
        public string logo { get; set; }
        public List<string> resources { get; set; } = [];
        public List<string> types { get; set; } = [];
        public List<Catalog> catalogs { get; set; } = [];
        public List<string> idPrefixes { get; set; } = [];
        public List<Config> config { get; set; } = [];
        public BehaviorHints behaviorHints { get; set; }
    }

    public class BehaviorHints
    {
        public bool configurable { get; set; }
        public bool configurationRequired { get; set; }
    }

    public class Catalog
    {
        public string type { get; set; }
        public string id { get; set; }
        public string name { get; set; }
        public List<Extra> extra { get; set; } = [];
    }

    public class Config
    {
        public string key { get; set; }
        public string type { get; set; }
        public string title { get; set; }
    }

    public class Extra
    {
        public Extra(string name)
        {
            this.name = name;
        }
        public string name { get; set; }
        public List<string> options { get; set; }
        public bool isRequired { get; set; }
    }
}
