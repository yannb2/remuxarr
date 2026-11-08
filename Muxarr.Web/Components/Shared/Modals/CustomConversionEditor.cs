using Muxarr.Core.Extensions;
using Muxarr.Core.Language;
using Muxarr.Core.Models;
using Muxarr.Core.MkvToolNix;
using Muxarr.Data.Entities;
using Muxarr.Data.Extensions;

namespace Muxarr.Web.Components.Shared.Modals;

/// <summary>
/// What the custom conversion modal is actually doing: an editable copy of the
/// file's tracks, which of them to keep, and the order they go out in. It lives
/// outside the component so the rules (profile overlay, reordering, what reaches
/// the plan) can be exercised without a renderer.
/// </summary>
public class CustomConversionEditor
{
    private readonly MediaFile _file;
    private readonly List<TrackSnapshot> _tracks;
    private readonly HashSet<int> _selected = [];

    // Names follow whichever profile last wrote them: the one applied here, or
    // the file's own. Deliberately not the profile the page has selected, which
    // is a preview and may never touch this file.
    private Profile? _appliedProfile;

    public CustomConversionEditor(MediaFile file)
    {
        _file = file;
        _tracks = file.Snapshot.Tracks.Select(t => t.ToSnapshot()).ToList();
        _tracks.AttachOriginalNames(file.OriginalTrackNames, file.Snapshot.Tracks);
        FileTitle = file.Snapshot.Title ?? "";
        Chapters = file.Snapshot.Chapters
            .Select(c => new ChapterEdit(c.StartMs, c.Title ?? "", c.Title ?? "")
            {
                Original = file.OriginalTrackNames?.OriginalChapterTitle(c.StartMs)
            })
            .ToList();
        Tags = file.Snapshot.GlobalTags.Select(t => new TagEdit(t.Name, t.Value)).ToList();

        // Start from raw file state: every track kept, no mutations applied.
        // ApplyProfile is how the user opts into a profile's rules.
        foreach (var track in _tracks)
        {
            _selected.Add(track.Index);
        }

        var present = _tracks
            .Where(t => t.Type != MediaTrackType.Video)
            .Select(t => t.LanguageName)
            .ToHashSet();

        Languages = IsoLanguage.Languages
            .OrderByDescending(l => present.Contains(l.Name))
            .ThenBy(l => l.Name)
            .ToList();
    }

    public IReadOnlyList<TrackSnapshot> Tracks => _tracks;

    /// <summary>
    /// The container title to write. Starts as the file's own; empty clears it.
    /// Only a change from the current title reaches the plan.
    /// </summary>
    public string FileTitle { get; set; } = "";

    public string CurrentFileTitle => _file.Snapshot.Title ?? "";

    /// <summary>One chapter's title as the file has it and as the user wants it.</summary>
    public sealed class ChapterEdit(long startMs, string current, string title)
    {
        public long StartMs { get; } = startMs;
        public string Current { get; } = current;
        public string Title { get; set; } = title;
        public bool IsChanged => !string.IsNullOrWhiteSpace(Title) && Title.Trim() != Current;

        /// <summary>The title from the track name backup, when the file arrived with one.</summary>
        public string? Original { get; init; }
    }

    /// <summary>True when the backup holds a chapter title that differs from the file's.</summary>
    public bool HasOriginalChapterTitles =>
        Chapters.Any(c => !string.IsNullOrEmpty(c.Original) && c.Original != c.Current);

    /// <summary>Puts back the titles the file arrived with, where the backup has one.</summary>
    public void UseOriginalChapterTitles()
    {
        foreach (var chapter in Chapters.Where(c => !string.IsNullOrEmpty(c.Original)))
        {
            chapter.Title = chapter.Original!;
        }
    }

    /// <summary>The file's chapters with editable titles. Only Matroska files can have them renamed.</summary>
    public List<ChapterEdit> Chapters { get; }

    public bool CanRenameChapters =>
        Chapters.Count > 0 && _file.Snapshot.ContainerType.ToContainerFamily() == ContainerFamily.Matroska;

    /// <summary>Titles every chapter "Chapter 1", "Chapter 2", ... with the given word.</summary>
    public void NumberChapters(string word = "Chapter")
    {
        var width = Chapters.Count >= 10 ? 2 : 1;
        for (var i = 0; i < Chapters.Count; i++)
        {
            Chapters[i].Title = $"{word} {(i + 1).ToString().PadLeft(width, '0')}";
        }
    }

    public void ResetChapters()
    {
        foreach (var chapter in Chapters)
        {
            chapter.Title = chapter.Current;
        }
    }

