using System;
using System.IO;
using Halyard.Core.Config;
using Halyard.Core.Model;
using Halyard.Core.Snapshots;
using Halyard.Core.Synthetic;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>The scheduled report's webhook delivery (Send-HalyardReport.ps1 -Via Webhook): what it needs and what it refuses to do.</summary>
    public sealed class ReportWebhookFacadeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "halyard-facade-" + Guid.NewGuid().ToString("N"));
        public ReportWebhookFacadeTests() { Directory.CreateDirectory(_root); }
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private HalyardSettings Settings(string? webhook)
        {
            return new HalyardSettings { RootDirectory = _root, ReportWebhookUrl = webhook };
        }

        private void Snapshot(HalyardSettings s)
        {
            new SnapshotStore(s.SnapshotsDirectory).Save(Items.Repo, SyntheticCatalog.ToWorkItems(SyntheticCatalog.Generate(SyntheticOptions.ForTotal(30)), Items.Repo), null);
        }

        [Fact]
        public void NoSnapshotIsAClearError()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Facade.NotifyWebhook(Settings("https://hooks.slack.com/services/T0/B0/x"), null, "Weekly", true));
            Assert.Contains("synchronize", ex.Message);
        }

        [Fact]
        public void DryRunReturnsTheMessageAndNeedsNoWebhook()
        {
            var s = Settings(null);
            Snapshot(s);
            var text = Facade.NotifyWebhook(s, null, "Weekly", true);
            Assert.StartsWith("Weekly - ", text);
            Assert.Contains("Features:", text);
        }

        [Fact]
        public void SendingWithoutAWebhookNamesTheSetting()
        {
            var s = Settings(null);
            Snapshot(s);
            var ex = Assert.Throws<InvalidOperationException>(() => Facade.NotifyWebhook(s, null, "Weekly", false));
            Assert.Contains("HALYARD_REPORT_WEBHOOK", ex.Message);
        }

        [Fact]
        public void AnInsecureAddressIsRefusedBeforeAnythingIsSent()
        {
            var s = Settings("http://hooks.example.test/x");
            Snapshot(s);
            var ex = Assert.ThrowsAny<Exception>(() => Facade.NotifyWebhook(s, null, "Weekly", false));
            Assert.Contains("https", ex.Message);
            Assert.DoesNotContain("hooks.example.test", ex.Message);
        }
    }
}
