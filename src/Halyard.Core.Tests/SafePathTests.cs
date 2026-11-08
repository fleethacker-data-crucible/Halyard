using System;
using System.IO;
using Halyard.Core.Config;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>SafePath.Under: joins that cannot leave their root (Path.Combine drops earlier parts before a rooted one).</summary>
    public sealed class SafePathTests
    {
        // Built by hand (not with Path.Combine or SafePath) so the expected values do not depend on what is tested.
        private static readonly string Root = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + @"\halyard-safepath";

        [Fact]
        public void JoinsRelativePartsUnderTheRoot()
        {
            Assert.Equal(Root + @"\assets\stt\a.js", SafePath.Under(Root, "assets", @"stt\a.js"));
            Assert.Equal(Root, SafePath.Under(Root));
            Assert.Equal(Root + @"\b", SafePath.Under(Root, @"a\..\b"));
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
            Assert.Throws<ArgumentException>(() => SafePath.Under(Root, "..", "halyard-safepath-evil", "x"));
            Assert.Throws<ArgumentException>(() => SafePath.Under(Root, ""));
        }
    }
}
