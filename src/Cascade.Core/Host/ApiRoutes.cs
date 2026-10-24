using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cascade.Core.Analysis;
using Cascade.Core.Intelligence;
using Cascade.Core.Model;
using Cascade.Core.Providers;
using Cascade.Core.Safety;
using Cascade.Core.Schema;
using Cascade.Core.Synthetic;
using Newtonsoft.Json.Linq;

namespace Cascade.Core.Host
{
    public sealed partial class CascadeHost
    {
        private void RegisterRoutes()
        {
            Router.Get("/api/health", r => Task.FromResult(Json.Ok(new { ok = true, version = Version })));
            Router.Get("/api/status", r => Task.FromResult(Json.Ok(StatusPayload())));
            Router.Post("/api/session/stop", StopSession);

            Router.Get("/api/config", r => Task.FromResult(Json.Ok(Settings.ToPublicDictionary())));
            Router.Post("/api/config", UpdateConfig);
            Router.Get("/api/schema", r => Task.FromResult(Json.Ok(SchemaPayload())));

            Router.Post("/api/provider/test", TestProvider);
            Router.Get("/api/provider/repositories", ListRepositories);
            Router.Get("/api/provider/labels", ListLabels);

            Router.Post("/api/sync", StartSync);
            Router.Get("/api/snapshot", r => Task.FromResult(Json.Ok(SnapshotPayload())));
            Router.Get("/api/snapshots", r => Task.FromResult(Json.Ok(Snapshots.List().Select(s => new { s.Id, s.Repository, provider = s.Provider, s.TakenAt, count = s.ItemCount, s.LabelFilter }))));
            Router.Get("/api/review", r => Task.FromResult(Json.Ok(ReviewPayload(Query(r, "label")))));
            Router.Get("/api/hierarchy", r => Task.FromResult(Json.Ok(HierarchyPayload(Query(r, "label")))));
            Router.Get("/api/items/{number}", GetItem);

            Router.Get("/api/sandbox/preview", PreviewCatalog);
            Router.Post("/api/sandbox/seed", StartSeed);
            Router.Post("/api/sandbox/teardown", StartTeardown);
            Router.Get("/api/sandbox/runs", r => Task.FromResult(Json.Ok(SandboxSeeder.ReadManifests(Settings.SeedsDirectory))));

            Router.Post("/api/intelligence/estimate", StartEstimate);
            Router.Post("/api/intelligence/parents", ParentProposals);
            Router.Post("/api/intelligence/refine", StartRefine);
            Router.Post("/api/intelligence/metadata", MetadataProposals);
            Router.Get("/api/proposals", r => Task.FromResult(Json.Ok(Proposals.LoadAll().Select(BatchSummary))));
            Router.Post("/api/proposals/manual", ManualProposal);
            Router.Get("/api/proposals/{batchId}", GetBatch);
            Router.Post("/api/proposals/{batchId}/decide", DecideProposals);
            Router.Post("/api/proposals/{batchId}/apply", StartApply);
            Router.Post("/api/proposals/{batchId}/delete", DeleteBatch);
            Router.Post("/api/proposals/apply-all", StartApplyAll);
            Router.Get("/api/proposals/{batchId}/{proposalId}/preview", PreviewProposal);

            Router.Get("/api/jobs", r => Task.FromResult(Json.Ok(Jobs.List())));
            Router.Get("/api/jobs/{id}", r =>
            {
                var job = Jobs.Get(r.RouteValues["id"]);
                if (job == null) throw new NotFoundException("No such job.");
                return Task.FromResult(Json.Ok(job));
            });
            Router.Post("/api/jobs/{id}/cancel", r => Task.FromResult(Json.Ok(new { cancelled = Jobs.Cancel(r.RouteValues["id"]) })));

            Router.Get("/api/diagnostics", r =>
            {
                int count;
                if (!int.TryParse(Query(r, "count"), out count) || count <= 0) count = 200;
                return Task.FromResult(Json.Ok(new { file = Log.FilePath, entries = Log.Recent(Math.Min(count, 2000), Query(r, "level")) }));
            });
            Router.Get("/api/writes", r => Task.FromResult(Json.Ok(new { policy = PolicyPayload(), entries = Gateway.Audit.Reverse().Take(500) })));
            Router.Post("/api/llm/test", TestLlm);
        }

