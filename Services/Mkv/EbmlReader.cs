using System.Text;

namespace AnimeList.Services.Mkv
{
    /// <summary>
    /// EBML / Matroska low-level primitives. Same VINT semantics as
    /// cf-mkv-extractor/src/ebml.js so cross-checking against the
    /// JS implementation is straightforward.
    ///
    /// Uses ReadOnlySpan&lt;byte&gt; everywhere on the hot path so the
    /// element walker doesn't allocate per call — the per-cluster
    /// block loop in the parser hits these helpers tens of thousands
    /// of times per file.
    /// </summary>
    internal static class EbmlReader
    {
        public const int TrackTypeSubtitle = 0x11;

        /// <summary>
        /// Reads a variable-length integer starting at buf[off].
        /// keepMarker=true keeps the leading bit set (element IDs);
        /// false strips it (data sizes).
        /// </summary>
        public static (ulong value, int length) ReadVint(ReadOnlySpan<byte> buf, int off, bool keepMarker)
        {
            if (off >= buf.Length) throw new InvalidDataException("VINT: read past EOF");
            byte first = buf[off];
            if (first == 0) throw new InvalidDataException("VINT: invalid leading byte 0x00");

            int len = 1;
            byte mask = 0x80;
            while ((first & mask) == 0)
            {
                mask >>= 1;
                len++;
                if (len > 8) throw new InvalidDataException("VINT: length > 8 bytes");
            }
            if (off + len > buf.Length) throw new InvalidDataException("VINT: payload past EOF");

            ulong value = keepMarker ? first : (ulong)(byte)(first & ~mask);
            for (int i = 1; i < len; i++)
            {
                value = (value << 8) | buf[off + i];
            }
            return (value, len);
        }

        /// <summary>Reads an EBML element header at buf[off].</summary>
        public static EbmlElement ReadElement(ReadOnlySpan<byte> buf, int off)
        {
            var (id, idLen) = ReadVint(buf, off, true);
            var (sizeRaw, sizeLen) = ReadVint(buf, off + idLen, false);
            int dataOffset = off + idLen + sizeLen;

            // EBML "unknown size" sentinel = all 1s after the marker.
            // Promote to ulong.MaxValue so callers can recognise and bail.
            ulong maxForLen = (1UL << (7 * sizeLen)) - 1;
            ulong size = sizeRaw == maxForLen ? ulong.MaxValue : sizeRaw;

            int nextOffset;
            if (size == ulong.MaxValue)
            {
                nextOffset = int.MaxValue;
            }
            else
            {
                long maybeNext = (long)dataOffset + (long)size;
                nextOffset = maybeNext > int.MaxValue ? int.MaxValue : (int)maybeNext;
            }

            return new EbmlElement
            {
                Id = id,
                Size = size,
                DataOffset = dataOffset,
                NextOffset = nextOffset,
            };
        }

        public static ulong ReadUInt(ReadOnlySpan<byte> buf, int off, ulong size)
        {
            ulong v = 0;
            int end = Math.Min(off + (int)size, buf.Length);
            for (int i = off; i < end; i++)
            {
                v = (v << 8) | buf[i];
            }
            return v;
        }

        /// <summary>Signed 16-bit big-endian (SimpleBlock timecode field).</summary>
        public static int ReadI16Be(ReadOnlySpan<byte> buf, int off)
        {
            if (off + 2 > buf.Length) return 0;
            return (short)((buf[off] << 8) | buf[off + 1]);
        }

        public static string ReadString(ReadOnlySpan<byte> buf, int off, ulong size)
        {
            int end = Math.Min(off + (int)size, buf.Length);
            var slice = buf.Slice(off, end - off);
            int nullIdx = slice.IndexOf((byte)0);
            if (nullIdx >= 0) slice = slice.Slice(0, nullIdx);
            return Encoding.UTF8.GetString(slice);
        }

        public static byte[] ReadBytes(ReadOnlySpan<byte> buf, int off, ulong size)
        {
            int end = Math.Min(off + (int)size, buf.Length);
            return buf.Slice(off, end - off).ToArray();
        }

        /// <summary>
        /// Walks a contiguous EBML buffer scanning for a direct-child
        /// element with the requested ID. Returns null if not found
        /// within the bounds.
        /// </summary>
        public static EbmlElement? FindChild(ReadOnlySpan<byte> buf, int start, int end, ulong targetId)
        {
            int off = start;
            int limit = Math.Min(end, buf.Length);
            while (off < limit)
            {
                EbmlElement el;
                try { el = ReadElement(buf, off); }
                catch { return null; }

                if (el.Id == targetId) return el;
                if (el.Size == ulong.MaxValue) return null;
                off = el.NextOffset;
                if (off == int.MaxValue) return null;
            }
            return null;
        }
    }
}