    /// <summary>One global tag: as the file has it, as the user wants it, or one the user added.</summary>
    public sealed class TagEdit(string name, string? current)
    {
        public string Name { get; set; } = name;
        public string? Current { get; } = current;
        public string Value { get; set; } = current ?? "";
        public bool Removed { get; set; }
        public bool IsNew => Current == null;
        public bool IsChanged => Removed || IsNew || Value != Current;
    }

    /// <summary>The file's global tags, editable. Only Matroska files can have them changed.</summary>
    public List<TagEdit> Tags { get; private set; }

    public bool CanEditTags => _file.Snapshot.ContainerType.ToContainerFamily() == ContainerFamily.Matroska;

    public bool TagsChanged => Tags.Any(t => t.IsChanged && !(t.IsNew && t.Removed));

    public void AddTag()
    {
        Tags.Add(new TagEdit("", null));
    }

    public void ResetTags()
    {
        Tags = _file.Snapshot.GlobalTags.Select(t => new TagEdit(t.Name, t.Value)).ToList();
    }

    /// <summary>
    /// The global tag a release label (Criterion, Arrow...) is written to, as in Ripparr: the standard
    /// Matroska LABEL, "the imprint on the disc".
    /// </summary>
    public const string ReleaseLabelTag = "LABEL";

    /// <summary>Where the release label was kept before 1.1.2 (and Ripparr 0.4.33): read, and moved to LABEL on an edit.</summary>
    public const string OldReleaseLabelTag = "DISTRIBUTED_BY";

    /// <summary>
    /// The release label, kept in the LABEL global tag: typing fills the tag (adding it when the
    /// file has none), and an empty box removes it. A file tagged before 1.1.2 shows its
    /// DISTRIBUTED_BY value; any edit writes LABEL and removes DISTRIBUTED_BY.
    /// </summary>
    public string ReleaseLabel
    {
        get => (Tags.FirstOrDefault(t => !t.Removed && t.Name.Trim() == ReleaseLabelTag)
                ?? Tags.FirstOrDefault(t => !t.Removed && t.Name.Trim() == OldReleaseLabelTag))?.Value ?? "";
        set
        {
            var text = value.Trim();
            foreach (var old in Tags.Where(t => t.Name.Trim() == OldReleaseLabelTag).ToList())
            {
                if (old.IsNew)
                {
                    Tags.Remove(old);
                }
                else
                {
                    old.Removed = true;
                }
            }

            var tag = Tags.FirstOrDefault(t => t.Name.Trim() == ReleaseLabelTag);
            if (tag == null)
            {
                if (text.Length > 0)
                {
                    Tags.Add(new TagEdit(ReleaseLabelTag, null) { Value = text });
                }

                return;
            }

            if (text.Length == 0)
            {
                if (tag.IsNew)
                {
                    Tags.Remove(tag);
                }
                else
                {
                    tag.Removed = true;
                }

                return;
            }

            tag.Removed = false;
            tag.Value = text;
        }
    }

    /// <summary>The tags in the track name backup, when they differ from the file's.</summary>
    public IReadOnlyList<FileTag>? BackedUpTags =>
        _file.OriginalTrackNames?.GlobalTags is { Count: > 0 } backup
        && !FileTag.SameSet(backup, _file.Snapshot.GlobalTags)
            ? backup
            : null;

    /// <summary>Makes the wanted tags the backed-up set: theirs back, the rest removed.</summary>
    public void UseBackedUpTags()
    {
        if (BackedUpTags is not { } backup)
        {
            return;
        }

        ResetTags();
        foreach (var tag in Tags)
        {
            var saved = backup.FirstOrDefault(b => b.Name == tag.Name);
            tag.Removed = saved == null;
            tag.Value = saved?.Value ?? tag.Value;
        }

        foreach (var saved in backup.Where(b => Tags.All(t => t.Name != b.Name)))
        {
            Tags.Add(new TagEdit(saved.Name, null) { Value = saved.Value });
        }
    }

    /// <summary>Why the tags cannot be written as they are, or null when they can.</summary>
    public string? TagProblem
    {
        get
        {
            var kept = Tags.Where(t => !t.Removed).ToList();
            if (kept.Any(t => string.IsNullOrWhiteSpace(t.Name)))
            {
                return "Every tag needs a name.";
            }

            if (kept.FirstOrDefault(t => FileTag.IsContainerField(t.Name.Trim())) is { } reserved)
            {
                return $"\"{reserved.Name.Trim()}\" is a container field, not a tag.";
            }

            var duplicate = kept.GroupBy(t => t.Name.Trim()).FirstOrDefault(g => g.Count() > 1);
            return duplicate == null ? null : $"\"{duplicate.Key}\" is used twice.";
        }
    }

