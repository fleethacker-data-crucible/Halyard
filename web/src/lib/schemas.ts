// Zod schemas for the host's JSON. The UI validates what it renders; a shape change in the core
// fails loudly here instead of as an undefined somewhere in a table.
import { z } from 'zod';

export const LevelToken = z.enum(['THEME', 'EPIC', 'FEATURE', 'STORY', 'TASK', 'BUG', 'UNCLASSIFIED']);
export type LevelToken = z.infer<typeof LevelToken>;

export const Severity = z.enum(['info', 'warning', 'error']);
export type Severity = z.infer<typeof Severity>;

const Nullable = <T extends z.ZodTypeAny>(s: T) => s.nullable().optional();

export const Config = z.object({
  provider: z.enum(['github', 'gitlab']),
  baseUrl: z.string(),
  sourceRepository: z.string(),
  sourceRepositories: z.array(z.string()),
  sandboxRepository: z.string(),
  hasProviderToken: z.boolean(),
  writeMode: z.enum(['sandbox', 'production']),
  allowProduction: z.boolean(),
  labelFilter: Nullable(z.string()),
  preferredPort: z.number(),
  maxSyncItems: z.number(),
  protectedRepositories: z.array(z.string()),
  llmProfile: z.string(),
  llmResolvedProfile: z.string(),
  llmModel: z.string(),
  llmUrl: z.string(),
  hasLlmToken: z.boolean(),
  llmMissing: Nullable(z.string()),
  llmProfiles: z.array(z.object({ profile: z.string(), model: z.string(), url: z.string(), configured: z.boolean(), missing: Nullable(z.string()) })),
  envFile: z.string(),
  envFileExists: z.boolean(),
  rootDirectory: z.string(),
  outputDirectory: z.string(),
});
export type Config = z.infer<typeof Config>;

export const Policy = z.object({
  mode: z.enum(['sandbox', 'production']),
  sandbox: z.array(z.string()),
  production: Nullable(z.string()),
  protected: z.array(z.string()),
  allowProduction: z.boolean(),
  deleteRequiresAnyLabel: z.array(z.string()),
  description: z.string(),
});
export type Policy = z.infer<typeof Policy>;

export const Identity = z.object({ login: z.string(), name: Nullable(z.string()), url: Nullable(z.string()), apiBase: z.string(), provider: z.string() });

export const Status = z.object({
  version: z.string(),
  startedAt: z.string(),
  uptimeSeconds: z.number(),
  port: z.number(),
  config: Config,
  policy: Policy,
  provider: z.object({
    kind: z.string(),
    configured: z.boolean(),
    identity: Nullable(Identity),
    error: Nullable(z.string()),
    capabilities: Nullable(z.object({ deleteIssues: z.boolean(), nativeParentLinks: z.boolean(), labelColors: z.boolean(), recommendedWriteDelayMs: z.number() })),
  }),
  snapshot: Nullable(z.object({ id: z.string(), repository: z.string(), repositories: z.array(z.string()).optional(), provider: z.string(), takenAt: z.string(), count: z.number(), ageSeconds: z.number(), labelFilter: Nullable(z.string()) })),
  jobs: z.object({ running: z.number() }),
  launcher: z.record(z.unknown()),
  webRoot: Nullable(z.string()),
  proposals: z.number(),
});
export type Status = z.infer<typeof Status>;

export const Job = z.object({
  id: z.string(),
  kind: z.string(),
  state: z.enum(['queued', 'running', 'succeeded', 'failed', 'cancelled']),
  createdAt: z.string(),
  startedAt: Nullable(z.string()),
  finishedAt: Nullable(z.string()),
  progressDone: z.number(),
  progressTotal: z.number(),
  message: Nullable(z.string()),
  error: Nullable(z.string()),
  errorCode: Nullable(z.string()),
  result: z.unknown(),
  log: z.array(z.string()),
});
export type Job = z.infer<typeof Job>;

export const Finding = z.object({
  code: z.string(),
  severity: Severity,
  explanation: z.string(),
});

export const ReviewItem = z.object({
  key: z.string(),
  number: z.number(),
  repository: z.string(),
  canonicalId: z.string(),
  title: z.string(),
  url: Nullable(z.string()),
  type: LevelToken,
  state: z.enum(['open', 'closed']),
  labels: z.array(z.string()),
  parentId: Nullable(z.string()),
  resolvedParentKey: Nullable(z.string()),
  themeId: Nullable(z.string()),
  hasMeta: z.boolean(),
  points: Nullable(z.string()),
  effort: Nullable(z.string()),
  sprint: Nullable(z.string()),
  area: Nullable(z.string()),
  updatedAt: Nullable(z.string()),
  missingMeta: z.array(z.string()),
  deficiencies: z.array(z.string()),
  findings: z.array(Finding),
  needsReview: z.boolean(),
});
export type ReviewItem = z.infer<typeof ReviewItem>;

