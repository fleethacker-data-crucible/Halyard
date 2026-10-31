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
    /// <summary>Apply journal and rollback of the last apply, against a stateful fake GitLab project (no live service).</summary>
    public sealed class ApplyRollbackTests : IDisposable
    {
        private static readonly RepositoryRef Sandbox = new RepositoryRef(ProviderKind.GitLab, "5678");
        private static readonly RepositoryRef Source = new RepositoryRef(ProviderKind.GitLab, "1234");
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "halyard-rollback-" + Guid.NewGuid().ToString("N"));
        private readonly FakeGitLabProject _gitlab = new FakeGitLabProject(5678, "platform/sandbox");
        private readonly GitLabProvider _provider;
        private readonly WriteGateway _gateway;
        private readonly ProposalStore _store;
        private readonly ApplyJournal _journal;

        public ApplyRollbackTests()
        {
            _provider = new GitLabProvider(_gitlab.Http, "https://gitlab.example.com", "t", Sandbox, 0);
            var policy = new WritePolicy();
            policy.SandboxTargets.Add(Sandbox);
            policy.ProtectedTargets.Add(Source);
            _gateway = new WriteGateway(policy);
            _store = new ProposalStore(Path.Combine(_dir, "proposals"));
            _journal = new ApplyJournal(Path.Combine(_dir, "applied"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private (long iid, string title) AddStory(string name, string body = "Plain narrative.")
        {
            var title = "STORY :: " + name;
            return (_gitlab.Add(title, body, "opened", "type::story"), title);
        }

        private ProposalBatch Batch(params (long iid, string title, string points)[] rows)
        {
            return BatchOf(ProposalKind.StoryPoints, rows);
        }

        private ProposalBatch BatchOf(ProposalKind kind, params (long iid, string title, string points)[] rows)
        {
            var batch = new ProposalBatch { Kind = kind, Target = Sandbox.Key };
            foreach (var row in rows)
            {
                batch.Proposals.Add(new Proposal
                {
                    BatchId = batch.Id, Kind = kind, ItemKey = Sandbox.Key + ":" + row.iid, ItemNumber = row.iid, ItemCanonicalId = "STORY-" + row.iid, ItemTitle = row.title,
                    ProposedValue = row.points, Decision = ProposalDecision.Approved,
                });
            }
            _store.Save(batch);
            return batch;
        }

        private async Task<(ApplyResult result, ApplyRun run)> Apply(ProposalBatch batch, ApplyRun? run = null)
        {
            run = run ?? new ApplyRun { Target = Sandbox.Key };
            var result = await ProposalApplier.ApplyAsync(batch, _store, _provider, _gateway, Sandbox, null, CancellationToken.None, run, _journal);
            return (result, run);
        }

        private Task<RollbackResult> Rollback(ApplyRun run, bool force = false, bool dryRun = false)
        {
            return ApplyRollback.RollbackAsync(run, _journal, _store, _provider, _gateway, Sandbox, force, dryRun, null, CancellationToken.None);
        }

        [Fact]
        public async Task ApplyJournalsThePreviousAndTheWrittenBody()
        {
            var (iid, title) = AddStory("login", "Original text.");
            var (result, run) = await Apply(Batch((iid, title, "5")));
            Assert.Equal(1, result.Applied);
            var entry = Assert.Single(run.Entries);
            Assert.Equal("Original text.", entry.PreviousBody);
            Assert.Equal(_gitlab.BodyOf(iid), entry.AppliedBody);
            Assert.NotEqual(entry.PreviousBody, entry.AppliedBody);
            Assert.Contains("points", entry.AppliedBody);
            Assert.Equal(iid, entry.ItemNumber);
            Assert.Equal(ProposalKind.StoryPoints, entry.Kind);
            // saved to disk as it goes
            var loaded = _journal.Load(run.Id)!;
            Assert.Equal(entry.PreviousBody, loaded.Entries.Single().PreviousBody);
            Assert.Equal(new[] { run.Entries[0].BatchId }, loaded.BatchIds.ToArray());
        }

        [Fact]
        public async Task ApplyWithoutAJournalStillWorksAndWritesNoRunFile()
        {
            var (iid, title) = AddStory("login");
            var batch = Batch((iid, title, "3"));
            var result = await ProposalApplier.ApplyAsync(batch, _store, _provider, _gateway, Sandbox, null, CancellationToken.None);
            Assert.Equal(1, result.Applied);
            Assert.Empty(_journal.LoadAll());
        }

        [Fact]
        public async Task AnItemThatAlreadyHadTheBodyIsNotJournalled()
        {
            var (iid, title) = AddStory("login");
            var (_, first) = await Apply(BatchOf(ProposalKind.ParentLink, (iid, title, "FEATURE-x")));
            Assert.Single(first.Entries);
            var second = BatchOf(ProposalKind.ParentLink, (iid, title, "FEATURE-x"));
            var (result, run) = await Apply(second);
            Assert.Equal(1, result.Skipped);
            Assert.Empty(run.Entries);
        }

        [Fact]
        public async Task RollbackRestoresTheEarlierBodyAndSendsTheProposalBackToReview()
        {
            var (iid, title) = AddStory("login", "Original text.");
            var batch = Batch((iid, title, "5"));
            var (_, run) = await Apply(batch);
            Assert.NotEqual("Original text.", _gitlab.BodyOf(iid));

            var result = await Rollback(run);

            Assert.Equal(1, result.Restored);
            Assert.Equal(0, result.Conflicts);
            Assert.Equal(RollbackStatus.Restored, Assert.Single(result.Rows).Status);
            Assert.Equal("Original text.", _gitlab.BodyOf(iid));
            var proposal = _store.Load(batch.Id)!.Proposals.Single();
            Assert.Equal(ProposalDecision.Pending, proposal.Decision);
            Assert.Null(proposal.AppliedAt);
            Assert.Contains("Rolled back", proposal.DecisionNote);
            var saved = _journal.Load(run.Id)!;
            Assert.False(saved.CanRollBack);
            Assert.NotNull(saved.Entries[0].RolledBackAt);
            Assert.Equal("restored", saved.Entries[0].RollbackNote);
        }

        [Fact]
        public async Task RollbackGoesThroughTheGatewayWithItsOwnIntentAndAudit()
        {
            var (iid, title) = AddStory("login");
            var (_, run) = await Apply(Batch((iid, title, "5")));
            _gateway.ClearAudit();
            await Rollback(run);
            var audit = Assert.Single(_gateway.Audit);
            Assert.Equal("update", audit.Operation);
            Assert.Equal(run.Id, audit.RunId);
            Assert.Equal("Undo 1 applied changes from apply run " + run.Id, audit.Reason);
            Assert.Equal(Sandbox.Key, audit.Target);
        }

        [Fact]
        public async Task RollbackRefusesARunThatWroteToAnotherTarget()
        {
            var (iid, title) = AddStory("login");
            var (_, run) = await Apply(Batch((iid, title, "5")));
            run.Target = Source.Key;
            await Assert.ThrowsAsync<WriteBlockedException>(() => Rollback(run));
            Assert.NotEqual("Plain narrative.", _gitlab.BodyOf(iid));
        }

        [Fact]
        public async Task RollbackRefusesAProtectedTargetEvenWhenTheRunClaimsIt()
        {
            var (iid, title) = AddStory("login");
            var (_, run) = await Apply(Batch((iid, title, "5")));
            run.Target = Source.Key;
            var before = _gitlab.Http.Writes().Count();
            await Assert.ThrowsAsync<WriteBlockedException>(() => ApplyRollback.RollbackAsync(run, _journal, _store, _provider, _gateway, Source, false, false, null, CancellationToken.None));
            Assert.Equal(before, _gitlab.Http.Writes().Count());
        }

        [Fact]
        public async Task SeveralProposalsOnOneItemUnwindToTheOriginal()
        {
            var (iid, title) = AddStory("login", "Original text.");
            var run = new ApplyRun { Target = Sandbox.Key };
            await Apply(Batch((iid, title, "3")), run);
            var afterFirst = _gitlab.BodyOf(iid);
            await Apply(Batch((iid, title, "8")), run);
            Assert.Equal(2, run.Entries.Count);
            Assert.NotEqual(afterFirst, _gitlab.BodyOf(iid));

            var preview = await Rollback(run, dryRun: true);
            Assert.True(preview.DryRun);
            Assert.Equal(2, preview.Restored);
            Assert.All(preview.Rows, r => Assert.Equal(RollbackStatus.Restore, r.Status));

            var result = await Rollback(run);
            Assert.Equal(2, result.Restored);
            Assert.Equal(0, result.Conflicts);
            Assert.Equal("Original text.", _gitlab.BodyOf(iid));
            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(afterFirst, run.Entries.OrderBy(e => e.AppliedAt).Last().PreviousBody);
        }

        [Fact]
        public async Task ADryRunWritesNothingAndChangesNoState()
        {
            var (iid, title) = AddStory("login", "Original text.");
            var batch = Batch((iid, title, "5"));
            var (_, run) = await Apply(batch);
            var writesBefore = _gitlab.Http.Writes().Count();
            var applied = _gitlab.BodyOf(iid);
            _gateway.ClearAudit();

            var preview = await Rollback(run, dryRun: true);

            Assert.Equal(1, preview.Restored);
            Assert.Equal(RollbackStatus.Restore, preview.Rows[0].Status);
            Assert.Equal(writesBefore, _gitlab.Http.Writes().Count());
            Assert.Equal(applied, _gitlab.BodyOf(iid));
            Assert.Empty(_gateway.Audit);
            Assert.True(_journal.Load(run.Id)!.CanRollBack);
            Assert.Equal(ProposalDecision.Applied, _store.Load(batch.Id)!.Proposals.Single().Decision);
        }

        [Fact]
        public async Task AnItemEditedSinceTheApplyIsALeftAloneConflictUnlessForced()
        {
            var (iid, title) = AddStory("login", "Original text.");
            var batch = Batch((iid, title, "5"));
            var (_, run) = await Apply(batch);
            // a person edits the issue after Halyard wrote it
            await _provider.OpenWriter(Sandbox).UpdateAsync(iid, new WorkItemPatch { Body = "A person rewrote this." }, CancellationToken.None);

            var preview = await Rollback(run, dryRun: true);
            Assert.Equal(1, preview.Conflicts);
            Assert.Equal(RollbackStatus.Conflict, preview.Rows[0].Status);

            var result = await Rollback(run);
            Assert.Equal(1, result.Conflicts);
            Assert.Equal(0, result.Restored);
            Assert.Equal("A person rewrote this.", _gitlab.BodyOf(iid));
            Assert.True(_journal.Load(run.Id)!.CanRollBack);
            Assert.Equal(ProposalDecision.Applied, _store.Load(batch.Id)!.Proposals.Single().Decision);

            var forced = await Rollback(run, force: true);
            Assert.Equal(1, forced.Restored);
            Assert.Equal("Original text.", _gitlab.BodyOf(iid));
            Assert.Equal("forced over a later edit", _journal.Load(run.Id)!.Entries[0].RollbackNote);
        }

        [Fact]
        public async Task AnItemThatAlreadyHasTheEarlierBodyIsMarkedDoneWithoutAWrite()
        {
            var (iid, title) = AddStory("login", "Original text.");
            var (_, run) = await Apply(Batch((iid, title, "5")));
            await _provider.OpenWriter(Sandbox).UpdateAsync(iid, new WorkItemPatch { Body = "Original text." }, CancellationToken.None);
            var writes = _gitlab.Http.Writes().Count();

            var result = await Rollback(run);

            Assert.Equal(1, result.AlreadyRestored);
            Assert.Equal(0, result.Restored);
            Assert.Equal(writes, _gitlab.Http.Writes().Count());
            Assert.False(_journal.Load(run.Id)!.CanRollBack);
        }

        [Fact]
        public async Task AMissingItemIsReportedAndTheRestOfTheRunIsStillRestored()
        {
            var a = AddStory("gone", "Text A.");
            var b = AddStory("kept", "Text B.");
            var (_, run) = await Apply(Batch((a.iid, a.title, "3"), (b.iid, b.title, "5")));
            await _provider.OpenWriter(Sandbox).DeleteAsync(new WorkItem(Sandbox, a.iid), CancellationToken.None);

            var result = await Rollback(run);

            Assert.Equal(1, result.Missing);
            Assert.Equal(1, result.Restored);
            Assert.Equal("Text B.", _gitlab.BodyOf(b.iid));
            Assert.Equal(1, _journal.Load(run.Id)!.Pending);
        }

        [Fact]
        public async Task ASecondRollbackHasNothingLeftToDo()
        {
            var (iid, title) = AddStory("login");
            var (_, run) = await Apply(Batch((iid, title, "5")));
            await Rollback(run);
            var writes = _gitlab.Http.Writes().Count();
            var again = await Rollback(run);
            Assert.Equal(0, again.Restored);
            Assert.Empty(again.Rows);
            Assert.Contains("nothing left to undo", Assert.Single(again.Messages));
            Assert.Equal(writes, _gitlab.Http.Writes().Count());
        }

        [Fact]
        public async Task AnAuthFailureStopsTheRollbackAndKeepsTheRemainingEntriesUndoable()
        {
            var a = AddStory("one", "Text A.");
            var b = AddStory("two", "Text B.");
            var (_, run) = await Apply(Batch((a.iid, a.title, "3"), (b.iid, b.title, "5")));
            // the newest entry (item b) is restored first; make that write fail with 403
            _gitlab.Http.Rules.Insert(0, new FakeTransport.Rule { Method = "PUT", UrlContains = "/issues/" + b.iid, Status = 403, Body = "{\"message\":\"403 Forbidden\"}" });

            var result = await Rollback(run);

            Assert.Equal(1, result.Failed);
            Assert.Equal(0, result.Restored);
            Assert.Equal(2, _journal.Load(run.Id)!.Pending);
            Assert.Single(result.Rows); // stopped after the first 403
        }

        [Fact]
        public async Task LatestReturnsTheNewestRunThatStillHasWritesToUndo()
        {
            var a = AddStory("one", "Text A.");
            var b = AddStory("two", "Text B.");
            var (_, first) = await Apply(Batch((a.iid, a.title, "3")));
            await Task.Delay(1100); // run ids and ordering are second-resolution
            var (_, second) = await Apply(Batch((b.iid, b.title, "5")));

            Assert.Equal(second.Id, _journal.Latest(Sandbox.Key)!.Id);
            await Rollback(second);
            Assert.Equal(first.Id, _journal.Latest(Sandbox.Key)!.Id);
            await Rollback(first);
            Assert.Null(_journal.Latest(Sandbox.Key));
            Assert.Null(_journal.Latest("gitlab:999"));
            Assert.Equal(2, _journal.LoadAll().Count);
        }

        [Fact]
        public void AnUnreadableRunFileIsSkipped()
        {
            Directory.CreateDirectory(_journal.Directory);
            File.WriteAllText(Path.Combine(_journal.Directory, "apply-broken.json"), "{ not json");
            Assert.Empty(_journal.LoadAll());
            Assert.Null(_journal.Load("missing"));
        }
    }
}
