using System;
using System.Collections.Concurrent;
using System.IO;

namespace MediaPlayerCore
{
    /// <summary>
    /// Reads the mux frontier (latest cluster timecode) from a growing Matroska file.
    /// </summary>
    public static class MatroskaMuxFrontier
    {
        private static readonly ConcurrentDictionary<string, (long DurationMs, long Tick)> Cache = new(StringComparer.OrdinalIgnoreCase);

        private const int CacheTtlMs = 1000;
        private const int MaxTailBytes = 512 * 1024;
        private const int MaxHeadBytes = 64 * 1024;

        private static readonly byte[] ClusterId = { 0x1F, 0x43, 0xB6, 0x75 };
        private static readonly byte[] TimecodeScaleId = { 0x2A, 0xD7, 0xB1 };
        private static readonly byte[] TimecodeId = { 0xE7 };

        public static long ProbeDurationMs(string? path, bool allowCached = true)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return 0;
            }

            if (allowCached
                && Cache.TryGetValue(path, out var freshCached)
                && Environment.TickCount64 - freshCached.Tick < CacheTtlMs)
            {
                return freshCached.DurationMs;
            }

            try
            {
                long durationMs = ProbeDurationMsCore(path);
                if (durationMs > 0)
                {
                    Cache[path] = (durationMs, Environment.TickCount64);
                    return durationMs;
                }

                if (allowCached && Cache.TryGetValue(path, out var staleCached))
                {
                    return staleCached.DurationMs;
                }

                return 0;
            }
            catch
            {
                if (allowCached && Cache.TryGetValue(path, out var staleCached))
                {
                    return staleCached.DurationMs;
                }

                return 0;
            }
        }

        private static long ProbeDurationMsCore(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long fileLen = stream.Length;
            if (fileLen < 4096)
            {
                return 0;
            }

            ulong timecodeScaleNs = ReadTimecodeScale(stream);
            if (timecodeScaleNs == 0)
            {
                timecodeScaleNs = 1_000_000;
            }

            int tailBytes = (int)Math.Min(fileLen, MaxTailBytes);
            byte[] tail = new byte[tailBytes];
            stream.Seek(fileLen - tailBytes, SeekOrigin.Begin);
            if (stream.Read(tail, 0, tailBytes) != tailBytes)
            {
                return 0;
            }

            int lastClusterIdx = FindLastClusterOffset(tail);
            if (lastClusterIdx < 0)
            {
                return 0;
            }

            int cursor = lastClusterIdx + ClusterId.Length;
            if (!TryReadVInt(tail, ref cursor, out ulong clusterDataSize, out _))
            {
                return 0;
            }

            int clusterBodyStart = cursor;
            int clusterEnd = (int)Math.Min((long)clusterBodyStart + (long)clusterDataSize, tail.Length);
            if (clusterEnd <= clusterBodyStart)
            {
                return 0;
            }

            ulong maxClusterTimecode = 0;
            for (int j = clusterBodyStart; j <= clusterEnd - TimecodeId.Length; j++)
            {
                if (!Matches(tail, j, TimecodeId))
                {
                    continue;
                }

                int tcCursor = j + TimecodeId.Length;
                if (!TryReadVInt(tail, ref tcCursor, out ulong timecode, out _))
                {
                    continue;
                }

                maxClusterTimecode = timecode;
                break;
            }

            if (maxClusterTimecode == 0)
            {
                return 0;
            }

            double seconds = maxClusterTimecode * (timecodeScaleNs / 1_000_000_000.0);
            if (seconds <= 0)
            {
                return 0;
            }

            return (long)(seconds * 1000.0);
        }

        private static int FindLastClusterOffset(byte[] tail)
        {
            for (int i = tail.Length - ClusterId.Length; i >= 0; i--)
            {
                if (Matches(tail, i, ClusterId))
                {
                    return i;
                }
            }

            return -1;
        }

        private static ulong ReadTimecodeScale(FileStream stream)
        {
            int headBytes = (int)Math.Min(stream.Length, MaxHeadBytes);
            byte[] head = new byte[headBytes];
            stream.Seek(0, SeekOrigin.Begin);
            if (stream.Read(head, 0, headBytes) != headBytes)
            {
                return 0;
            }

            for (int i = 0; i <= head.Length - TimecodeScaleId.Length - 2; i++)
            {
                if (!Matches(head, i, TimecodeScaleId))
                {
                    continue;
                }

                int cursor = i + TimecodeScaleId.Length;
                if (!TryReadVInt(head, ref cursor, out ulong value, out _))
                {
                    continue;
                }

                return value;
            }

            return 0;
        }

        private static bool Matches(byte[] buffer, int offset, byte[] pattern)
        {
            if (offset + pattern.Length > buffer.Length)
            {
                return false;
            }

            for (int i = 0; i < pattern.Length; i++)
            {
                if (buffer[offset + i] != pattern[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadVInt(byte[] buffer, ref int offset, out ulong value, out int encodedLength)
        {
            value = 0;
            encodedLength = 0;
            if (offset >= buffer.Length)
            {
                return false;
            }

            int first = buffer[offset];
            if (first == 0)
            {
                return false;
            }

            int mask = 0x80;
            encodedLength = 1;
            while ((first & mask) == 0 && encodedLength < 8)
            {
                mask >>= 1;
                encodedLength++;
            }

            if (offset + encodedLength > buffer.Length)
            {
                return false;
            }

            value = (ulong)(first & (mask - 1));
            for (int i = 1; i < encodedLength; i++)
            {
                value = (value << 8) | buffer[offset + i];
            }

            offset += encodedLength;
            return true;
        }
    }
}