export const Review = z.object({
  snapshot: Nullable(z.object({ id: z.string(), takenAt: z.string(), repository: z.string(), repositories: z.array(z.string()) })),
  sandbox: Nullable(z.string()),
  labelFilter: Nullable(z.string()),
  generatedAt: z.string(),
  summary: z.object({
    total: z.number(),
    classified: z.number(),
    withMeta: z.number(),
    needsReview: z.number(),
    missingParents: z.number(),
    storiesWithPoints: z.number(),
    stories: z.number(),
    featuresWithEffort: z.number(),
    features: z.number(),
    byType: z.record(z.number()),
    deficiencyCounts: z.record(z.number()),
    findingCounts: z.record(z.number()),
    totalPoints: z.number(),
  }),
  audit: z.object({
    visibleCount: z.number(),
    sourceCount: z.number(),
    classifiedCount: z.number(),
    errorCount: z.number(),
    warningCount: z.number(),
    infoCount: z.number(),
    affectedCount: z.number(),
    findings: z.array(Finding.extend({ key: z.string(), number: z.number(), title: z.string(), url: Nullable(z.string()), type: LevelToken, canonicalId: z.string() })),
  }),
  items: z.array(ReviewItem),
  parentReviews: z.array(z.object({
    key: z.string(),
    canonicalId: z.string(),
    title: z.string(),
    type: LevelToken,
    declaredParent: Nullable(z.string()),
    expectedParentType: Nullable(LevelToken),
    candidates: z.array(z.object({ key: z.string(), canonicalId: z.string(), title: z.string(), type: LevelToken, score: z.number(), evidence: z.array(z.string()) })),
  })),
});
export type Review = z.infer<typeof Review>;

export type HierarchyNode = {
  key: string;
  number: number;
  repository: string;
  canonicalId: string;
  type: LevelToken;
  title: string;
  fullTitle: string;
  url?: string | null;
  state: 'open' | 'closed';
  points?: string | null;
  effort?: string | null;
  sprint?: string | null;
  rolledUpPoints: number;
  descendantCount: number;
  children: HierarchyNode[];
};
export const HierarchyNode: z.ZodType<HierarchyNode> = z.lazy(() => z.object({
  key: z.string(),
  number: z.number(),
  repository: z.string(),
  canonicalId: z.string(),
  type: LevelToken,
  title: z.string(),
  fullTitle: z.string(),
  url: Nullable(z.string()),
  state: z.enum(['open', 'closed']),
  points: Nullable(z.string()),
  effort: Nullable(z.string()),
  sprint: Nullable(z.string()),
  rolledUpPoints: z.number(),
  descendantCount: z.number(),
  children: z.array(HierarchyNode),
}));

export const Hierarchy = z.object({
  labelFilter: Nullable(z.string()),
  total: z.number(),
  roots: z.array(HierarchyNode),
  orphans: z.array(HierarchyNode),
  edges: z.array(z.object({ child: z.string(), parent: z.string() })),
});
export type Hierarchy = z.infer<typeof Hierarchy>;

export const ItemDetail = z.object({
  key: z.string(),
  number: z.number(),
  repository: z.string(),
  inSandbox: z.boolean(),
  title: z.string(),
  shortName: z.string(),
  type: LevelToken,
  canonicalId: z.string(),
  state: z.enum(['open', 'closed']),
  labels: z.array(z.string()),
  assignees: z.array(z.string()),
  url: Nullable(z.string()),
  milestone: Nullable(z.string()),
  createdAt: Nullable(z.string()),
  updatedAt: Nullable(z.string()),
  body: z.string(),
  narrative: z.string(),
  meta: Nullable(z.array(z.object({ key: z.string(), value: z.string() }))),
  missingMeta: z.array(z.string()),
  deficiencies: z.array(z.object({ code: z.string(), rule: Nullable(z.object({ code: z.string(), appliesTo: Nullable(LevelToken), explanation: z.string() })) })),
  findings: z.array(Finding),
  parentKey: Nullable(z.string()),
  children: z.array(z.string()),
});
export type ItemDetail = z.infer<typeof ItemDetail>;

export const ProposalKind = z.enum(['storyPoints', 'featureEffort', 'parentLink', 'refinement', 'metadata']);
export type ProposalKind = z.infer<typeof ProposalKind>;

export const Proposal = z.object({
  id: z.string(),
  batchId: z.string(),
  kind: ProposalKind,
  itemKey: z.string(),
  itemNumber: z.number(),
  itemCanonicalId: z.string(),
  itemTitle: z.string(),
  currentValue: Nullable(z.string()),
  proposedValue: z.string(),
  confidence: z.string(),
  reason: z.string(),
  source: z.string(),
  decision: z.enum(['pending', 'approved', 'rejected', 'applied', 'failed']),
  decisionNote: Nullable(z.string()),
  createdAt: z.string(),
  decidedAt: Nullable(z.string()),
  appliedAt: Nullable(z.string()),
  error: Nullable(z.string()),
  proposedMeta: Nullable(z.record(z.string().nullable())),
  addresses: z.array(z.string()),
  remaining: z.array(z.string()),
});
export type Proposal = z.infer<typeof Proposal>;