        private static string? Query(HttpRequest r, string name)
        {
            string? v;
            return r.QueryValues.TryGetValue(name, out v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        }

        // ---- status & config ----

        private object StatusPayload()
        {
            var snap = Snapshots.Current;
            return new
            {
                version = Version,
                startedAt = StartedAt,
                uptimeSeconds = (int)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
                port = Port,
                config = Settings.ToPublicDictionary(),
                policy = PolicyPayload(),
                provider = new
                {
                    kind = Settings.Provider.Kind,
                    configured = Settings.Provider.HasToken && (Settings.Provider.Source != null || Settings.Provider.Sandbox != null),
                    identity = _identity,
                    error = _providerError,
                    capabilities = _provider == null ? null : _provider.Capabilities,
                },
                snapshot = snap == null ? null : new { snap.Id, snap.Repository, provider = snap.Provider, snap.TakenAt, count = snap.ItemCount, ageSeconds = (int)snap.Age.TotalSeconds, snap.LabelFilter },
                jobs = new { running = Jobs.List().Count(j => j.State == JobState.Running || j.State == JobState.Queued) },
                launcher = Options.LauncherState,
                webRoot = Options.WebRoot ?? Settings.WebRootDirectory,
                proposals = Proposals.LoadAll().Count,
            };
        }

        private object PolicyPayload()
        {
            var p = Gateway.Policy;
            return new
            {
                mode = p.Mode,
                sandbox = p.SandboxTargets.Select(t => t.Key).ToList(),
                production = p.ProductionTarget == null ? null : p.ProductionTarget.Key,
                @protected = p.ProtectedTargets.Select(t => t.Key).ToList(),
                allowProduction = p.AllowProduction,
                deleteRequiresAnyLabel = p.DeleteRequiresAnyLabel,
                description = p.Describe(),
            };
        }

        private object SchemaPayload()
        {
            return new
            {
                types = WorkItemTypes.Classified.Select(t => new { token = WorkItemTypes.ToToken(t), label = WorkItemTypes.ToLabel(t), depth = WorkItemTypes.Depth(t), expectedParent = WorkItemTypes.ExpectedParent(t) == null ? null : WorkItemTypes.ToToken(WorkItemTypes.ExpectedParent(t)!.Value) }),
                requiredMeta = Taxonomy.RequiredMeta.ToDictionary(kv => WorkItemTypes.ToToken(kv.Key), kv => kv.Value),
                themes = Taxonomy.Themes,
                deficiencyRules = Deficiencies.Rules.Select(r => new { r.Code, appliesTo = r.AppliesTo == null ? null : WorkItemTypes.ToToken(r.AppliesTo.Value), r.Explanation }),
                sizing = new { points = Sizing.StoryPointScale, effort = Sizing.EffortScale, tbd = Sizing.Tbd },
                labels = new { managed = Labels.Managed, synthetic = Labels.Synthetic, runPrefix = Labels.RunPrefix, reviewQueue = Labels.ReviewQueue },
                metaFence = CascadeMeta.Fence,
            };
        }

        private Task<HttpResponse> StopSession(HttpRequest r)
        {
            if (!string.Equals(r.Header("X-Cascade-Intent"), "end-session", StringComparison.Ordinal))
                return Task.FromResult(Json.Error(403, "INTENT_REQUIRED", "Ending the session requires the X-Cascade-Intent header.", r.RequestId));
            Log.Info("SESSION_STOP_REQUESTED", "End session requested from the browser.");
            Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); Stop(); });
            return Task.FromResult(Json.Ok(new { stopping = true }));
        }

        private Task<HttpResponse> UpdateConfig(HttpRequest r)
        {
            var body = Json.Body(r);
            var p = Settings.Provider;
            var changed = new List<string>();
            foreach (var locked in new[] { "provider", "baseUrl", "sourceRepository", "sandboxRepository", "writeMode", "allowProduction" })
            {
                if (body[locked] != null)
                    throw new BadRequestException(locked + " can only be set in config/cascade.env (then relaunch). The browser cannot change where Cascade reads, writes or sends tokens.");
            }
            if (body["labelFilter"] != null) { var lf = Json.Read<string>(body, "labelFilter"); Settings.LabelFilter = string.IsNullOrWhiteSpace(lf) ? null : lf!.Trim(); changed.Add("labelFilter"); }
            var llmProfile = Json.Read<string>(body, "llmProfile");
            if (llmProfile != null)
            {
                var parsed = Llm.LlmSettings.ParseProfile(llmProfile);
                if (parsed == null) throw new BadRequestException("llmProfile must be auto, openai or databricks.");
                Settings.Llm.Profile = parsed.Value;
                changed.Add("llmProfile");
            }
            // The OpenAI-compatible API key and model may be entered in the browser. The key stays in memory;
            // the endpoint URL is deliberately not settable here, so the key can only ever go where
            // config/cascade.env points. "remember" writes both into the git-ignored env file on this machine.
            var persist = new Dictionary<string, string?>();
            if (body["llmToken"] != null)
            {
                var token = Llm.LlmSettings.Clean(Json.Read<string>(body, "llmToken"));
                if (token != null && (token.Length < 8 || token.Length > 4096)) throw new BadRequestException("llmToken does not look like an API key.");
                Settings.Llm.OpenAiToken = token;
                changed.Add(token == null ? "llmToken (cleared)" : "llmToken");
                persist["LLM_TOKEN"] = token;
                if (token == null) persist["OPENAI_API_KEY"] = null;
            }
            if (body["llmModel"] != null)
            {
                var model = (Json.Read<string>(body, "llmModel") ?? string.Empty).Trim();
                if (model.Length > 200 || model.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0) throw new BadRequestException("llmModel is not a valid model name.");
                Settings.Llm.OpenAiModel = model.Length == 0 ? null : model;
                changed.Add("llmModel");
                persist["LLM_MODEL"] = model.Length == 0 ? null : model;
            }
            var remembered = false;
            if (Json.Read<bool?>(body, "remember") == true && persist.Count > 0)
            {
                Config.CascadeSettings.UpsertEnvFile(Settings.EnvFilePath, persist);
                remembered = true;
                Log.Info("CONFIG_PERSISTED", string.Join(", ", persist.Keys) + " written to " + Settings.EnvFilePath);
            }
            var delay = Json.Read<int?>(body, "writeDelayMs");
            if (delay != null) { p.WriteDelayMs = delay.Value; changed.Add("writeDelayMs"); }
            if (changed.Count > 0)
            {
                ResetProvider();
                Log.Info("CONFIG_UPDATED", string.Join(", ", changed));
            }
            return Task.FromResult(Json.Ok(new { changed, remembered, config = Settings.ToPublicDictionary(), policy = PolicyPayload() }));
        }

        // ---- provider ----

        private async Task<HttpResponse> TestProvider(HttpRequest r)
        {
            var provider = GetProvider();
            try
            {
                _identity = await provider.TestConnectionAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                _providerError = null;
                Log.Info("PROVIDER_CONNECTED", provider.Kind + " as " + _identity.Login);
                return Json.Ok(new { connected = true, identity = _identity, capabilities = provider.Capabilities, repository = provider.Repository.Key });
            }
            catch (ProviderHttpException ex)
            {
                _providerError = ex.Code + ": " + ex.Message;
                Log.Warn("PROVIDER_TEST_FAILED", _providerError);
                return Json.Ok(new { connected = false, code = ex.Code, error = ex.Message });
            }
        }

        private async Task<HttpResponse> ListRepositories(HttpRequest r)
        {
            var provider = GetProvider();
            var repos = await provider.ListRepositoriesAsync(100, System.Threading.CancellationToken.None).ConfigureAwait(false);
            return Json.Ok(repos.Select(x => new { x.Identifier, x.DisplayName, x.Url, x.Key }));
        }

        private async Task<HttpResponse> ListLabels(HttpRequest r)
        {
            var repo = ResolveRepository(Query(r, "repository"));
            var reader = GetProvider().ForRepository(repo);
            var labels = await reader.ListLabelsAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
            return Json.Ok(new { repository = repo.Key, labels = labels.OrderBy(l => l, StringComparer.OrdinalIgnoreCase) });
        }

        // ---- snapshot & analysis ----

        private Task<HttpResponse> StartSync(HttpRequest r)
        {
            var body = Json.Body(r);
            var repo = ResolveRepository(Json.Read<string>(body, "repository"));
            var labels = Json.Read<List<string>>(body, "labels") ?? new List<string>();
            var max = Json.Read<int?>(body, "max") ?? Settings.MaxSyncItems;
            var includeClosed = Json.Read<bool?>(body, "includeClosed") ?? true;
            var provider = GetProvider();
            var job = Jobs.Start("sync", async ctx =>
            {
                var reader = provider.ForRepository(repo);
                var query = new WorkItemQuery { MaxItems = max, State = includeClosed ? (WorkItemState?)null : WorkItemState.Open };
                query.Labels.AddRange(labels.Where(l => !string.IsNullOrWhiteSpace(l)));
                ctx.Report(0, 0, "Fetching work items from " + repo.Key);
                var progress = new Progress<string>(m => ctx.Report(0, 0, m));
                var items = await reader.ListWorkItemsAsync(query, progress, ctx.Cancellation).ConfigureAwait(false);
                var snap = Snapshots.Save(repo, items, labels.Count == 0 ? null : string.Join(",", labels));
                ctx.Report(items.Count, items.Count, "Snapshot " + snap.Id + " saved with " + items.Count + " items");
                return (object?)new { snap.Id, count = items.Count, snap.TakenAt, repository = repo.Key };
            });
            return Task.FromResult(Json.Accepted(job));
        }

        private object? SnapshotPayload()
        {
            var snap = Snapshots.Current;
            if (snap == null) return null;
            return new
            {
                snap.Id, snap.Repository, provider = snap.Provider, snap.TakenAt, ageSeconds = (int)snap.Age.TotalSeconds, snap.LabelFilter, count = snap.ItemCount,
                labels = snap.Items.SelectMany(i => i.Labels).GroupBy(l => l, StringComparer.OrdinalIgnoreCase).Select(g => new { label = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ThenBy(x => x.label),
                items = snap.Items.Select(ItemSummary),
            };
        }

        private static object ItemSummary(WorkItem i)
        {
            return new
            {
                key = i.Key, number = i.Number, title = i.Title, type = WorkItemTypes.ToToken(i.Type), canonicalId = i.CanonicalId, parentId = i.ParentId, themeId = i.ThemeId,
                state = i.State, labels = i.Labels, url = i.Url, updatedAt = i.UpdatedAt, hasMeta = i.HasMeta,
                points = i.Meta == null ? null : i.Meta.Points, effort = i.Meta == null ? null : i.Meta.Effort, sprint = i.Meta == null ? null : i.Meta.Sprint,
            };
        }

        private object ReviewPayload(string? label)
        {
            List<WorkItem> all;
            var visible = VisibleItems(label ?? Settings.LabelFilter, out all);
            var report = Review.Run(visible, Taxonomy, label ?? Settings.LabelFilter, all);
            var snap = Snapshots.Current;
            return new
            {
                snapshot = snap == null ? null : new { snap.Id, snap.TakenAt, snap.Repository },
                labelFilter = report.LabelFilter,
                generatedAt = report.GeneratedAt,
                summary = report.Summary,
                audit = new
                {
                    report.Audit.VisibleCount, report.Audit.SourceCount, report.Audit.ClassifiedCount, report.Audit.ErrorCount, report.Audit.WarningCount, report.Audit.InfoCount, report.Audit.AffectedCount,
                    findings = report.Audit.Findings.Select(f => new { f.Key, f.Number, f.Title, f.Url, type = WorkItemTypes.ToToken(f.Type), f.CanonicalId, f.Code, f.Severity, f.Explanation }),
                },
                items = report.Items.Select(i => new
                {
                    i.Key, i.Number, i.CanonicalId, i.Title, i.Url, type = WorkItemTypes.ToToken(i.Type), i.State, i.Labels, i.ParentId, i.ResolvedParentKey, i.ThemeId, i.HasMeta,
                    i.Points, i.Effort, i.Sprint, i.Area, i.UpdatedAt, i.MissingMeta, i.Deficiencies, findings = i.Findings.Select(f => new { f.Code, f.Severity, f.Explanation }), i.NeedsReview,
                }),
                parentReviews = report.ParentReviews.Select(p => new { p.Key, p.CanonicalId, p.Title, type = WorkItemTypes.ToToken(p.Type), p.DeclaredParent, expectedParentType = p.ExpectedParentType == null ? null : WorkItemTypes.ToToken(p.ExpectedParentType.Value), candidates = p.Candidates.Select(c => new { c.Key, c.CanonicalId, c.Title, type = WorkItemTypes.ToToken(c.Type), c.Score, c.Evidence }) }),
            };
        }

        private object HierarchyPayload(string? label)
        {
            List<WorkItem> all;
            var visible = VisibleItems(label ?? Settings.LabelFilter, out all);
            var audit = TopologyAudit.Run(visible, all, label);
            var forest = HierarchyTree.Build(visible, audit.ParentEdges);
            Func<HierarchyNode, object>? toJson = null;
            toJson = n => new
            {
                key = n.Item.Key, number = n.Item.Number, canonicalId = n.Item.CanonicalId, type = WorkItemTypes.ToToken(n.Item.Type), title = Descriptions.ShortName(n.Item.Title), fullTitle = n.Item.Title,
                url = n.Item.Url, state = n.Item.State, points = n.Item.Meta == null ? null : n.Item.Meta.Points, effort = n.Item.Meta == null ? null : n.Item.Meta.Effort, sprint = n.Item.Meta == null ? null : n.Item.Meta.Sprint,
                rolledUpPoints = n.RolledUpPoints(), descendantCount = n.Descendants().Count(), children = n.Children.Select(c => toJson!(c)).ToList(),
            };
            return new
            {
                labelFilter = label ?? Settings.LabelFilter,
                total = visible.Count,
                roots = forest.Roots.Select(toJson),
                orphans = forest.Orphans.Select(toJson),
                edges = audit.ParentEdges.Select(kv => new { child = kv.Key, parent = kv.Value }),
            };
        }

        private Task<HttpResponse> GetItem(HttpRequest r)
        {
            long number;
            if (!long.TryParse(r.RouteValues["number"], out number)) throw new BadRequestException("number must be an integer.");
            var snap = Snapshots.Current;
            var item = snap == null ? null : snap.Items.FirstOrDefault(i => i.Number == number);
            if (item == null) throw new NotFoundException("Item #" + number + " is not in the current snapshot.");
            var all = snap!.Items;
            var audit = TopologyAudit.Run(all, all, null);
            return Task.FromResult(Json.Ok(new
            {
                key = item.Key, number = item.Number, title = item.Title, shortName = Descriptions.ShortName(item.Title), type = WorkItemTypes.ToToken(item.Type), canonicalId = item.CanonicalId,
                item.State, item.Labels, item.Assignees, item.Url, item.Milestone, item.CreatedAt, item.UpdatedAt, body = item.Body, narrative = Descriptions.Narrative(item.Body),
                meta = item.Meta == null ? null : item.Meta.Entries.Select(e => new { key = e.Key, value = e.Value }),
                missingMeta = Taxonomy.MissingKeys(item.Type, item.Meta), deficiencies = Deficiencies.Evaluate(item).Select(c => { var rule = Deficiencies.Find(c); return new { code = c, rule = rule == null ? null : new { rule.Code, appliesTo = rule.AppliesTo == null ? null : WorkItemTypes.ToToken(rule.AppliesTo.Value), rule.Explanation } }; }),
                findings = audit.Findings.Where(f => f.Key == item.Key).Select(f => new { f.Code, f.Severity, f.Explanation }),
                parentKey = audit.ParentEdges.ContainsKey(item.Key) ? audit.ParentEdges[item.Key] : null,
                children = audit.ParentEdges.Where(kv => kv.Value == item.Key).Select(kv => kv.Key),
            }));
        }

        // ---- sandbox ----

        private Task<HttpResponse> PreviewCatalog(HttpRequest r)
        {
            int total, seed;
            if (!int.TryParse(Query(r, "total"), out total) || total < 5 || total > 2000) total = 100;
            if (!int.TryParse(Query(r, "seed"), out seed)) seed = 20261024;
            var options = SyntheticOptions.ForTotal(total, seed);
            options.IncludeDefects = Query(r, "defects") == "true";
            options.DefectRate = ReadRate(Query(r, "rate")) ?? options.DefectRate;
            var items = SyntheticCatalog.Generate(options);
            return Task.FromResult(Json.Ok(new
            {
                options,
                total = items.Count,
                counts = items.GroupBy(i => WorkItemTypes.ToToken(i.Type)).ToDictionary(g => g.Key, g => g.Count()),
                complete = items.Count(i => !i.NeedsHumanReview),
                needsReview = items.Count(i => i.NeedsHumanReview),
                sample = items.Take(12).Select(i => new { i.Id, type = WorkItemTypes.ToToken(i.Type), i.Title, i.ParentId, labels = i.Labels, points = i.Meta.Points, effort = i.Meta.Effort }),
                defects = items.Where(i => i.Defects.Count > 0).Select(i => new { i.Id, type = WorkItemTypes.ToToken(i.Type), i.Defects, expectedCodes = i.ExpectedCodes }),
                defectKinds = SyntheticCatalog.DefectKinds,
            }));
        }

        /// <summary>A defect share between 0 and 0.5, given as a fraction (0.2) or a percentage (20).</summary>
        private static double? ReadRate(string? text)
        {
            double v;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return null;
            if (v > 1) v = v / 100.0;
            if (v < 0 || v > 0.5) throw new BadRequestException("defectRate must be between 0 and 0.5 (0-50%).");
            return v;
        }

        private Task<HttpResponse> StartSeed(HttpRequest r)
        {
            var body = Json.Body(r);
            var total = Json.Read<int?>(body, "total") ?? 100;
            if (total < 5 || total > 2000) throw new BadRequestException("total must be between 5 and 2000.");
            var seed = Json.Read<int?>(body, "seed") ?? 20261024;
            var options = SyntheticOptions.ForTotal(total, seed);
            options.IncludeDefects = Json.Read<bool?>(body, "includeDefects") ?? false;
            var rate = Json.Read<double?>(body, "defectRate");
            if (rate != null) options.DefectRate = ReadRate(rate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? options.DefectRate;
            var seedOptions = new SeedOptions
            {
                RunId = Json.Read<string>(body, "runId") ?? SeedOptions.NewRunId(),
                NativeLinks = Json.Read<bool?>(body, "nativeLinks") ?? true,
                AddReviewLabel = Json.Read<bool?>(body, "addReviewLabel") ?? true,
                ManifestDirectory = Settings.SeedsDirectory,
            };
            if (!System.Text.RegularExpressions.Regex.IsMatch(seedOptions.RunId, @"^[A-Za-z0-9][A-Za-z0-9\-_\.]{0,40}$")) throw new BadRequestException("runId may only contain letters, digits, '-', '_' and '.'.");
            var sandbox = SandboxOrThrow();
            var provider = GetProvider();
            Gateway.Policy.Assert(sandbox, new WriteIntent("seed-synthetic", "preflight", seedOptions.RunId));
            var catalog = SyntheticCatalog.Generate(options);
            var job = Jobs.Start("seed", async ctx =>
            {
                var progress = new Progress<SeedProgress>(p => ctx.Report(p.Done, p.Total, p.Message));
                var manifest = await SandboxSeeder.SeedAsync(provider, Gateway, sandbox, catalog, seedOptions, progress, ctx.Cancellation).ConfigureAwait(false);
                return (object?)manifest;
            });
            return Task.FromResult(Json.Accepted(new { job, runId = seedOptions.RunId, total = catalog.Count, target = sandbox.Key }));
        }

        private Task<HttpResponse> StartTeardown(HttpRequest r)
        {
            var body = Json.Body(r);
            var scopeText = (Json.Read<string>(body, "scope") ?? "run").Trim().ToLowerInvariant();
            var options = new TeardownOptions
            {
                RunId = Json.Read<string>(body, "runId"),
                ConfirmText = Json.Read<string>(body, "confirmText"),
                DryRun = Json.Read<bool?>(body, "dryRun") ?? false,
            };
            switch (scopeText)
            {
                case "run": options.Scope = TeardownScope.Run; if (string.IsNullOrWhiteSpace(options.RunId)) throw new BadRequestException("runId is required for scope 'run'."); break;
                case "allsynthetic": options.Scope = TeardownScope.AllSynthetic; break;
                case "everything": options.Scope = TeardownScope.Everything; break;
                default: throw new BadRequestException("scope must be run, allSynthetic or everything.");
            }
            var sandbox = SandboxOrThrow();
            var provider = GetProvider();
            if (options.Scope == TeardownScope.Everything && !string.Equals(options.ConfirmText, TeardownOptions.ConfirmationPhrase(sandbox), StringComparison.Ordinal))
                throw new WriteBlockedException("Type '" + TeardownOptions.ConfirmationPhrase(sandbox) + "' to confirm deleting every item in the sandbox.", sandbox);
            Gateway.Policy.Assert(sandbox, new WriteIntent("teardown-synthetic", "preflight", options.RunId));
            var job = Jobs.Start("teardown", async ctx =>
            {
                var progress = new Progress<SeedProgress>(p => ctx.Report(p.Done, p.Total, p.Message));
                var result = await SandboxTeardown.RunAsync(provider, Gateway, sandbox, options, progress, ctx.Cancellation).ConfigureAwait(false);
                return (object?)result;
            });
            return Task.FromResult(Json.Accepted(new { job, scope = options.Scope, runId = options.RunId, dryRun = options.DryRun, target = sandbox.Key }));
        }

        // ---- intelligence ----

        private Task<HttpResponse> StartEstimate(HttpRequest r)
        {
            var body = Json.Body(r);
            var kindText = (Json.Read<string>(body, "kind") ?? "storyPoints").Trim().ToLowerInvariant();
            var kind = kindText == "featureeffort" || kindText == "effort" ? ProposalKind.FeatureEffort : ProposalKind.StoryPoints;
            var options = new EstimateOptions
            {
                Kind = kind,
                OnlyUnsized = Json.Read<bool?>(body, "onlyUnsized") ?? true,
                MaxItems = Math.Min(Math.Max(Json.Read<int?>(body, "maxItems") ?? 40, 1), 200),
                RequireDefinition = Json.Read<bool?>(body, "requireDefinition") ?? true,
            };
            options.OnlyNumbers = ReadNumbers(body);
            var label = Json.Read<string>(body, "label") ?? Settings.LabelFilter;
            var llm = CreateLlm(Json.Read<string>(body, "profile"));
            var snap = Snapshots.Current;
            if (snap == null) throw new BadRequestException("Synchronize a repository first; proposals are made from the current snapshot.");
            List<WorkItem> all;
            var items = VisibleItems(label, out all);
            var target = new RepositoryRef(snap.Provider, snap.Repository);
            var job = Jobs.Start("estimate", async ctx =>
            {
                var progress = new Progress<string>(m => ctx.Report(0, 0, m));
                var batch = await Estimator.RunAsync(items, llm, options, target, progress, ctx.Cancellation).ConfigureAwait(false);
                Proposals.Save(batch);
                ctx.Report(batch.Proposals.Count, batch.Proposals.Count, batch.Proposals.Count + " proposals saved in batch " + batch.Id);
                return (object?)BatchSummary(batch);
            });
            return Task.FromResult(Json.Accepted(new { job, profile = llm.Profile, model = llm.Model, candidates = items.Count }));
        }

        private Task<HttpResponse> ParentProposals(HttpRequest r)
        {
            var body = Json.Body(r);
            var label = Json.Read<string>(body, "label") ?? Settings.LabelFilter;
            var minimumScore = Json.Read<double?>(body, "minimumScore") ?? 2.0;
            var snap = Snapshots.Current;
            if (snap == null) throw new BadRequestException("Synchronize a repository first.");
            List<WorkItem> all;
            var items = VisibleItems(label, out all);
            var report = Review.Run(items, Taxonomy, label, all);
            var batch = Estimator.ParentProposals(report, new RepositoryRef(snap.Provider, snap.Repository), minimumScore, ReadNumbers(body));
            Proposals.Save(batch);
            return Task.FromResult(Json.Ok(batch));
        }

        /// <summary>Optional <c>numbers: [..]</c> selection from a bulk action; null means every eligible item.</summary>
        private static HashSet<long>? ReadNumbers(JObject body)
        {
            var list = Json.Read<List<long>>(body, "numbers");
            if (list == null || list.Count == 0) return null;
            if (list.Count > 500) throw new BadRequestException("Select at most 500 items at a time.");
            return new HashSet<long>(list);
        }

        private Task<HttpResponse> StartRefine(HttpRequest r)
        {
            var body = Json.Body(r);
            var options = new RefineOptions
            {
                MaxItems = Math.Min(Math.Max(Json.Read<int?>(body, "maxItems") ?? 10, 1), 100),
                OnlyNumbers = ReadNumbers(body),
            };
            var label = Json.Read<string>(body, "label") ?? Settings.LabelFilter;
            var llm = CreateLlm(Json.Read<string>(body, "profile"));
            var snap = Snapshots.Current;
            if (snap == null) throw new BadRequestException("Synchronize a repository first; proposals are made from the current snapshot.");
            List<WorkItem> all;
            var items = VisibleItems(label, out all);
            var report = Review.Run(items, Taxonomy, label, all);
            var candidates = Refiner.Candidates(report, options).Count;
            var target = new RepositoryRef(snap.Provider, snap.Repository);
            var job = Jobs.Start("refine", async ctx =>
            {
                var progress = new Progress<string>(m => ctx.Report(0, 0, m));
                var batch = await Refiner.RunAsync(items, report, llm, options, target, progress, ctx.Cancellation).ConfigureAwait(false);
                Proposals.Save(batch);
                ctx.Report(batch.Proposals.Count, batch.Proposals.Count, batch.Proposals.Count + " refinement proposals saved in batch " + batch.Id);
                return (object?)BatchSummary(batch);
            });
            return Task.FromResult(Json.Accepted(new { job, profile = llm.Profile, model = llm.Model, candidates, sending = Math.Min(candidates, options.MaxItems) }));
        }

        private Task<HttpResponse> MetadataProposals(HttpRequest r)
        {
            var body = Json.Body(r);
            var label = Json.Read<string>(body, "label") ?? Settings.LabelFilter;
            var snap = Snapshots.Current;
            if (snap == null) throw new BadRequestException("Synchronize a repository first.");
            List<WorkItem> all;
            var items = VisibleItems(label, out all);
            var report = Review.Run(items, Taxonomy, label, all);
            var batch = Refiner.MetadataProposals(items, report, Taxonomy, new RepositoryRef(snap.Provider, snap.Repository), ReadNumbers(body));
            Proposals.Save(batch);
            return Task.FromResult(Json.Ok(batch));
        }

        private Task<HttpResponse> ManualProposal(HttpRequest r)
        {
            var body = Json.Body(r);
            var number = Json.Read<long?>(body, "number");
            if (number == null) throw new BadRequestException("number is required.");
            var kindText = (Json.Read<string>(body, "kind") ?? string.Empty).Trim().ToLowerInvariant();
            ProposalKind kind;
            switch (kindText)
            {
                case "points": case "storypoints": kind = ProposalKind.StoryPoints; break;
                case "effort": case "featureeffort": kind = ProposalKind.FeatureEffort; break;
                case "parent": case "parentlink": kind = ProposalKind.ParentLink; break;
                default: throw new BadRequestException("kind must be points, effort or parent.");
            }
            var value = (Json.Read<string>(body, "value") ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 120) throw new BadRequestException("value is required.");
            var snap = Snapshots.Current;
            if (snap == null) throw new BadRequestException("Synchronize a repository first.");
            var item = snap.Items.FirstOrDefault(i => i.Number == number.Value);
            if (item == null) throw new NotFoundException("Item #" + number + " is not in the current snapshot.");
            ProposalBatch batch;
            try { batch = Estimator.Manual(item, kind, value, Json.Read<string>(body, "note"), new RepositoryRef(snap.Provider, snap.Repository), snap.Items); }
            catch (ArgumentException ex) { throw new BadRequestException(ex.Message); }
            Proposals.Save(batch);
            Log.Info("MANUAL_PROPOSAL", kind + " " + item.CanonicalId + " -> " + batch.Proposals[0].ProposedValue + " (batch " + batch.Id + ")");
            return Task.FromResult(Json.Ok(batch));
        }

        /// <summary>What applying one proposal would do to the snapshot copy of its item: current and proposed body side by side.</summary>
        private Task<HttpResponse> PreviewProposal(HttpRequest r)
        {
            var batch = Proposals.Load(r.RouteValues["batchId"]);
            if (batch == null) throw new NotFoundException("No such proposal batch.");
            var proposal = batch.Proposals.FirstOrDefault(p => p.Id == r.RouteValues["proposalId"]);
            if (proposal == null) throw new NotFoundException("No such proposal.");
            var snap = Snapshots.Current;
            var item = snap == null ? null : snap.Items.FirstOrDefault(i => i.Number == proposal.ItemNumber);
            if (item == null) throw new NotFoundException("Item #" + proposal.ItemNumber + " is not in the current snapshot; synchronize again.");
            var projection = ProposalApplier.Project(item, proposal);
            var after = new WorkItem(item.Repository, item.Number) { Title = item.Title, Body = projection.Body };
            after.Labels.AddRange(item.Labels);
            return Task.FromResult(Json.Ok(new
            {
                proposalId = proposal.Id, batchId = batch.Id, number = item.Number, title = item.Title, type = WorkItemTypes.ToToken(item.Type),
                current = new { body = item.Body, narrative = Descriptions.Narrative(item.Body), meta = item.Meta == null ? null : item.Meta.Entries.Select(e => new { key = e.Key, value = e.Value }), deficiencies = Deficiencies.Evaluate(item) },
                proposed = new { body = projection.Body, narrative = Descriptions.Narrative(projection.Body), meta = projection.Meta.Entries.Select(e => new { key = e.Key, value = e.Value }), deficiencies = Deficiencies.Evaluate(after) },
                unchanged = projection.Body == item.Body,
                snapshotStale = snap!.Age.TotalMinutes > 30,
            }));
        }

        private static object BatchSummary(ProposalBatch b)
        {
            return new
            {
                b.Id, b.Kind, b.Target, b.CreatedAt, b.Source, b.Considered, b.Skipped, b.Warnings,
                total = b.Proposals.Count,
                pending = b.Proposals.Count(p => p.Decision == ProposalDecision.Pending),
                approved = b.Proposals.Count(p => p.Decision == ProposalDecision.Approved),
                rejected = b.Proposals.Count(p => p.Decision == ProposalDecision.Rejected),
                applied = b.Proposals.Count(p => p.Decision == ProposalDecision.Applied),
                failed = b.Proposals.Count(p => p.Decision == ProposalDecision.Failed),
            };
        }

        private Task<HttpResponse> GetBatch(HttpRequest r)
        {
            var batch = Proposals.Load(r.RouteValues["batchId"]);
            if (batch == null) throw new NotFoundException("No such proposal batch.");
            return Task.FromResult(Json.Ok(batch));
        }

        private Task<HttpResponse> DecideProposals(HttpRequest r)
        {
            var batchId = r.RouteValues["batchId"];
            var body = Json.Body(r);
            var decisionText = (Json.Read<string>(body, "decision") ?? string.Empty).Trim().ToLowerInvariant();
            ProposalDecision decision;
            if (decisionText == "approved" || decisionText == "approve") decision = ProposalDecision.Approved;
            else if (decisionText == "rejected" || decisionText == "reject") decision = ProposalDecision.Rejected;
            else if (decisionText == "pending" || decisionText == "reset") decision = ProposalDecision.Pending;
            else throw new BadRequestException("decision must be approved, rejected or pending.");
            if (Json.Read<bool?>(body, "all") == true)
            {
                var count = Proposals.DecideAll(batchId, decision, Json.Read<string>(body, "minimumConfidence"));
                return Task.FromResult(Json.Ok(new { batchId, decision, count }));
            }
            var proposalId = Json.Read<string>(body, "proposalId");
            if (string.IsNullOrWhiteSpace(proposalId)) throw new BadRequestException("proposalId (or all=true) is required.");
            var p = Proposals.Decide(batchId, proposalId!, decision, Json.Read<string>(body, "note"));
            if (p == null) throw new NotFoundException("No such proposal.");
            return Task.FromResult(Json.Ok(p));
        }

        private Task<HttpResponse> StartApply(HttpRequest r)
        {
            var batch = Proposals.Load(r.RouteValues["batchId"]);
            if (batch == null) throw new NotFoundException("No such proposal batch.");
            var sandbox = SandboxOrThrow();
            if (!string.Equals(batch.Target, sandbox.Key, StringComparison.OrdinalIgnoreCase))
                throw new WriteBlockedException("APPLY BLOCKED: batch " + batch.Id + " was made from " + batch.Target + ", not the sandbox " + sandbox.Key + ". Synchronize the sandbox and run the analysis again.", sandbox);
            var provider = GetProvider();
            Gateway.Policy.Assert(sandbox, new WriteIntent("apply-proposals", "preflight", batch.Id));
            var job = Jobs.Start("apply", async ctx =>
            {
                var progress = new Progress<string>(m => ctx.Report(0, 0, m));
                var result = await ProposalApplier.ApplyAsync(batch, Proposals, provider, Gateway, sandbox, progress, ctx.Cancellation).ConfigureAwait(false);
                return (object?)result;
            });
            return Task.FromResult(Json.Accepted(new { job, batchId = batch.Id, approved = batch.Proposals.Count(p => p.Decision == ProposalDecision.Approved) }));
        }

        private Task<HttpResponse> DeleteBatch(HttpRequest r)
        {
            var batchId = r.RouteValues["batchId"];
            var batch = Proposals.Load(batchId);
            if (batch == null) throw new NotFoundException("No such proposal batch.");
            // Deleting is a local file operation; the sandbox is untouched. Applied proposals stay on the provider.
            Proposals.Delete(batchId);
            Log.Info("BATCH_DELETED", batch.Id + " (" + batch.Kind + ", " + batch.Proposals.Count + " proposals, " + batch.Proposals.Count(p => p.Decision == ProposalDecision.Applied) + " already applied)");
            return Task.FromResult(Json.Ok(new { deleted = true, batchId }));
        }

        /// <summary>Applies every approved proposal in every batch made from the sandbox, oldest batch first, as one job.</summary>
        private Task<HttpResponse> StartApplyAll(HttpRequest r)
        {
            var sandbox = SandboxOrThrow();
            var provider = GetProvider();
            var batches = Proposals.LoadAll().Where(b => string.Equals(b.Target, sandbox.Key, StringComparison.OrdinalIgnoreCase) && b.Proposals.Any(p => p.Decision == ProposalDecision.Approved)).OrderBy(b => b.CreatedAt).ToList();
            if (batches.Count == 0) throw new BadRequestException("No approved proposals are waiting in batches made from the sandbox.");
            Gateway.Policy.Assert(sandbox, new WriteIntent("apply-proposals", "preflight", null));
            var approved = batches.Sum(b => b.Proposals.Count(p => p.Decision == ProposalDecision.Approved));
            var job = Jobs.Start("apply", async ctx =>
            {
                var totals = new ApplyResult { BatchId = "all" };
                var done = 0;
                foreach (var batch in batches)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();
                    var progress = new Progress<string>(m => ctx.Report(done, approved, batch.Kind + ": " + m));
                    var result = await ProposalApplier.ApplyAsync(batch, Proposals, provider, Gateway, sandbox, progress, ctx.Cancellation).ConfigureAwait(false);
                    totals.Applied += result.Applied; totals.Skipped += result.Skipped; totals.Failed += result.Failed;
                    totals.Messages.AddRange(result.Messages);
                    done += result.Applied + result.Skipped + result.Failed;
                }
                ctx.Report(done, approved, "Applied " + totals.Applied + ", skipped " + totals.Skipped + ", failed " + totals.Failed + " across " + batches.Count + " batches");
                return (object?)new { totals.Applied, totals.Skipped, totals.Failed, totals.Messages, batches = batches.Count };
            });
            return Task.FromResult(Json.Accepted(new { job, batches = batches.Count, approved }));
        }

        private async Task<HttpResponse> TestLlm(HttpRequest r)
        {
            var body = Json.Body(r);
            var llm = CreateLlm(Json.Read<string>(body, "profile"));
            try
            {
                var response = await llm.TestAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                Log.Info("LLM_TEST_OK", llm.Profile + " " + llm.Model + " " + response.Elapsed.TotalMilliseconds + "ms");
                return Json.Ok(new { ok = true, profile = llm.Profile, model = llm.Model, text = response.Text, elapsedMs = (int)response.Elapsed.TotalMilliseconds, response.PromptTokens, response.CompletionTokens });
            }
            catch (ProviderHttpException ex)
            {
                Log.Warn("LLM_TEST_FAILED", ex.Code + " " + ex.Message);
                return Json.Ok(new { ok = false, profile = llm.Profile, model = llm.Model, code = ex.Code, error = ex.Message });
            }
        }
    }
}
