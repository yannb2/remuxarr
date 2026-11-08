using Muxarr.Core.Extensions;
using Muxarr.Core.Models;
using Muxarr.Data.Extensions;
using Muxarr.Web.Components.Shared.Modals;
using static Muxarr.Tests.TestData;

namespace Muxarr.Tests;

/// <summary>
/// What the file page's Track checks hand to Custom conversion: the "Commentary or AD?" hint, the
/// flags picked there, and the release label box.
/// </summary>
[TestClass]
public class TrackChecksTests
{
    [TestMethod]
    public void AStereoLossyTrackAfterTheMultichannelOneIsUnsure()
    {
        var tracks = new[]
        {
            Video(0),
            Audio(1, "English", nameof(AudioCodec.TrueHd), channels: 8),
            Audio(2, "English", nameof(AudioCodec.Ac3), channels: 2),
            Audio(3, "English", nameof(AudioCodec.Ac3), channels: 2, commentary: true),
            Audio(4, "French", nameof(AudioCodec.Ac3), channels: 2),
            Audio(5, "English", nameof(AudioCodec.Flac), channels: 2)
        };

        var unsure = TrackQualityScorer.CommentaryOrAudioDescription(tracks);

        // 3 is already a commentary, 4 has no multichannel French before it, 5 is lossless.
        CollectionAssert.AreEquivalent(new[] { 2 }, unsure.ToArray());
    }

    [TestMethod]
    public void AStereoTrackFirstIsNotUnsure()
    {
        var tracks = new[]
        {
            Audio(1, "English", nameof(AudioCodec.Ac3), channels: 2),
            Audio(2, "English", nameof(AudioCodec.TrueHd), channels: 8)
        };

        Assert.IsEmpty(TrackQualityScorer.CommentaryOrAudioDescription(tracks));
    }

    [TestMethod]
    public void PresetsSetTheFlagsWhenTheModalOpens()
    {
        var file = MakeFile("English", Video(0), Audio(1, "English"), Audio(2, "English", channels: 2), Sub(3, "English"));
        var editor = new CustomConversionEditor(file);

        editor.ApplyPresets(new Dictionary<int, TrackFlagPreset>
        {
            [2] = new(VisualImpaired: true),
            [3] = new(HearingImpaired: true, Commentary: false)
        });

        var audio = editor.Tracks.Single(t => t.Index == 2);
        var sub = editor.Tracks.Single(t => t.Index == 3);
        Assert.IsTrue(audio.IsVisualImpaired);
        Assert.IsFalse(audio.IsCommentary, "a flag the preset leaves out stays as it was");
        Assert.IsTrue(sub.IsHearingImpaired);
        Assert.IsFalse(editor.Tracks.Single(t => t.Index == 1).IsVisualImpaired);
    }

    [TestMethod]
    public void TheReleaseLabelIsTheLabelTag()
    {
        var file = MakeFile("English", Video(0), Audio(1));
        file.Snapshot.ContainerType = "Matroska";
        var editor = new CustomConversionEditor(file);
        Assert.AreEqual("", editor.ReleaseLabel);

        editor.ReleaseLabel = "  Criterion ";

        Assert.AreEqual("Criterion", editor.ReleaseLabel);
        var tag = editor.Build().GlobalTags!.Single();
        Assert.AreEqual(CustomConversionEditor.ReleaseLabelTag, tag.Name);
        Assert.AreEqual("Criterion", tag.Value);

        editor.ReleaseLabel = "";
        Assert.IsNull(editor.Build().GlobalTags, "a label typed and cleared leaves nothing");
        Assert.AreEqual("", editor.Tags.Single(t => t.Name == CustomConversionEditor.ReleaseLabelTag).Value,
            "the LABEL row stays listed, empty");
    }

    [TestMethod]
    public void EmptyingTheReleaseLabelRemovesTheFilesTag()
    {
        var file = MakeFile("English", Video(0), Audio(1));
        file.Snapshot.ContainerType = "Matroska";
        file.Snapshot.GlobalTags = [new FileTag { Name = "DISTRIBUTED_BY", Value = "Arrow" }, new FileTag { Name = "RELEASE_GROUP", Value = "YANNRIP" }];
        var editor = new CustomConversionEditor(file);
        Assert.AreEqual("Arrow", editor.ReleaseLabel);

        editor.ReleaseLabel = "";

        var kept = editor.Build().GlobalTags!;
        Assert.AreEqual("RELEASE_GROUP", kept.Single().Name);

        // Typed again: the label goes to LABEL, the old DISTRIBUTED_BY stays gone.
        editor.ReleaseLabel = "Arrow Video";
        var tags = editor.Build().GlobalTags!;
        Assert.AreEqual("Arrow Video", tags.Single(t => t.Name == "LABEL").Value);
        Assert.IsFalse(tags.Any(t => t.Name == "DISTRIBUTED_BY"));
    }

    [TestMethod]
    public void AnOldDistributedByLabelMovesToLabelOnEdit()
    {
        var file = MakeFile("English", Video(0), Audio(1));
        file.Snapshot.ContainerType = "Matroska";
        file.Snapshot.GlobalTags = [new FileTag { Name = "DISTRIBUTED_BY", Value = "Criterion" }];
        var editor = new CustomConversionEditor(file);
        Assert.AreEqual("Criterion", editor.ReleaseLabel, "shown from the old tag");

        editor.ReleaseLabel = "Criterion";

        var tags = editor.Build().GlobalTags!;
        Assert.AreEqual("Criterion", tags.Single().Value);
        Assert.AreEqual("LABEL", tags.Single().Name);
    }
}
