using Microsoft.Extensions.Configuration;
using Kintsugi.Infrastructure.Storage;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// The storage class is the one thing that touches the disk with a name that arrived in a request,
/// and it is reached from more than one command — so it refuses a path of its own accord rather
/// than trusting that PublishAgentPackageCommandValidator ran first.
/// </summary>
public class AgentPackageFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kintsugi-package-storage-" + Guid.NewGuid().ToString("N"));
    private readonly AgentPackageFileStorage _storage;

    public AgentPackageFileStorageTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentPackages:Directory"] = _root })
            .Build();
        _storage = new AgentPackageFileStorage(configuration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("../../escaped.txt")]
    [InlineData("/etc/escaped.txt")]
    [InlineData("nested/escaped.txt")]
    [InlineData("nested\\escaped.txt")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveAsync_WithAFileNameThatIsNotBare_RefusesAndWritesNothing(string fileName)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storage.SaveAsync("linux", fileName, new MemoryStream(new byte[] { 1 }), CancellationToken.None));

        // Nothing above the platform directory, and no platform directory either — the check runs
        // before anything is created.
        Assert.False(Directory.Exists(_root), "the storage must not have created anything on a refused name");
    }

    [Theory]
    [InlineData("../linux")]
    [InlineData("linux/../..")]
    [InlineData("/linux")]
    public async Task SaveAsync_WithAPlatformThatIsNotBare_Refuses(string platform)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storage.SaveAsync(platform, "package.tar.gz", new MemoryStream(new byte[] { 1 }), CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_WithABareName_WritesUnderThePlatformDirectory()
    {
        var (size, sha256) = await _storage.SaveAsync("linux", "kintsugi-agent-linux-v0.13.0.tar.gz", new MemoryStream(new byte[] { 1, 2, 3 }), CancellationToken.None);

        Assert.Equal(3, size);
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", sha256);
        Assert.True(File.Exists(Path.Combine(_root, "linux", "kintsugi-agent-linux-v0.13.0.tar.gz")));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/escaped.txt")]
    [InlineData("..")]
    public void OpenRead_WithAFileNameThatIsNotBare_Refuses(string fileName)
    {
        Assert.Throws<ArgumentException>(() => _storage.OpenRead("linux", fileName));
    }

    [Theory]
    [InlineData("kintsugi-agent-linux-v0.13.0.tar.gz")]
    [InlineData("a")]
    [InlineData("payload_1.bin")]
    public void RequireBareName_AcceptsABareName(string name)
    {
        Assert.Equal(name, AgentPackageFileStorage.RequireBareName(name, "name"));
    }
}