    public IReadOnlyList<IsoLanguage> Languages { get; }

    /// <summary>The name the track arrived with, when the backup has one that differs from the name now.</summary>
    public string? OriginalNameOf(TrackSnapshot track)
    {
        return string.IsNullOrWhiteSpace(track.OriginalName) || track.OriginalName == track.Name
            ? null
            : track.OriginalName;
    }

    /// <summary>Puts the original name back as the track's name, to edit from there.</summary>
    public void UseOriginalName(TrackSnapshot track)
    {
        if (OriginalNameOf(track) is { } original)
        {
            track.Name = original;
        }
    }

    /// <summary>
    /// The track's {description} under the profile's names, for the Description box. Null when the
    /// profile does not standardize this track type, or for video.
    /// </summary>
    public string? DescriptionOf(TrackSnapshot track)
    {
        return track.Type == MediaTrackType.Video ? null : track.DescriptionOf(_appliedProfile ?? _file.Profile);
    }

    /// <summary>Writes the profile's name for the track with this text as {description} ("=..." is the whole name).</summary>
    public void Describe(TrackSnapshot track, string? text)
    {
        track.DescribeTrack(_file, _appliedProfile ?? _file.Profile, _tracks, text);
    }

    public bool IsSelected(TrackSnapshot track)
    {
        return _selected.Contains(track.Index);
    }

    /// <summary>A conversion with no audio would produce an unplayable file.</summary>
    public bool HasAudio => _tracks.Any(t => t.Type == MediaTrackType.Audio && _selected.Contains(t.Index));

    public void ToggleTrack(TrackSnapshot track)
    {
        if (track.Type == MediaTrackType.Video)
        {
            return;
        }

        if (!_selected.Add(track.Index))
        {
            _selected.Remove(track.Index);
        }
    }

    /// <summary>
    /// Overlays a profile's target on the editable tracks: its selection, and any
    /// field it has an opinion on. Null fields mean "no opinion" and leave the
    /// track alone; "" on a name is an explicit clear.
    /// </summary>
    public void ApplyProfile(Profile profile)
    {
        var target = _file.BuildTargetFromProfile(profile);
        _appliedProfile = profile;

        var allowed = target.Tracks.Select(t => t.Index).ToHashSet();
        _selected.Clear();
        foreach (var track in _tracks.Where(t => allowed.Contains(t.Index)))
        {
            _selected.Add(track.Index);
        }

        foreach (var resolved in target.Tracks)
        {
            var editable = _tracks.FirstOrDefault(t => t.Index == resolved.Index);
            if (editable == null)
            {
                continue;
            }

            if (resolved.Name != null)
            {
                editable.Name = string.IsNullOrEmpty(resolved.Name) ? null : resolved.Name;
            }

            if (resolved.LanguageCode != null)
            {
                var iso = IsoLanguage.Find(resolved.LanguageCode);
                editable.LanguageCode = resolved.LanguageCode;
                editable.LanguageName = iso != IsoLanguage.Unknown ? iso.Name : editable.LanguageName;
            }

            if (resolved.IsDefault is { } isDefault)
            {
                editable.IsDefault = isDefault;
            }

            if (resolved.IsForced is { } isForced)
            {
                editable.IsForced = isForced;
            }

            if (resolved.IsCommentary is { } isComm)
            {
                editable.IsCommentary = isComm;
            }

            if (resolved.IsHearingImpaired is { } isHi)
            {
                editable.IsHearingImpaired = isHi;
            }

            if (resolved.IsVisualImpaired is { } isAd)
            {
                editable.IsVisualImpaired = isAd;
            }

            // Matroska strips IsDub during resolution and encodes it in the title;
            // read it back from whichever title applies so the UI stays consistent.
            editable.IsDub = resolved.IsDub ?? TrackNameFlags.ContainsDub(editable.Name);

            // Same for a language a container cannot tag: the resolver downgraded
            // the code and put the variant in the name, so read it back the way
            // the next scan will, or the dropdown contradicts the name beside it.
            editable.RefineLanguageFromName();
        }

        TakeProfileOrder(target);
    }

