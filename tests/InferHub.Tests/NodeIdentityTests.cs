using InferHub.Node;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

public class NodeIdentityTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(Path.GetTempPath(), $"inferhub-tests-{Guid.NewGuid():N}");

    public NodeIdentityTests()
    {
        Directory.CreateDirectory(tempDirectory);
    }

    [Fact]
    public void FirstCallCreatesNodeIdFile()
    {
        var identity = CreateIdentity();

        var nodeId = identity.GetOrCreateNodeId();

        Assert.True(Guid.TryParse(nodeId, out _));
        Assert.Equal(nodeId, File.ReadAllText(Path.Combine(tempDirectory, FileNodeIdentity.FileName)));
    }

    [Fact]
    public void SecondCallReturnsSameNodeId()
    {
        var identity = CreateIdentity();

        var first = identity.GetOrCreateNodeId();
        var second = identity.GetOrCreateNodeId();

        Assert.Equal(first, second);
    }

    [Fact]
    public void InvalidFileContentRegeneratesNodeId()
    {
        File.WriteAllText(Path.Combine(tempDirectory, FileNodeIdentity.FileName), "not-a-guid");
        var identity = CreateIdentity();

        var nodeId = identity.GetOrCreateNodeId();

        Assert.True(Guid.TryParse(nodeId, out _));
        Assert.NotEqual("not-a-guid", File.ReadAllText(Path.Combine(tempDirectory, FileNodeIdentity.FileName)));
    }

    public void Dispose()
    {
        Directory.Delete(tempDirectory, recursive: true);
    }

    private FileNodeIdentity CreateIdentity()
    {
        return new FileNodeIdentity(
            new TestHostEnvironment(tempDirectory),
            NullLogger<FileNodeIdentity>.Instance);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "InferHub.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
