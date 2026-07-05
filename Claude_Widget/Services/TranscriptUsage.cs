using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Claude_Widget.Services
{
    /// <summary>Token counts and a rough dollar estimate accumulated from a session transcript.</summary>
    public sealed class UsageTotals
    {
        public long TokensIn { get; set; }   // input + cache-create + cache-read
        public long TokensOut { get; set; }  // output
        public double CostUsd { get; set; }
    }

    /// <summary>
    /// Reads a Claude Code session transcript (JSONL) and sums token usage into a rough
    /// cost estimate. Claude Code appends one JSON object per line; assistant turns carry a
    /// <c>message.usage</c> block. Reads are <b>incremental</b> — the byte offset per file is
    /// cached so each poll only parses newly-appended lines, which keeps large transcripts cheap.
    ///
    /// The dollar figure is a deliberate estimate (blended per-model rates, cache-aware) and is
    /// always surfaced to the user as "예상" — not a billing source of truth.
    /// </summary>
    public sealed class TranscriptUsageReader
    {
        // Approximate USD per 1M tokens (Sonnet-class blend). Cache reads are much cheaper.
        private const double RateInputPerM = 3.0;
        private const double RateCacheCreatePerM = 3.75;
        private const double RateCacheReadPerM = 0.30;
        private const double RateOutputPerM = 15.0;

        private sealed class State
        {
            public long Offset;
            public long InputTokens;
            public long CacheCreateTokens;
            public long CacheReadTokens;
            public long OutputTokens;
        }

        private readonly Dictionary<string, State> _byPath = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns cumulative usage for a transcript, parsing only bytes appended since the
        /// last call. Returns null when the path is empty or cannot be read.
        /// </summary>
        public UsageTotals? Read(string? transcriptPath)
        {
            if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
                return null;

            if (!_byPath.TryGetValue(transcriptPath, out var st))
            {
                st = new State();
                _byPath[transcriptPath] = st;
            }

            try
            {
                using var fs = new FileStream(transcriptPath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);

                // File truncated/rotated (e.g. new session reused the path): start over.
                if (fs.Length < st.Offset)
                {
                    st.Offset = 0;
                    st.InputTokens = st.CacheCreateTokens = st.CacheReadTokens = st.OutputTokens = 0;
                }

                if (fs.Length <= st.Offset)
                    return ToTotals(st); // nothing new

                // Read appended bytes (capped per poll; the rest follows next time). Splitting on
                // '\n' is safe in UTF-8 — 0x0A never occurs inside a multibyte sequence — so we
                // only need to stop at the last complete newline and reparse the tail later.
                fs.Seek(st.Offset, SeekOrigin.Begin);
                int toRead = (int)Math.Min(fs.Length - st.Offset, 4 * 1024 * 1024);
                byte[] buf = new byte[toRead];
                int read = fs.Read(buf, 0, toRead);
                if (read <= 0)
                    return ToTotals(st);

                int lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
                if (lastNl < 0)
                    return ToTotals(st); // no complete line yet

                string text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
                st.Offset += lastNl + 1;

                foreach (var line in text.Split('\n'))
                    AccumulateLine(st, line);

                return ToTotals(st);
            }
            catch
            {
                // Transcript may be mid-write / locked; return what we have so far.
                return ToTotals(st);
            }
        }

        private static void AccumulateLine(State st, string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message", out var msg)
                    || !msg.TryGetProperty("usage", out var usage))
                    return;

                st.InputTokens += GetLong(usage, "input_tokens");
                st.CacheCreateTokens += GetLong(usage, "cache_creation_input_tokens");
                st.CacheReadTokens += GetLong(usage, "cache_read_input_tokens");
                st.OutputTokens += GetLong(usage, "output_tokens");
            }
            catch
            {
                // Skip malformed lines.
            }
        }

        private static long GetLong(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64()
                : 0;

        private static UsageTotals ToTotals(State st)
        {
            double cost =
                st.InputTokens / 1_000_000.0 * RateInputPerM +
                st.CacheCreateTokens / 1_000_000.0 * RateCacheCreatePerM +
                st.CacheReadTokens / 1_000_000.0 * RateCacheReadPerM +
                st.OutputTokens / 1_000_000.0 * RateOutputPerM;

            return new UsageTotals
            {
                TokensIn = st.InputTokens + st.CacheCreateTokens + st.CacheReadTokens,
                TokensOut = st.OutputTokens,
                CostUsd = cost,
            };
        }

        /// <summary>Forgets cached offsets for transcripts no longer active (called on prune).</summary>
        public void Forget(IEnumerable<string> keepPaths)
        {
            var keep = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
            var drop = new List<string>();
            foreach (var key in _byPath.Keys)
                if (!keep.Contains(key))
                    drop.Add(key);
            foreach (var key in drop)
                _byPath.Remove(key);
        }
    }
}
