using Arrkit.Subtitles;
using Muxarr.Core.Extensions;
using Muxarr.Core.Models;
using Muxarr.Data.Entities;
using Muxarr.Data.Extensions;

namespace Muxarr.Web.Components.Shared.TrackChecks;

/// <summary>
/// Listen to an audio track, preview a subtitle track, check it for SDH and commentary, or line picture
/// tracks up side by side, from a few slices of the file (a remux is too big to read whole for this).
/// Audio and pictures come back as data URLs through the circuit: the app's HTTP endpoints are API-key only.
/// <para>
/// The page owns one per file and hands it to the Tracks table, so the results survive the table being
/// filled again from the profile. Track numbers are the file's own, which the editor's rows keep.
/// </para>
/// </summary>
public sealed class TrackCheckSession : IDisposable
{
    /// <summary>How many captions the preview shows, picked evenly from the slices.</summary>
    public const int PreviewCount = 24;

    /// <summary>How many moments the side-by-side view shows, picked evenly.</summary>
    public const int SideBySideRows = 40;

    private readonly Dictionary<int, TrackCheckState> _states = new();
    private readonly HashSet<int> _compare = [];
    private readonly CancellationTokenSource _cancel = new();

    public TrackCheckSession(MediaFile file)
    {
        File = file;
        Unsure = TrackQualityScorer.CommentaryOrAudioDescription(file.Snapshot.Tracks);
    }

    /// <summary>The file as last loaded; a reload of the same file keeps the results.</summary>
    public MediaFile File { get; set; }

    /// <summary>
    /// Stereo tracks after the multichannel one in the same language: a commentary or an audio
    /// description, and the file does not say which.
    /// </summary>
    public HashSet<int> Unsure { get; }

    /// <summary>Raised when a check starts, moves on or ends, so the table can draw again.</summary>
    public event Func<Task>? Changed;

    public TrackCheckState StateOf(int index)
    {
        if (!_states.TryGetValue(index, out var state))
        {
            _states[index] = state = new TrackCheckState();
        }

        return state;
    }

    private long Duration(IMediaTrack track) => track.DurationMs > 0 ? track.DurationMs : File.Snapshot.DurationMs;

    private static string WorkFolder() => Path.Combine(Path.GetTempPath(), "muxarr-checks", Guid.NewGuid().ToString("N"));

    public async Task Listen(IMediaTrack track)
    {
        var state = StateOf(track.Index);
        state.Audio = null;
        await Run(state, "Reading 20 seconds of audio...", async folder =>
        {
            var duration = Duration(track);
            var at = duration > 120_000 ? TimeSpan.FromMilliseconds(duration / 3.0) : TimeSpan.Zero;
            var bytes = await TrackSampler.ListenAsync(File.Path, track.Index, duration, folder, at, _cancel.Token);
            state.AudioAt = at;
            state.Audio = "data:audio/mp4;base64," + Convert.ToBase64String(bytes);
        });
    }

    public async Task Preview(IMediaTrack track)
    {
        var state = StateOf(track.Index);
        state.ClosePreview();
        await Run(state, $"Reading {TrackSampler.Slices} slices of the track...", async folder =>
        {
            var sample = await TrackSampler.SubtitlesAsync(File.Path, track.Index, track.Codec, Duration(track), folder, _cancel.Token);
            state.Found = sample.Count;
            if (sample.Pictures.Count > 0)
            {
                var picked = Spread(sample.Pictures, PreviewCount);
                state.Pictures = picked.Select(c => (c.Start, "data:image/png;base64," + Convert.ToBase64String(Png.Rgba(c.Render())))).ToList();
                state.Shown = picked.Count;
            }
            else
            {
                state.Texts = Spread(sample.Texts, PreviewCount * 2);
                state.Shown = state.Texts.Count;
            }

            if (sample.Count == 0)
            {
                state.Error = "No captions in the slices read: the track may be empty or only forced captions.";
            }
        });
    }

    /// <summary>Reads the captions (Tesseract for pictures) in the track's language as the table has it now.</summary>
    public async Task Check(IMediaTrack track)
    {
        var state = StateOf(track.Index);
        state.Guess = null;
        await Run(state, $"Reading {TrackSampler.Slices} slices of the track...", async folder =>
        {
            var sample = await TrackSampler.SubtitlesAsync(File.Path, track.Index, track.Codec, Duration(track), folder, _cancel.Token);
            if (sample.Count == 0)
            {
                state.Error = "No captions in the slices read: nothing to check.";
                return;
            }

            if (sample.Pictures.Count > 0)
            {
                state.Status = $"Reading {Math.Min(sample.Pictures.Count, SubtitleClassifier.SampleSize)} captions with Tesseract...";
                await RaiseChanged();
            }

            var captions = await TrackSampler.ReadAsync(sample, track.LanguageCode, "tesseract", folder, _cancel.Token);
            // Ripparr also weighs "a second, longer track in the same language"; the file page
            // has no caption counts for the other tracks, so that clue is left out here.
            state.Guess = SubtitleClassifier.Guess(captions, false);
        });
    }