    // The profile's track order (language priority, custom rules...) for the tracks it
    // keeps; within each type the ones it drops follow, in file order, so they stay
    // under their own heading.
    private void TakeProfileOrder(ConversionPlan target)
    {
        var rank = target.Tracks.Select((t, i) => (t.Index, i)).ToDictionary(x => x.Index, x => x.i);
        var types = target.Tracks.Where(t => t.Type != MediaTrackType.Video).Select(t => t.Type)
            .Concat(_tracks.Where(t => t.Type != MediaTrackType.Video).Select(t => t.Type))
            .Distinct()
            .ToList();

        var ordered = _tracks.Where(t => t.Type == MediaTrackType.Video).ToList();
        foreach (var type in types)
        {
            var ofType = _tracks.Where(t => t.Type == type).ToList();
            ordered.AddRange(ofType.Where(t => _selected.Contains(t.Index)).OrderBy(t => rank.GetValueOrDefault(t.Index, int.MaxValue)));
            ordered.AddRange(ofType.Where(t => !_selected.Contains(t.Index)));
        }

        _tracks.Clear();
        _tracks.AddRange(ordered);
    }

    /// <summary>
    /// Every metadata edit goes through here, so the track name follows whatever
    /// changed. The user sees the title that will actually be written, and the
    /// profile has nothing left to rename behind the conversion.
    /// </summary>
    public void Edit(TrackSnapshot track, Action<TrackSnapshot> edit)
    {
        track.ApplyTrackEdit(_file, _appliedProfile ?? _file.Profile, _tracks, edit);
    }

    /// <summary>Flags chosen on the file page's Track checks ("Use this"), set before the modal opens.</summary>
    public void ApplyPresets(IReadOnlyDictionary<int, TrackFlagPreset>? presets)
    {
        if (presets == null)
        {
            return;
        }

        foreach (var (index, preset) in presets)
        {
            if (_tracks.FirstOrDefault(t => t.Index == index) is not { } track)
            {
                continue;
            }

            Edit(track, t =>
            {
                t.IsHearingImpaired = preset.HearingImpaired ?? t.IsHearingImpaired;
                t.IsCommentary = preset.Commentary ?? t.IsCommentary;
                t.IsVisualImpaired = preset.VisualImpaired ?? t.IsVisualImpaired;
            });
        }
    }

    public void SetLanguage(TrackSnapshot track, string? languageName)
    {
        if (string.IsNullOrEmpty(languageName))
        {
            return;
        }

        var iso = IsoLanguage.Find(languageName);

        // A video track's name says nothing about its language, so none of the
        // name markers audio and subtitles get apply.
        if (track.Type == MediaTrackType.Video)
        {
            track.LanguageName = iso.Name;
            track.LanguageCode = iso.WriteCode ?? track.LanguageCode;
            return;
        }

        Edit(track, t =>
        {
            t.LanguageName = iso.Name;
            t.LanguageCode = iso.WriteCode ?? t.LanguageCode;
        });
    }

    public bool CanMoveUp(TrackSnapshot track)
    {
        var index = _tracks.IndexOf(track);
        return index > 0 && _tracks[index - 1].Type != MediaTrackType.Video;
    }

    public bool CanMoveDown(TrackSnapshot track)
    {
        var index = _tracks.IndexOf(track);
        return index >= 0 && index < _tracks.Count - 1;
    }

    public void Move(TrackSnapshot track, int direction)
    {
        var index = _tracks.IndexOf(track);
        var newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= _tracks.Count)
        {
            return;
        }

        _tracks.RemoveAt(index);
        _tracks.Insert(newIndex, track);
    }

    /// <summary>
    /// The plan for what is on screen. Video always survives; everything else in
    /// display order, which is what the converter writes and what the planner
    /// compares against the source to decide a remux is needed.
    /// </summary>
    public ConversionPlan Build()
    {
        var video = _tracks.Where(t => t.Type == MediaTrackType.Video);
        var kept = _tracks.Where(t => t.Type != MediaTrackType.Video && _selected.Contains(t.Index));
        var plan = _file.BuildTargetFromCustom(video.Concat(kept).ToList());

        var title = FileTitle.Trim();
        if (title != CurrentFileTitle)
        {
            plan.Title = title;
        }

        if (CanRenameChapters && Chapters.Any(c => c.IsChanged))
        {
            plan.Chapters = Chapters
                .Select(c => new ChapterInfo { StartMs = c.StartMs, Title = c.IsChanged ? c.Title.Trim() : null })
                .ToList();
        }

        if (CanEditTags && TagsChanged && TagProblem == null)
        {
            plan.GlobalTags = Tags
                .Where(t => !t.Removed)
                .Select(t => new FileTag { Name = t.Name.Trim(), Value = t.Value })
                .ToList();
        }

        return plan;
    }
}

/// <summary>Flags to set on one track when the custom conversion opens; null leaves a flag as it is.</summary>
public sealed record TrackFlagPreset(bool? HearingImpaired = null, bool? Commentary = null, bool? VisualImpaired = null);
