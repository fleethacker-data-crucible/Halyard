using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halyard.Core.Intelligence;
using Halyard.Core.Model;
using Halyard.Core.Providers;
using Halyard.Core.Safety;
using Xunit;

namespace Halyard.Core.Tests
{
    /// <summary>Two operators on one sandbox: decisions made at the same time, or during an apply, must all survive.</summary>
    public sealed class ProposalStoreConcurrencyTests : IDisposable
    {
        private static readonly RepositoryRef Sandbox = new RepositoryRef(ProviderKind.GitLab, "5678");
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "halyard-store-" + Guid.NewGuid().ToString("N"));
        private readonly ProposalStore _store;

        public ProposalStoreConcurrencyTests() { _store = new ProposalStore(Path.Combine(_dir, "proposals")); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private ProposalBatch Batch(int n, ProposalDecision decision = ProposalDecision.Pending)
        {
            var batch = new ProposalBatch { Kind = ProposalKind.StoryPoints, Target = Sandbox.Key };
            for (var i = 1; i <= n; i++)
                batch.Proposals.Add(new Proposal { BatchId = batch.Id, Kind = ProposalKind.StoryPoints, ItemKey = Sandbox.Key + ":" + i, ItemNumber = i, ItemCanonicalId = "STORY-" + i, ItemTitle = "STORY :: s" + i, ProposedValue = "3", Decision = decision });
            _store.Save(batch);
            return batch;
        }

        [Fact]
        public async Task ManyDecisionsAtOnceAreAllKept()
        {
            var batch = Batch(24);
            var tasks = batch.Proposals.Select((p, i) => Task.Run(() => _store.Decide(batch.Id, p.Id, i % 2 == 0 ? ProposalDecision.Approved : ProposalDecision.Rejected, "n" + i))).ToArray();
            await Task.WhenAll(tasks);
            var after = _store.Load(batch.Id)!;
            Assert.Equal(12, after.Proposals.Count(p => p.Decision == ProposalDecision.Approved));
            Assert.Equal(12, after.Proposals.Count(p => p.Decision == ProposalDecision.Rejected));
            Assert.Empty(Directory.GetFiles(_store.Directory, "*.lock"));
        }

        [Fact]
        public void ADecisionIsMadeOnTheBatchAsItIsOnDiskNow()
        {
            var batch = Batch(3);
            var stale = _store.Load(batch.Id)!;
            _store.Decide(batch.Id, batch.Proposals[0].Id, ProposalDecision.Rejected, "other operator");
            _store.Decide(batch.Id, batch.Proposals[1].Id, ProposalDecision.Approved, "me");
            var after = _store.Load(batch.Id)!;
            Assert.Equal(ProposalDecision.Rejected, after.Proposals[0].Decision);
            Assert.Equal(ProposalDecision.Approved, after.Proposals[1].Decision);
            Assert.Equal(ProposalDecision.Pending, stale.Proposals[1].Decision);
        }

        [Fact]
        public void AnAppliedProposalCannotBeDecidedAgainAndAMissingOneIsNull()
        {
            var batch = Batch(1, ProposalDecision.Applied);
            var p = _store.Decide(batch.Id, batch.Proposals[0].Id, ProposalDecision.Rejected, "x");
            Assert.Equal(ProposalDecision.Applied, p!.Decision);
            Assert.Equal(ProposalDecision.Applied, _store.Load(batch.Id)!.Proposals[0].Decision);
            Assert.Null(_store.Decide(batch.Id, "nope", ProposalDecision.Approved, null));
            Assert.Null(_store.Decide("missing-batch", "x", ProposalDecision.Approved, null));
            Assert.Equal(0, _store.DecideAll("missing-batch", ProposalDecision.Approved, null));
        }

        [Fact]
        public void SaveProposalTouchesOnlyThatProposal()
        {
            var batch = Batch(2);
            _store.Decide(batch.Id, batch.Proposals[1].Id, ProposalDecision.Approved, "concurrent");
            var mine = batch.Proposals[0];
            mine.Decision = ProposalDecision.Applied;
            _store.SaveProposal(batch.Id, mine);
            var after = _store.Load(batch.Id)!;
            Assert.Equal(ProposalDecision.Applied, after.Proposals[0].Decision);
            Assert.Equal(ProposalDecision.Approved, after.Proposals[1].Decision);
        }

        [Fact]
        public void AStaleLockFromADeadProcessDoesNotBlockForever()
        {
            var batch = Batch(1);
            var lockPath = Path.Combine(_store.Directory, "proposals-" + batch.Id + ".json.lock");
            File.WriteAllText(lockPath, "");
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-5));
            Assert.NotNull(_store.Decide(batch.Id, batch.Proposals[0].Id, ProposalDecision.Approved, null));
        }

        [Fact]
        public async Task ALiveLockMakesAWriterWaitThenProceed()
        {
            var batch = Batch(1);
            var lockPath = Path.Combine(_store.Directory, "proposals-" + batch.Id + ".json.lock");
            var holder = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            var decide = Task.Run(() => _store.Decide(batch.Id, batch.Proposals[0].Id, ProposalDecision.Approved, null));
            await Task.Delay(150);
            Assert.False(decide.IsCompleted);
            holder.Dispose();
            Assert.Equal(ProposalDecision.Approved, (await decide)!.Decision);
        }

        [Fact]
        public async Task AnApplyKeepsDecisionsMadeOnOtherProposalsAndHonoursARejection()
        {
            var gitlab = new FakeGitLabProject(5678, "platform/sandbox");
            var provider = new GitLabProvider(gitlab.Http, "https://gitlab.example.com", "t", Sandbox, 0);
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(Sandbox);
            var gateway = new WriteGateway(policy);
            var batch = new ProposalBatch { Kind = ProposalKind.StoryPoints, Target = Sandbox.Key };
            for (var i = 1; i <= 3; i++)
            {
                var title = "STORY :: s" + i;
                var iid = gitlab.Add(title, "Text " + i, "opened", "type::story");
                batch.Proposals.Add(new Proposal { BatchId = batch.Id, Kind = ProposalKind.StoryPoints, ItemKey = Sandbox.Key + ":" + iid, ItemNumber = iid, ItemCanonicalId = "STORY-" + i, ItemTitle = title, ProposedValue = "5", Decision = i == 3 ? ProposalDecision.Pending : ProposalDecision.Approved });
            }
            _store.Save(batch);
            var first = batch.Proposals[0].Id; var second = batch.Proposals[1].Id; var third = batch.Proposals[2].Id;

            // While the first proposal is being written, another operator rejects the second and approves the third.
            var original = gitlab.Http.Rules.ToList();
            gitlab.Http.Rules.Insert(0, new FakeTransport.Rule
            {
                Method = "PUT", UrlContains = "/issues/", Dynamic = call =>
                {
                    gitlab.Http.Rules.RemoveAt(0);
                    _store.Decide(batch.Id, second, ProposalDecision.Rejected, "changed my mind");
                    _store.Decide(batch.Id, third, ProposalDecision.Approved, "also this");
                    return gitlab.Http.SendAsync(call, CancellationToken.None).Result;
                },
            });

            var result = await ProposalApplier.ApplyAsync(batch, _store, provider, gateway, Sandbox, null, CancellationToken.None);

            var after = _store.Load(batch.Id)!;
            Assert.Equal(1, result.Applied);
            Assert.Equal(1, result.Skipped);
            Assert.Contains(result.Messages, m => m.Contains("no longer approved"));
            Assert.Equal(ProposalDecision.Applied, after.Proposals.Single(p => p.Id == first).Decision);
            Assert.Equal(ProposalDecision.Rejected, after.Proposals.Single(p => p.Id == second).Decision);
            Assert.Equal(ProposalDecision.Approved, after.Proposals.Single(p => p.Id == third).Decision);
            Assert.Equal("Text 2", gitlab.BodyOf(batch.Proposals[1].ItemNumber));
            Assert.Equal("Text 3", gitlab.BodyOf(batch.Proposals[2].ItemNumber));
        }
    }
}