    private async Task Run(TrackCheckState state, string status, Func<string, Task> work)
    {
        state.Busy = true;
        state.Error = null;
        state.Status = status;
        await RaiseChanged();

        var folder = WorkFolder();
        try
        {
            await work(folder);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            state.Error = e.Message;
        }
        finally
        {
            state.Busy = false;
            state.Status = null;
            DeleteFolder(folder);
        }

        await RaiseChanged();
    }

    public bool IsCompared(int index) => _compare.Contains(index);

    public int CompareCount => _compare.Count;

    public Task ToggleCompare(int index, bool on) => Update(() =>
    {
        if (on)
        {
            _compare.Add(index);
        }
        else
        {
            _compare.Remove(index);
        }
    });

    public SideBySideView? Side { get; private set; }
    public bool SideBusy { get; private set; }
    public string? SideStatus { get; private set; }
    public string? SideError { get; private set; }

    public Task CloseSide() => Update(() => Side = null);

    /// <summary>
    /// A change made from one of the check components (closing a result, picking a track to compare);
    /// the table around them draws again too.
    /// </summary>
    public async Task Update(Action change)
    {
        change();
        await RaiseChanged();
    }

    /// <summary>
    /// Reads the same slices of each ticked track (same length, so the same slice starts) and lines
    /// their captions up by time with arrkit's CaptionAlignment, as Ripparr's compare page does.
    /// </summary>
    public async Task SideBySide()
    {
        var tracks = File.Snapshot.Tracks.Where(t => _compare.Contains(t.Index)).OrderBy(t => t.Index).ToList();
        SideBusy = true;
        SideError = null;
        Side = null;
        var folder = WorkFolder();
        try
        {
            var captions = new List<IReadOnlyList<PgsCaption>>();
            foreach (var track in tracks)
            {
                SideStatus = $"Reading track {track.Index} ({captions.Count + 1} of {tracks.Count})...";
                await RaiseChanged();
                var sample = await TrackSampler.SubtitlesAsync(File.Path, track.Index, track.Codec, File.Snapshot.DurationMs,
                    Path.Combine(folder, track.Index.ToString()), _cancel.Token);
                captions.Add(sample.Pictures);
            }

            var rows = CaptionAlignment.Align(captions);
            if (rows.Count == 0)
            {
                SideError = "No captions in the slices read.";
                return;
            }

            var picked = Spread(rows, SideBySideRows);
            Side = new SideBySideView(
                tracks.Select(t => $"#{t.Index} {t.LanguageName}{(t.IsForced ? " forced" : "")}{(t.IsHearingImpaired ? " SDH" : "")}").ToList(),
                picked.Select(r => (r.Start, r.Cells.Select(c => c == null ? null : "data:image/png;base64," + Convert.ToBase64String(Png.Rgba(c.Render()))).ToArray())).ToList(),
                rows.Count);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            SideError = e.Message;
        }
        finally
        {
            SideBusy = false;
            SideStatus = null;
            DeleteFolder(folder);
        }

        await RaiseChanged();
    }

    private async Task RaiseChanged()
    {
        if (_cancel.IsCancellationRequested || Changed is not { } changed)
        {
            return;
        }

        foreach (var handler in changed.GetInvocationList().Cast<Func<Task>>())
        {
            await handler();
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (IOException)
        {
            // Temp files: left for the OS to clean.
        }
    }

    public static List<T> Spread<T>(IReadOnlyList<T> items, int count)
    {
        if (items.Count <= count)
        {
            return items.ToList();
        }

        return Enumerable.Range(0, count).Select(i => items[(int)((long)i * items.Count / count)]).ToList();
    }

    /// <summary>The check reads the track differently from the flags the Tracks table has for it now.</summary>
    public static bool FlagsDiffer(IMediaTrack track, TrackGuess guess) =>
        guess.Sdh != track.IsHearingImpaired || guess.Commentary != track.IsCommentary;

    public static string Clock(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }

    public sealed record SideBySideView(List<string> Headers, List<(TimeSpan At, string?[] Cells)> Rows, int Total);
}

public sealed class TrackCheckState
{
    public bool Busy { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? Audio { get; set; }
    public TimeSpan AudioAt { get; set; }
    public TrackGuess? Guess { get; set; }
    public List<(TimeSpan At, string Image)>? Pictures { get; set; }
    public List<SrtCue>? Texts { get; set; }
    public int Found { get; set; }
    public int Shown { get; set; }

    public bool HasPreview => Pictures != null || Texts != null;

    /// <summary>Something to show under the track: progress, an error, the audio player, a check or a preview.</summary>
    public bool HasResult => (Busy && Status != null) || Error != null || Audio != null || Guess != null || HasPreview;

    public void ClosePreview()
    {
        Pictures = null;
        Texts = null;
    }

    public void Close()
    {
        Error = null;
        Audio = null;
        Guess = null;
        ClosePreview();
    }
}
