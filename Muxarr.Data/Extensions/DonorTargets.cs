using Muxarr.Core.Language;
using Muxarr.Core.Models;
using Muxarr.Data.Entities;

namespace Muxarr.Data.Extensions;

// Fork: the Copy subtitles page. Donor subtitle tracks go through the file's
// profile as if the file already had them, so they are named, ordered,
// flagged and filtered by the same rules as its own tracks.
public static class DonorTargets
{
    // Donor tracks are numbered from here while the profile runs, far past any
    // real track number, and taken out of the plan again afterwards.
    public const int FirstDonorIndex = 100_000;

    /// <summary>The donor track as the profile sees it: its probe, with the page's choices applied.</summary>
    public static TrackSnapshot DonorSnapshot(TrackSnapshot probed, DonorTrack donor)
    {
        var track = probed.ToSnapshot();
        if (!string.IsNullOrEmpty(donor.LanguageCode))
        {
            var language = IsoLanguage.Find(donor.LanguageCode);
            track.LanguageCode = donor.LanguageCode;
            track.LanguageName = language.Name;
        }

        if (donor.IsForced is { } forced)
        {
            track.IsForced = forced;
        }

        if (donor.IsHearingImpaired is { } sdh)
        {
            track.IsHearingImpaired = sdh;
        }

        // An auto-synced copy is a loose file: the name and commentary flag of the
        // track it was made from come with the donor instead.
        if (donor.Name != null)
        {
            track.Name = donor.Name;
        }

        if (donor.IsCommentary is { } commentary)
        {
            track.IsCommentary = commentary;
        }

        // The donor's default flag says nothing about this file: a new track starts
        // as not default, and the profile's default rule decides from there.
        track.IsDefault = false;
        track.Type = MediaTrackType.Subtitles;
        return track;
    }

    /// <summary>
    /// The profile's plan for the file with the donor tracks added. Each donor gets its
    /// Output (null when the profile drops it) and the source track it goes before; the
    /// plan's Tracks are the file's own, and Donors lists the ones that are kept.
    /// </summary>
    public static ConversionPlan BuildTargetWithDonors(this MediaFile file, Profile? profile,
        IReadOnlyList<(DonorTrack Donor, TrackSnapshot Track)> donors)
    {
        if (donors.Count == 0)
        {
            return file.BuildTargetFromProfile(profile);
        }

        var tracks = file.Snapshot.Tracks.Select(t => t.ToSnapshot()).ToList();
        for (var n = 0; n < donors.Count; n++)
        {
            var track = DonorSnapshot(donors[n].Track, donors[n].Donor);
            track.Index = FirstDonorIndex + n;
            tracks.Add(track);
        }

        var snapshot = file.Snapshot;
        var combined = new MediaFile
        {
            Id = file.Id,
            Path = file.Path,
            Title = file.Title,
            OriginalLanguage = file.OriginalLanguage,
            Profile = file.Profile,
            ProfileId = file.ProfileId,
            OriginalTrackNames = file.OriginalTrackNames,
            Snapshot = new MediaSnapshot
            {
                ContainerType = snapshot.ContainerType,
                Title = snapshot.Title,
                Resolution = snapshot.Resolution,
                DurationMs = snapshot.DurationMs,
                VideoBitDepth = snapshot.VideoBitDepth,
                TrackCount = tracks.Count,
                HasChapters = snapshot.HasChapters,
                HasAttachments = snapshot.HasAttachments,
                HasFaststart = snapshot.HasFaststart,
                Chapters = snapshot.Chapters,
                GlobalTags = snapshot.GlobalTags,
                Tracks = tracks
            }
        };

        var target = combined.BuildTargetFromProfile(profile);
        foreach (var (donor, _) in donors)
        {
            donor.Output = null;
            donor.BeforeIndex = -1;
        }

        var own = new List<TrackPlan>();
        var waiting = new List<DonorTrack>();
        foreach (var track in target.Tracks)
        {
            if (track.Index >= FirstDonorIndex)
            {
                var donor = donors[track.Index - FirstDonorIndex].Donor;
                var probed = tracks[file.Snapshot.Tracks.Count + track.Index - FirstDonorIndex];
                track.Index = 0;
                // Nothing is inherited from a donor: every field the output needs is set.
                track.Name ??= probed.Name ?? string.Empty;
                track.LanguageCode ??= probed.LanguageCode;
                donor.Output = track;
                waiting.Add(donor);
                continue;
            }

            foreach (var donor in waiting)
            {
                donor.BeforeIndex = track.Index;
            }

            waiting.Clear();
            own.Add(track);
        }

        target.Tracks = own;
        target.Donors = donors.Select(d => d.Donor).Where(d => d.Output != null).ToList();
        if (target.Donors.Count == 0)
        {
            target.Donors = null;
        }

        return target;
    }
}
