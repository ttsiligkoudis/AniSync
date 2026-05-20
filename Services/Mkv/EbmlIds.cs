namespace AnimeList.Services.Mkv
{
    /// <summary>
    /// Matroska element IDs we care about. Same values as the
    /// Matroska spec — kept as ulong because EBML IDs are VINTs
    /// with the marker bit retained.
    /// </summary>
    internal static class EbmlIds
    {
        public const ulong EBML = 0x1A45_DFA3;
        public const ulong Segment = 0x1853_8067;

        public const ulong SeekHead = 0x114D_9B74;
        public const ulong Seek = 0x4DBB;
        public const ulong SeekId = 0x53AB;
        public const ulong SeekPosition = 0x53AC;

        public const ulong SegmentInfo = 0x1549_A966;
        public const ulong TimecodeScale = 0x2AD7_B1;

        public const ulong Tracks = 0x1654_AE6B;
        public const ulong TrackEntry = 0xAE;
        public const ulong TrackNumber = 0xD7;
        public const ulong TrackType = 0x83;
        public const ulong CodecId = 0x86;
        public const ulong CodecPrivate = 0x63A2;
        public const ulong Name = 0x536E;
        public const ulong Language = 0x22B5_9C;

        public const ulong Cues = 0x1C53_BB6B;
        public const ulong CuePoint = 0xBB;
        public const ulong CueTime = 0xB3;
        public const ulong CueTrackPositions = 0xB7;
        public const ulong CueTrack = 0xF7;
        public const ulong CueClusterPosition = 0xF1;

        public const ulong Cluster = 0x1F43_B675;
        public const ulong ClusterTimecode = 0xE7;
        public const ulong SimpleBlock = 0xA3;
        public const ulong BlockGroup = 0xA0;
        public const ulong Block = 0xA1;
        public const ulong BlockDuration = 0x9B;
    }

    internal struct EbmlElement
    {
        public ulong Id;
        /// <summary>ulong.MaxValue for EBML "unknown size" sentinel.</summary>
        public ulong Size;
        public int DataOffset;
        /// <summary>int.MaxValue when Size is the unknown-size sentinel.</summary>
        public int NextOffset;
    }
}
