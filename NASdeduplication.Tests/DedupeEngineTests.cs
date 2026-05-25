using Xunit;
using System.IO;
using System.Text;
using NASDeduplicator;

namespace NASdeduplication.Tests
{
    public class DedupeEngineTests
    {
        [Theory]
        [InlineData(@"C:\Temp\file.txt", @"\\?\C:\Temp\file.txt")]
        [InlineData(@"\\Pwlaw-nas\Clients\file.txt", @"\\?\UNC\Pwlaw-nas\Clients\file.txt")]
        public void ToLongPath_ConvertsCorrectly(string input, string expected)
        {
            var result = DedupeEngine.ToLongPath(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void IsNetworkError_IdentifiesKnownErrors()
        {
            var ex = new IOException("Network name not found", unchecked((int)0x80070040));
            Assert.True(DedupeEngine.IsNetworkError(ex));

            var normalEx = new IOException("File not found", unchecked((int)0x80070002));
            Assert.False(DedupeEngine.IsNetworkError(normalEx));
        }

        [Fact]
        public void CalculateFullHash_ReturnsConsistentHash()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                string content = "Hello World Deduplication Test Content";
                File.WriteAllText(tempFile, content);

                string hash1 = DedupeEngine.CalculateFullHash(tempFile);
                string hash2 = DedupeEngine.CalculateFullHash(tempFile);

                Assert.Equal(hash1, hash2);
                Assert.False(string.IsNullOrEmpty(hash1));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void CalculateSamplingHash_ReadsOnlyParts()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                // Create a 10MB file
                byte[] data = new byte[10 * 1024 * 1024];
                new Random().NextBytes(data);
                File.WriteAllBytes(tempFile, data);

                string hash = DedupeEngine.CalculateSamplingHash(tempFile, out long bytesRead);

                // Should read roughly 3MB (start, middle, end)
                Assert.Equal(3145728, bytesRead);
                Assert.StartsWith("SAMPLED_", hash);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}