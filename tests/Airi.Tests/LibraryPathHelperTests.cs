using System;
using System.IO;
using Airi.Infrastructure;
using Xunit;

namespace Airi.Tests
{
    public sealed class LibraryPathHelperTests
    {
        [Theory]
        [InlineData("FC2-PPV-12345", "FC2PPV12345")]
        [InlineData("abp-123", "ABP123")]
        [InlineData("  s-cute  999 ", "SCUTE999")]
        public void NormalizeCode_ReturnsExpectedValue(string input, string expected)
        {
            var normalized = LibraryPathHelper.NormalizeCode(input);

            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData("Videos/sample.mp4", "./Videos/sample.mp4")]
        [InlineData("./Videos/sample.mp4", "./Videos/sample.mp4")]
        [InlineData("../Videos/sample.mp4", "../Videos/sample.mp4")]
        public void NormalizeLibraryPath_NormalizesRelativePath(string input, string expected)
        {
            var normalized = LibraryPathHelper.NormalizeLibraryPath(input);

            Assert.Equal(expected, normalized);
        }

        [Fact]
        public void NormalizeLibraryPath_PreservesRootedPath()
        {
            var rootedPath = Path.Combine(Path.GetTempPath(), "sample.mp4");

            var normalized = LibraryPathHelper.NormalizeLibraryPath(rootedPath);

            Assert.Equal(rootedPath.Replace('\\', '/'), normalized);
        }

        [Fact]
        public void Combine_JoinsRootAndRelativePath()
        {
            var combined = LibraryPathHelper.Combine("./Videos", Path.Combine("nested", "sample.mp4"));

            Assert.Equal("./Videos/nested/sample.mp4", combined);
        }

        [Fact]
        public void ResolveToAbsolute_ResolvesRelativeLibraryPath()
        {
            var absolute = LibraryPathHelper.ResolveToAbsolute("./Videos/sample.mp4");

            Assert.True(Path.IsPathRooted(absolute));
            Assert.EndsWith(Path.Combine("Videos", "sample.mp4"), absolute, StringComparison.OrdinalIgnoreCase);
        }
    }
}
