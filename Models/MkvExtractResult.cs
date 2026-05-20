using System.Text.Json.Serialization;

namespace AnimeList.Models
{
    /// <summary>
    /// Result of an indexed MKV subtitle extraction. Wire shape mirrors
    /// the cf-mkv-extractor JSON worker so the client can consume either
    /// implementation without branching.
    /// </summary>
    public class MkvExtractResult
    {
        public List<MkvExtractTrack> Tracks { get; set; } = new();
        public bool Extracted { get; set; }

        /// <summary>"indexless" / "network" / "parse" / null on success.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Error { get; set; }

        public MkvExtractStats Stats { get; set; } = new();
    }

    public class MkvExtractStats
    {
        public long FileSize { get; set; }
        public int TrackCount { get; set; }
    }

    public class MkvExtractTrack
    {
        public int Number { get; set; }
        public string Language { get; set; } = "und";
        public string Name { get; set; } = "";

        // Exact casing matters — Watch.cshtml's JS reads `t.codecID`.
        [JsonPropertyName("codecID")]
        public string CodecID { get; set; } = "";

        public string Header { get; set; } = "";
        public List<MkvExtractCue> Cues { get; set; } = new();
    }

    public class MkvExtractCue
    {
        public double Time { get; set; }
        public double Duration { get; set; }
        public string Text { get; set; } = "";
    }
}
