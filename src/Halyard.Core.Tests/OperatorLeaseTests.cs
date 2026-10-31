using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Host;
using Halyard.Core.Serialization;
using Xunit;

namespace Halyard.Core.Tests
{
    public sealed class OperatorLeaseTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "halyard-lease-" + Guid.NewGuid().ToString("N"));
        private string LeasePath { get { return Path.Combine(_dir, "write.lease"); } }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private OperatorLease Lease(string holder, double hours = 2) { return new OperatorLease(LeasePath, holder, TimeSpan.FromHours(hours)); }

        [Fact]
        public void OneOperatorAtATimeAndTheSecondIsToldWho()
        {
            var ann = Lease("ann@pc1:10");
            var bo = Lease("bo@pc2:20");
            using (ann.Acquire("apply"))
            {
                Assert.True(File.Exists(LeasePath));
                var ex = Assert.Throws<OperatorLeaseException>(() => bo.Acquire("apply"));
                Assert.Contains("ann@pc1:10", ex.Message);
                Assert.Contains("apply", ex.Message);
                Assert.Equal("ann@pc1:10", (string)bo.Current()!["holder"]!);
            }
            Assert.False(File.Exists(LeasePath));
            using (bo.Acquire("rollback")) Assert.Equal("bo@pc2:20", (string)ann.Current()!["holder"]!);
        }

        [Fact]
        public void TheSameEngineMayHoldItForSeveralJobsAndReleasesOnTheLast()
        {
            var ann = Lease("ann@pc1:10");
            var first = ann.Acquire("apply");
            var second = ann.Acquire("promote");
            first.Dispose();
            Assert.True(File.Exists(LeasePath));
            second.Dispose();
            Assert.False(File.Exists(LeasePath));
            first.Dispose(); // disposing twice is harmless
        }

        [Fact]
        public void AnExpiredLeaseIsTakenOver()
        {
            Directory.CreateDirectory(_dir);
            var old = new JObject { { "holder", "gone@pc9:1" }, { "reason", "apply" }, { "host", "SOMEWHERE-ELSE" }, { "pid", 1 }, { "acquiredAt", "2026-10-01T00:00:00Z" }, { "expiresAt", "2026-10-01T02:00:00Z" } };
            File.WriteAllText(LeasePath, old.ToString());
            Assert.Null(Lease("bo@pc2:20").Current());
            using (Lease("bo@pc2:20").Acquire("apply")) Assert.Equal("bo@pc2:20", (string)JObject.Parse(File.ReadAllText(LeasePath))["holder"]!);
        }

        [Fact]
        public void ALeaseFromAProcessThatNoLongerRunsHereIsTakenOver()
        {
            Directory.CreateDirectory(_dir);
            var dead = new JObject { { "holder", "ann@" + Environment.MachineName + ":999999" }, { "reason", "apply" }, { "host", Environment.MachineName }, { "pid", 999999 }, { "acquiredAt", DateTimeOffset.UtcNow.ToString("o") }, { "expiresAt", DateTimeOffset.UtcNow.AddHours(1).ToString("o") } };
            File.WriteAllText(LeasePath, dead.ToString());
            using (Lease("bo@pc2:20").Acquire("apply")) Assert.Equal("bo@pc2:20", (string)JObject.Parse(File.ReadAllText(LeasePath))["holder"]!);
        }

        [Fact]
        public void ALiveLeaseOnThisMachineIsRespected()
        {
            Directory.CreateDirectory(_dir);
            var mine = new JObject { { "holder", "ann@x:1" }, { "reason", "apply" }, { "host", Environment.MachineName }, { "pid", System.Diagnostics.Process.GetCurrentProcess().Id }, { "acquiredAt", DateTimeOffset.UtcNow.ToString("o") }, { "expiresAt", DateTimeOffset.UtcNow.AddHours(1).ToString("o") } };
            File.WriteAllText(LeasePath, mine.ToString());
            Assert.Throws<OperatorLeaseException>(() => Lease("bo@pc2:20").Acquire("apply"));
        }

        [Fact]
        public void ACorruptLeaseFileDoesNotBlockForever()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(LeasePath, "{ not json");
            using (Lease("bo@pc2:20").Acquire("apply")) Assert.True(File.Exists(LeasePath));
        }

        // ---- through the job manager ----

        private static JobManager Jobs(OperatorLease lease) { return new JobManager(new Diagnostics(Path.Combine(Path.GetTempPath(), "halyard-lease-log-" + Guid.NewGuid().ToString("N")))) { Lease = lease }; }

        private static async Task WaitDone(JobManager jobs, string id)
        {
            for (var i = 0; i < 200; i++)
            {
                var j = jobs.Get(id)!;
                if (j.State != JobState.Queued && j.State != JobState.Running) return;
                await Task.Delay(10);
            }
            throw new TimeoutException();
        }

        [Fact]
        public async Task WriteJobsHoldTheLeaseWhileTheyRunAndReleaseItAfterwards()
        {
            var ann = Jobs(Lease("ann@pc1:10"));
            var bo = Jobs(Lease("bo@pc2:20"));
            var gate = new TaskCompletionSource<object>();
            var job = ann.Start("apply", async ctx => { await gate.Task; return "done"; });
            Assert.True(File.Exists(LeasePath));
            Assert.Throws<OperatorLeaseException>(() => bo.Start("apply", ctx => Task.FromResult<object>("x")));
            Assert.Throws<OperatorLeaseException>(() => bo.Start("rollback", ctx => Task.FromResult<object>("x")));

            gate.SetResult(null!);
            await WaitDone(ann, job.Id);
            for (var i = 0; i < 100 && File.Exists(LeasePath); i++) await Task.Delay(10);
            Assert.False(File.Exists(LeasePath));
            var next = bo.Start("rollback", ctx => Task.FromResult<object>("x"));
            await WaitDone(bo, next.Id);
        }

        [Fact]
        public async Task ReadOnlyJobsNeedNoLeaseAndAFailedJobStillReleasesIt()
        {
            var ann = Jobs(Lease("ann@pc1:10"));
            var bo = Jobs(Lease("bo@pc2:20"));
            var gate = new TaskCompletionSource<object>();
            var writing = ann.Start("apply", async ctx => { await gate.Task; throw new InvalidOperationException("boom"); });
            var reading = bo.Start("sync", ctx => Task.FromResult<object>("ok")); // not a write kind
            await WaitDone(bo, reading.Id);
            Assert.Equal(JobState.Succeeded, bo.Get(reading.Id)!.State);

            gate.SetResult(null!);
            await WaitDone(ann, writing.Id);
            Assert.Equal(JobState.Failed, ann.Get(writing.Id)!.State);
            for (var i = 0; i < 100 && File.Exists(LeasePath); i++) await Task.Delay(10);
            Assert.False(File.Exists(LeasePath));
        }

        [Fact]
        public void ARefusedSecondJobOfTheSameKindDoesNotLeakTheLease()
        {
            var ann = Jobs(Lease("ann@pc1:10"));
            var gate = new TaskCompletionSource<object>();
            ann.Start("apply", async ctx => { await gate.Task; return "x"; });
            Assert.Throws<InvalidOperationException>(() => ann.Start("apply", ctx => Task.FromResult<object>("y")));
            gate.SetResult(null!);
        }
    }
}