const MetaRows = Nullable(z.array(z.object({ key: z.string(), value: z.string() })));
export const ProposalPreview = z.object({
  proposalId: z.string(),
  batchId: z.string(),
  number: z.number(),
  title: z.string(),
  type: LevelToken,
  current: z.object({ body: z.string(), narrative: z.string(), meta: MetaRows, deficiencies: z.array(z.string()) }),
  proposed: z.object({ body: z.string(), narrative: z.string(), meta: MetaRows, deficiencies: z.array(z.string()) }),
  unchanged: z.boolean(),
  snapshotStale: z.boolean(),
});
export type ProposalPreview = z.infer<typeof ProposalPreview>;

export const BatchSummary = z.object({
  id: z.string(),
  kind: ProposalKind,
  target: z.string(),
  createdAt: z.string(),
  source: z.string(),
  considered: z.number(),
  skipped: z.number(),
  warnings: z.array(z.string()),
  total: z.number(),
  pending: z.number(),
  approved: z.number(),
  rejected: z.number(),
  applied: z.number(),
  failed: z.number(),
});
export type BatchSummary = z.infer<typeof BatchSummary>;

export const Batch = z.object({
  id: z.string(),
  kind: ProposalKind,
  target: z.string(),
  createdAt: z.string(),
  source: z.string(),
  considered: z.number(),
  skipped: z.number(),
  skipReasons: z.array(z.string()),
  proposals: z.array(Proposal),
  rawModelOutput: Nullable(z.string()),
  warnings: z.array(z.string()),
});
export type Batch = z.infer<typeof Batch>;

export const SeedManifest = z.object({
  runId: z.string(),
  target: z.string(),
  startedAt: z.string(),
  finishedAt: Nullable(z.string()),
  requested: z.number(),
  items: z.array(z.object({ id: z.string(), type: z.string(), parentId: Nullable(z.string()), number: z.number(), key: z.string(), url: Nullable(z.string()), nativeLinked: z.boolean(), defects: z.array(z.string()), expectedCodes: z.array(z.string()) })),
  errors: z.array(z.string()),
  completed: z.boolean(),
  needsReview: z.number(),
});
export type SeedManifest = z.infer<typeof SeedManifest>;

export const CatalogPreview = z.object({
  options: z.object({ themes: z.number(), epicsPerTheme: z.number(), featuresPerEpic: z.number(), stories: z.number(), tasks: z.number(), seed: z.number(), includeDefects: z.boolean(), defectRate: z.number(), total: z.number() }).passthrough(),
  total: z.number(),
  counts: z.record(z.number()),
  complete: z.number(),
  needsReview: z.number(),
  sample: z.array(z.object({ id: z.string(), type: LevelToken, title: z.string(), parentId: Nullable(z.string()), labels: z.array(z.string()), points: Nullable(z.string()), effort: Nullable(z.string()) })),
  defects: z.array(z.object({ id: z.string(), type: LevelToken.optional(), defects: z.array(z.string()), expectedCodes: z.array(z.string()) })),
  defectKinds: z.array(z.string()),
});
export type CatalogPreview = z.infer<typeof CatalogPreview>;

export const LogEntry = z.object({ time: z.string(), level: z.string(), event: z.string(), message: Nullable(z.string()), route: Nullable(z.string()), requestId: Nullable(z.string()) });
export const Diagnostics = z.object({ file: Nullable(z.string()), entries: z.array(LogEntry) });
export type LogEntry = z.infer<typeof LogEntry>;

export const WriteAudit = z.object({
  policy: Policy,
  entries: z.array(z.object({ at: z.string(), operation: z.string(), target: z.string(), number: Nullable(z.number()), title: Nullable(z.string()), reason: Nullable(z.string()), runId: Nullable(z.string()), succeeded: z.boolean(), error: Nullable(z.string()) })),
});
export type WriteAudit = z.infer<typeof WriteAudit>;

export const Schema = z.object({
  types: z.array(z.object({ token: LevelToken, label: z.string(), depth: z.number(), expectedParent: Nullable(LevelToken) })),
  requiredMeta: z.record(z.array(z.string())),
  themes: z.array(z.object({ id: z.string(), title: z.string(), area: z.string(), purpose: z.string(), status: z.string() })),
  deficiencyRules: z.array(z.object({ code: z.string(), appliesTo: Nullable(LevelToken), explanation: z.string() })),
  sizing: z.object({ points: z.array(z.number()), effort: z.array(z.string()), tbd: z.string() }),
  labels: z.object({ managed: z.string(), synthetic: z.string(), runPrefix: z.string(), reviewQueue: z.string() }),
  metaFence: z.string(),
});
export type Schema = z.infer<typeof Schema>;

export const Snapshot = z.object({
  id: z.string(),
  repository: z.string(),
  repositories: z.array(z.string()),
  provider: z.string(),
  takenAt: z.string(),
  ageSeconds: z.number(),
  labelFilter: Nullable(z.string()),
  count: z.number(),
  labels: z.array(z.object({ label: z.string(), count: z.number() })),
});
export type Snapshot = z.infer<typeof Snapshot>;

export const Repositories = z.array(z.object({ identifier: z.string(), displayName: z.string(), url: Nullable(z.string()), key: z.string() }));
export const Labels = z.object({ repository: z.string(), labels: z.array(z.string()) });
