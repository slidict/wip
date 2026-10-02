using System.Security.Cryptography;
using System.Text;
using Wip.Execution;

namespace Wip.Tests;

public class FileVolumeUsageStoreTests
{
    [Fact]
    public void ObservationsSurviveRestartAndDoNotApplyToAnotherGeneration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wip-volume-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new FileVolumeUsageStore(directory))
            {
                Assert.False(store.WasUsed("first")); store.MarkUsed("first"); store.MarkUsed("first");
            }
            using var reopened = new FileVolumeUsageStore(directory);
            Assert.True(reopened.WasUsed("first")); Assert.False(reopened.WasUsed("second"));
            reopened.MarkUsed("second"); reopened.Forget("first"); reopened.Forget("first");
            Assert.False(reopened.WasUsed("first")); Assert.True(reopened.WasUsed("second"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{\"version\":\"1\",\"backendName\":\"first\"}")]
    [InlineData("{\"version\":1,\"backendName\":\"other\"}")]
    public void CorruptMarkersFailClosed(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wip-volume-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new FileVolumeUsageStore(directory);
            var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("first"))).ToLowerInvariant() + ".json");
            File.WriteAllText(path, content);
            Assert.Throws<WipException>(() => store.WasUsed("first"));
            Assert.Throws<WipException>(() => store.MarkUsed("first"));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
