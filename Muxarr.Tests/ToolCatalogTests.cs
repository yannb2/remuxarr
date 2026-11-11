using Muxarr.Web.Services.Tools;

namespace Muxarr.Tests;

[TestClass]
public class ToolCatalogTests
{
    private static ToolDefinition Tool(string id) => ToolCatalog.All.Single(t => t.Id == id);

    [TestMethod]
    public void ReadsInstalledVersions()
    {
        Assert.AreEqual("103.0", ToolCatalog.InstalledVersion(Tool("mkvtoolnix"), "mkvmerge v103.0 ('Asylum') 64-bit"));
        Assert.AreEqual("2.3.4", ToolCatalog.InstalledVersion(Tool("dovi_tool"), "dovi_tool 2.3.4"));
        Assert.AreEqual("2.0.0", ToolCatalog.InstalledVersion(Tool("alass"), "alass-cli 2.0.0"));
        Assert.AreEqual("9.0.2", ToolCatalog.InstalledVersion(Tool("ffmpeg"), "ffmpeg version n9.0.2-3-g1234abcd-20261103 Copyright"));
        Assert.AreEqual("9.0", ToolCatalog.InstalledVersion(Tool("ffprobe"), "ffprobe version n9.0-12-g1234 Copyright"));
    }

    [TestMethod]
    public void FindsTheNewestVersionUpstream()
    {
        const string json = """
            {"tag_name":"latest","assets":[
              {"name":"ffmpeg-master-latest-linux64-gpl.tar.xz"},
              {"name":"ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz"},
              {"name":"ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz"},
              {"name":"ffmpeg-n10.0-latest-win64-gpl-10.0.zip"}]}
            """;
        Assert.AreEqual("9.0", ToolCatalog.Highest(Tool("ffmpeg").UpstreamPattern!, ToolCatalog.GitHubAssetNames(json)));
        Assert.AreEqual("104.0", ToolCatalog.Highest(Tool("mkvtoolnix").UpstreamPattern!,
            "<mkvtoolnix-releases><latest-source><version>104.0</version></latest-source></mkvtoolnix-releases>"));
        Assert.IsNull(ToolCatalog.Highest(Tool("dovi_tool").UpstreamPattern!, ToolCatalog.GitHubTag("{\"tag_name\":\"libdovi-3.4.0\"}")!));
    }

    [TestMethod]
    public void UpdateAvailableComparesWithWhatIsInstalled()
    {
        Assert.IsTrue(VersionComparer.Instance.Compare("2.3.10", "2.3.4") > 0);
        Assert.AreEqual(0, VersionComparer.Instance.Compare("103.0", "103"));

        var ffmpeg = new ToolStatus { Tool = Tool("ffmpeg"), Installed = "9.0.2", Latest = "9.0" };
        Assert.IsFalse(ffmpeg.UpdateAvailable);
        Assert.IsTrue(ffmpeg with { Installed = "8.1.3" } is { UpdateAvailable: true });
    }
}
