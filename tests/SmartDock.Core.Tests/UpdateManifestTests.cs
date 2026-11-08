using SmartDock.Core.Updates;

namespace SmartDock.Core.Tests;

public class UpdateManifestTests
{
    private const string Sha = "3F1A0C9B8E7D6F5A4B3C2D1E0F9A8B7C6D5E4F3A2B1C0D9E8F7A6B5C4D3E2F1A";

    [Fact]
    public void Parses_a_valid_manifest()
    {
        var m = UpdateManifest.Parse($$"""{ "version": "1.13.0", "msi": "SmartDock-Setup-v1.13.0-x64.msi", "sha256": "{{Sha.ToLowerInvariant()}}", "notes": "Automation" }""");

        Assert.Equal(new Version(1, 13, 0), m.Version);
        Assert.Equal("SmartDock-Setup-v1.13.0-x64.msi", m.Msi);
        Assert.Equal(Sha, m.Sha256);
        Assert.Equal("Automation", m.Notes);
    }

    [Theory]
    [InlineData("../evil.msi")]
    [InlineData("C:\\temp\\x.msi")]
    [InlineData("https://evil.example/x.msi")]
    [InlineData("setup.exe")]
    public void Rejects_msi_names_that_are_paths_urls_or_not_msi(string msi) =>
        Assert.Throws<UpdateException>(() => UpdateManifest.Parse($$"""{ "version": "1.13.0", "msi": "{{msi.Replace("\\", "\\\\")}}", "sha256": "{{Sha}}" }"""));

    [Fact]
    public void Rejects_bad_checksum_and_version()
    {
        Assert.Throws<UpdateException>(() => UpdateManifest.Parse("""{ "version": "1.13.0", "msi": "a.msi", "sha256": "abc" }"""));
        Assert.Throws<UpdateException>(() => UpdateManifest.Parse($$"""{ "version": "latest", "msi": "a.msi", "sha256": "{{Sha}}" }"""));
        Assert.Throws<UpdateException>(() => UpdateManifest.Parse("not json"));
    }

    [Theory]
    [InlineData("1.13.0", "1.12.0", true)]
    [InlineData("1.12.1", "1.12.0", true)]
    [InlineData("1.12.0", "1.12.0", false)]
    [InlineData("1.11.9", "1.12.0", false)]
    [InlineData("1.13", "1.12.5", true)]
    public void Compares_versions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateManifest.IsNewer(Version.Parse(candidate), current));
}
