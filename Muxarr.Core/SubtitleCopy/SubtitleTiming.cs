namespace Muxarr.Core.SubtitleCopy;

/// <summary>
/// Works out how a donor's subtitles line up with the file they go into: a
/// stretch when the two run at different speeds (PAL 25 fps against film
/// 23.976 fps), then a fixed shift, found by lining up the start times of the
/// donor's captions with those of a subtitle the file already has.
/// </summary>
public static class SubtitleTiming
{
    public enum Quality
    {
        NotChecked,
        Good,
        Weak,
        NoMatch
    }

    /// <summary>A speed change as mkvmerge takes it: donor times x Numerator / Denominator.</summary>
    public sealed record Stretch(long Numerator, long Denominator, string Label)
    {
        public double Factor => (double)Numerator / Denominator;
        public bool IsNone => Numerator == Denominator;
    }

    public static readonly Stretch None = new(1, 1, "None");

    // 25 fps PAL plays 4.27 % faster than 24000/1001; 24 fps film 4.17 %.
    public static readonly Stretch PalToFilm = new(25025, 24000, "PAL 25 fps to 23.976 fps");
    public static readonly Stretch FilmToPal = new(24000, 25025, "23.976 fps to PAL 25 fps");
    public static readonly Stretch PalTo24 = new(25, 24, "PAL 25 fps to 24 fps");
    public static readonly Stretch From24ToPal = new(24, 25, "24 fps to PAL 25 fps");

    public static readonly IReadOnlyList<Stretch> Stretches = [None, PalToFilm, FilmToPal, PalTo24, From24ToPal];

    public static Stretch? FindStretch(long numerator, long denominator) =>
        Stretches.FirstOrDefault(s => s.Numerator == numerator && s.Denominator == denominator);

    /// <summary>
    /// The stretch the two lengths call for: none when they are within half a percent
    /// (a different logo or end card is a shift, not a speed change), else the known
    /// speed change within 0.3 % of their ratio, else none (a different cut).
    /// </summary>
    public static Stretch StretchFor(long targetMs, long donorMs)
    {
        if (targetMs <= 0 || donorMs <= 0)
        {
            return None;
        }

        var ratio = (double)targetMs / donorMs;
        if (Math.Abs(ratio - 1) < 0.005)
        {
            return None;
        }

        return Stretches.Skip(1)
            .Select(s => (Stretch: s, Distance: Math.Abs(ratio / s.Factor - 1)))
            .Where(x => x.Distance < 0.003)
            .OrderBy(x => x.Distance)
            .Select(x => x.Stretch)
            .FirstOrDefault() ?? None;
    }

    public sealed record Result(long OffsetMs, int Matched, int Compared, Quality Quality)
    {
        public double Share => Compared == 0 ? 0 : (double)Matched / Compared;
    }

    /// <summary>A donor caption counts as lined up when one of the file's starts within this.</summary>
    public const int MatchWindowMs = 250;

    private const int BinMs = 40;

    /// <summary>
    /// The shift that lines up the most donor caption starts (already stretched) with
    /// the file's own: every pair of starts within <paramref name="maxOffsetMs"/> votes
    /// for its difference, the busiest 40 ms bin wins, and the median of the
    /// differences in and next to it is the shift. Two tracks in the same language
    /// line up almost one for one; two languages are cut into captions differently,
    /// so fewer of their starts meet and the result says Weak sooner.
    /// </summary>
    public static Result FindOffset(IReadOnlyList<long> targetStarts, IReadOnlyList<long> donorStarts, long maxOffsetMs = 30_000)
    {
        var compared = Math.Min(targetStarts.Count, donorStarts.Count);
        if (compared == 0)
        {
            return new Result(0, 0, 0, Quality.NoMatch);
        }

        var target = targetStarts.OrderBy(x => x).ToArray();
        var votes = new Dictionary<long, int>();
        foreach (var b in donorStarts)
        {
            // Each donor caption votes once per bin, so a burst of the file's captions
            // near it does not outweigh the others.
            var voted = new HashSet<long>();
            var from = LowerBound(target, b - maxOffsetMs);
            for (var i = from; i < target.Length && target[i] <= b + maxOffsetMs; i++)
            {
                var bin = (long)Math.Round((target[i] - b) / (double)BinMs);
                if (voted.Add(bin))
                {
                    votes[bin] = votes.GetValueOrDefault(bin) + 1;
                }
            }
        }

        if (votes.Count == 0)
        {
            return new Result(0, 0, compared, Quality.NoMatch);
        }

        // Neighbouring bins split one true shift between them, so each bin is
        // scored with its two neighbours; ties go to the smaller shift.
        var best = votes.Keys
            .Select(k => (Bin: k, Score: votes[k] + votes.GetValueOrDefault(k - 1) + votes.GetValueOrDefault(k + 1)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Math.Abs(x.Bin))
            .First().Bin;

        var near = new List<long>();
        foreach (var b in donorStarts)
        {
            var nearest = Nearest(target, b + best * BinMs);
            var difference = nearest - b;
            if (Math.Abs(difference - best * BinMs) <= BinMs * 1.5)
            {
                near.Add(difference);
            }
        }

        var offset = near.Count > 0 ? Median(near) : best * BinMs;
        var matched = donorStarts.Count(b => Math.Abs(Nearest(target, b + offset) - (b + offset)) <= MatchWindowMs);
        return new Result(offset, matched, compared, Grade(matched, compared));
    }

    public static Quality Grade(int matched, int compared)
    {
        if (compared == 0)
        {
            return Quality.NoMatch;
        }

        var share = (double)matched / compared;
        if (share >= 0.5 && matched >= 8)
        {
            return Quality.Good;
        }

        return share >= 0.2 && matched >= 4 ? Quality.Weak : Quality.NoMatch;
    }

    /// <summary>
    /// How well a track already lines up, with no shift: how many of its caption starts
    /// have one of the file's within <see cref="MatchWindowMs"/>. Grades an Auto-sync result.
    /// </summary>
    public static Result Score(IReadOnlyList<long> targetStarts, IReadOnlyList<long> starts)
    {
        var compared = Math.Min(targetStarts.Count, starts.Count);
        if (compared == 0)
        {
            return new Result(0, 0, 0, Quality.NoMatch);
        }

        var target = targetStarts.OrderBy(x => x).ToArray();
        var matched = Math.Min(compared, starts.Count(s => Math.Abs(Nearest(target, s) - s) <= MatchWindowMs));
        return new Result(0, matched, compared, Grade(matched, compared));
    }

    /// <summary>Donor time to file time: stretched first, then shifted, as mkvmerge does.</summary>
    public static long Apply(long donorMs, Stretch stretch, long offsetMs) =>
        (long)Math.Round(donorMs * stretch.Factor) + offsetMs;

    private static int LowerBound(long[] sorted, long value)
    {
        var lo = 0;
        var hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static long Nearest(long[] sorted, long value)
    {
        var i = LowerBound(sorted, value);
        if (i == 0)
        {
            return sorted[0];
        }

        if (i == sorted.Length)
        {
            return sorted[^1];
        }

        return value - sorted[i - 1] <= sorted[i] - value ? sorted[i - 1] : sorted[i];
    }

    private static long Median(List<long> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
