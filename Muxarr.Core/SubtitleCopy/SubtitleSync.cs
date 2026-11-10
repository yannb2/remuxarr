using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Muxarr.Core.SubtitleCopy;

/// <summary>
/// The pieces of Auto-sync that need no tools: the reference file alass lines a donor
/// up with (only the times of the library file's captions), and what alass says it did
/// (the parts it shifted, and a frame-rate change).
/// </summary>
public static partial class SubtitleSync
{
    /// <summary>One caption's time on screen, in ms.</summary>
    public readonly record struct Span(long StartMs, long EndMs);

    /// <summary>One packet of a subtitle track as ffprobe lists it.</summary>
    public readonly record struct Packet(long PtsMs, long? DurationMs, int Size);

    /// <summary>A part of the donor alass moved by one amount.</summary>
    public sealed record Block(int Count, long LengthMs, long OffsetMs);

    /// <summary>What alass did: the parts, in order, and the frame-rate change it made (null for none).</summary>
    public sealed record Report(IReadOnlyList<Block> Blocks, string? FrameRate)
    {
        public bool IsEmpty => Blocks.Count == 0;
    }

    // A PGS caption is two display sets: one that shows the picture and one that
    // clears it, a few dozen bytes. Same limit as the timing check.
    private const int PgsClearMaxBytes = 80;

    // A caption with no length of its own (DVD pictures often) is shown this long at most.
    private const long DefaultLengthMs = 2_000;

    /// <summary>
    /// The captions of a track from its packets: a text packet lasts its duration; a
    /// picture caption runs from the set that shows it to the set that clears it.
    /// </summary>
    public static List<Span> Spans(IEnumerable<Packet> packets, bool pictures)
    {
        var sorted = packets.OrderBy(p => p.PtsMs).ToList();
        var spans = new List<Span>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var packet = sorted[i];
            if (pictures && packet.Size <= PgsClearMaxBytes)
            {
                continue;
            }

            long end;
            if (packet.DurationMs is > 0 and var duration)
            {
                end = packet.PtsMs + duration;
            }
            else if (i + 1 < sorted.Count)
            {
                // The next set: a clear ends the caption; another picture replaces it.
                end = Math.Min(sorted[i + 1].PtsMs, packet.PtsMs + (pictures ? 10_000 : DefaultLengthMs));
            }
            else
            {
                end = packet.PtsMs + DefaultLengthMs;
            }

            if (end > packet.PtsMs && (spans.Count == 0 || spans[^1].StartMs != packet.PtsMs))
            {
                spans.Add(new Span(packet.PtsMs, end));
            }
        }

        return spans;
    }

    /// <summary>An SRT with only the times (every caption reads "x"): all alass needs from a reference.</summary>
    public static string TimingSrt(IEnumerable<Span> spans)
    {
        var text = new StringBuilder();
        var n = 1;
        foreach (var span in spans)
        {
            text.Append(n++).Append('\n')
                .Append(SrtTime(span.StartMs)).Append(" --> ").Append(SrtTime(span.EndMs)).Append('\n')
                .Append("x\n\n");
        }

        return text.ToString();
    }

    public static string SrtTime(long ms)
    {
        ms = Math.Max(0, ms);
        return string.Create(CultureInfo.InvariantCulture,
            $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    /// <summary>
    /// Reads alass's own account of what it did: one "shifted block of N subtitles with
    /// length L by D" line per part, and an "info: ... ratio is A/B" line when it changed
    /// the frame rate. Parts next to each other moved by the same amount (within 40 ms) are one.
    /// </summary>
    public static Report ParseAlass(string output)
    {
        var blocks = new List<Block>();
        foreach (Match match in BlockLine().Matches(output))
        {
            var block = new Block(int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture),
                Duration(match.Groups["length"].Value), Duration(match.Groups["by"].Value));
            if (blocks.Count > 0 && Math.Abs(blocks[^1].OffsetMs - block.OffsetMs) <= 40)
            {
                var last = blocks[^1];
                blocks[^1] = new Block(last.Count + block.Count, last.LengthMs + block.LengthMs,
                    (last.OffsetMs * last.Count + block.OffsetMs * block.Count) / (last.Count + block.Count));
                continue;
            }

            blocks.Add(block);
        }

        string? frameRate = null;
        if (RatioLine().Match(output) is { Success: true } ratio)
        {
            var reference = ratio.Groups["ref"].Value;
            var donor = ratio.Groups["inc"].Value;
            if (reference != donor)
            {
                frameRate = $"{donor} to {reference} fps";
            }
        }

        return new Report(blocks, frameRate);
    }

    /// <summary>
    /// The biggest move, when it is too big to be a cut: more than a quarter of the file
    /// and more than 10 minutes. Null when the result looks like a real edit.
    /// </summary>
    public static long? Implausible(Report report, long durationMs)
    {
        var limit = Math.Max(10 * 60_000L, durationMs / 4);
        var far = report.Blocks.Select(b => Math.Abs(b.OffsetMs)).DefaultIfEmpty(0).Max();
        return far > limit ? far : null;
    }

    /// <summary>What alass did, in a line: "Shifted +2.5 s." or "3 parts: +2.5 s (13 min), -4 s (13 min), +1.2 s (9 min)."</summary>
    public static string Describe(Report report)
    {
        var stretch = report.FrameRate != null ? $"Stretched {report.FrameRate}, then " : "";
        if (report.Blocks.Count == 0)
        {
            return stretch.Length > 0 ? $"Stretched {report.FrameRate}." : "alass gave no details.";
        }

        if (report.Blocks.Count == 1)
        {
            var only = report.Blocks[0];
            var words = Math.Abs(only.OffsetMs) < 40 ? "left in place" : $"shifted {Seconds(only.OffsetMs)}";
            return stretch.Length > 0 ? $"{stretch}{words}." : $"{char.ToUpperInvariant(words[0])}{words[1..]}.";
        }

        const int Shown = 4;
        var parts = report.Blocks.Take(Shown).Select(b => $"{Seconds(b.OffsetMs)} ({Length(b.LengthMs)})").ToList();
        var more = report.Blocks.Count > Shown ? $" and {report.Blocks.Count - Shown} more" : "";
        var cut = $"{report.Blocks.Count} parts moved apart (cuts or breaks): {string.Join(", ", parts)}{more}.";
        return stretch.Length > 0 ? $"{stretch}{char.ToLowerInvariant(cut[0])}{cut[1..]}" : cut;
    }

    private static string Seconds(long ms) =>
        (ms >= 0 ? "+" : "-") + (Math.Abs(ms) / 1000.0).ToString("0.0##", CultureInfo.InvariantCulture) + " s";

    private static string Length(long ms) =>
        ms >= 60_000 ? $"{Math.Round(ms / 60_000.0):0} min" : $"{Math.Max(1, ms / 1000)} s";

    // alass writes -0:00:02.656 / 0:13:10.814; hours may be one or more digits.
    private static long Duration(string text)
    {
        var negative = text.StartsWith('-');
        var parts = text.TrimStart('-').Split(':');
        double total = 0;
        foreach (var part in parts)
        {
            total = total * 60 + double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        var ms = (long)Math.Round(total * 1000);
        return negative ? -ms : ms;
    }

    [GeneratedRegex(@"shifted block of (?<count>\d+) subtitles with length (?<length>-?[\d:.]+) by (?<by>-?[\d:.]+)")]
    private static partial Regex BlockLine();

    [GeneratedRegex(@"ratio is (?<ref>[\d.]+)/(?<inc>[\d.]+)")]
    private static partial Regex RatioLine();
}
