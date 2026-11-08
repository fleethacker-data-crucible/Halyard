using System;
using System.IO;
using Halyard.Core.Config;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>SafePath.Under: joins that cannot leave their root (Path.Combine drops earlier parts before a rooted one).</summary>
    public sealed class SafePathTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "halyard-safepath");

        [Fact]
        public void JoinsRelativePartsUnderTheRoot()
        {
            Assert.Equal(Path.Combine(Path.GetFullPath(Root), "assets", "stt", "a.js"), SafePath.Under(Root, "assets", @"stt\a.js"));
            Assert.Equal(Path.GetFullPath(Root).TrimEnd('\\'), SafePath.Under(Root));
        }

        [Fact]
        public void RefusesARootedPartThatPathCombineWouldUseInstead()
        {
            Assert.Throws<ArgumentException>(() => SafePath.Under(Root, @"C:\Windows\win.ini"));
            Assert.Throws<ArgumentException>(() => SafePath.Under(Root, "assets", @"\\server\share\x"));
        }

        [Fact]
        public void RefusesClimbingOutAndLookalikeSiblings()
        {
            Assert.Throws<ArgumentException>(() => SafePath.Under(Root, @"..\config\halyard.env"));
            Assert.Throws<ArgumentException>(() => SafePath.Under(Root, "..", Path.GetFileName(Root) + "-evil", "x"));
            Assert.Throws<ArgumentException>(() => SafePath.Under(Root, ""));
        }
    }
}
