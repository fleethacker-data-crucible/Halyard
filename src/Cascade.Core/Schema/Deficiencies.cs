using System.Collections.Generic;
using Cascade.Core.Model;

namespace Cascade.Core.Schema
{
    public sealed class DorRule
    {
        public DorRule(string code, WorkItemType? appliesTo, string explanation)
        {
            Code = code;
            AppliesTo = appliesTo;
            Explanation = explanation;
        }

        public string Code { get; private set; }
        public WorkItemType? AppliesTo { get; private set; }
        public string Explanation { get; private set; }
    }

    /// <summary>
    /// Definition-of-Ready deficiency codes. The code strings are stable identifiers used by the UI,
    /// exports and tests; keep them unchanged when editing explanations.
    /// </summary>
    public static class Deficiencies
    {
        public const string MissingMeta = "missing_cascade_metadata";
        public const string InvalidMeta = "invalid_cascade_metadata";
        public const string StoryMissingParent = "story_missing_feature_parent";
        public const string StoryMissingAcceptanceCriteria = "story_missing_acceptance_criteria";
        public const string StoryMissingUserNeed = "story_missing_user_need";
        public const string StoryMissingTasks = "story_missing_tasks";
        public const string StoryMissingPoints = "story_missing_points";
        public const string StoryInvalidPoints = "story_invalid_points";
        public const string StoryMissingSprint = "story_missing_sprint";
        public const string TaskMissingParent = "task_missing_story_parent";
        public const string FeatureMissingParent = "feature_missing_epic_parent";
        public const string FeatureMissingDoneWhen = "feature_missing_done_when";
        public const string FeatureMissingDescription = "feature_missing_short_description";
        public const string FeatureMissingEffort = "feature_missing_effort";
        public const string FeatureInvalidEffort = "feature_invalid_effort";
        public const string EpicMissingOutcome = "epic_missing_outcome_statement";
        public const string EpicMissingSuccessPicture = "epic_missing_success_picture";
        public const string EpicMissingTheme = "epic_missing_theme";
        public const string ThemeMissingPurpose = "theme_missing_purpose";
        public const string ThemeMissingScope = "theme_missing_scope";
        public const string FeatureHasPoints = "feature_has_story_points";
        public const string StoryHasEffort = "story_has_effort_band";

        public static readonly DorRule[] Rules =
        {
            new DorRule(MissingMeta, null, "No cascade-meta block; the item cannot be placed in the hierarchy by machine."),
            new DorRule(InvalidMeta, null, "A cascade-meta block exists but has no recognised type or no id."),
            new DorRule(StoryMissingParent, WorkItemType.Story, "Stories belong under a Feature; declare parent: FEATURE-…"),
            new DorRule(StoryMissingAcceptanceCriteria, WorkItemType.Story, "Add testable acceptance criteria as bullets or checkboxes."),
            new DorRule(StoryMissingUserNeed, WorkItemType.Story, "State the user need: who, what, why."),
            new DorRule(StoryMissingTasks, WorkItemType.Story, "Add a task checklist; tasks live inside the Story by default."),
            new DorRule(StoryMissingPoints, WorkItemType.Story, "Stories carry points (1, 2, 3, 5, 8) or an explicit TBD."),
            new DorRule(StoryInvalidPoints, WorkItemType.Story, "Points must be one of 1, 2, 3, 5, 8 or TBD."),
            new DorRule(StoryMissingSprint, WorkItemType.Story, "Stories carry a sprint or an explicit TBD."),
            new DorRule(TaskMissingParent, WorkItemType.Task, "Standalone Tasks belong under a Story; declare parent: STORY-…"),
            new DorRule(FeatureMissingParent, WorkItemType.Feature, "Features belong under an Epic; declare parent: EPIC-…"),
            new DorRule(FeatureMissingDoneWhen, WorkItemType.Feature, "Add a Done-when / Definition of done checklist."),
            new DorRule(FeatureMissingDescription, WorkItemType.Feature, "Add a short description of the capability delivered."),
            new DorRule(FeatureMissingEffort, WorkItemType.Feature, "Features carry effort Small, Medium or Large, or an explicit TBD."),
            new DorRule(FeatureInvalidEffort, WorkItemType.Feature, "Effort must be Small, Medium, Large or TBD."),
            new DorRule(EpicMissingOutcome, WorkItemType.Epic, "State the observable outcome when the Epic is done."),
            new DorRule(EpicMissingSuccessPicture, WorkItemType.Epic, "Add a success picture: 3–7 concrete bullets."),
            new DorRule(EpicMissingTheme, WorkItemType.Epic, "Epics declare their Theme lane in meta."),
            new DorRule(ThemeMissingPurpose, WorkItemType.Theme, "State why the lane exists."),
            new DorRule(ThemeMissingScope, WorkItemType.Theme, "State what is in and out of scope."),
            new DorRule(FeatureHasPoints, WorkItemType.Feature, "Features use effort bands, never story points."),
            new DorRule(StoryHasEffort, WorkItemType.Story, "Stories use points, never effort bands."),
        };

        public static DorRule? Find(string code)
        {
            foreach (var r in Rules) if (r.Code == code) return r;
            return null;
        }

        /// <summary>Evaluates one item. Returns stable codes in a deterministic order.</summary>
        public static IReadOnlyList<string> Evaluate(WorkItem item)
        {
            return Evaluate(item.Type, item.Meta, item.Body);
        }

        public static IReadOnlyList<string> Evaluate(WorkItemType type, CascadeMeta? meta, string? body)
        {
            var codes = new List<string>();
            if (meta == null) codes.Add(MissingMeta);
            else if (!meta.IsValid) codes.Add(InvalidMeta);

            var parent = meta == null ? null : meta.Parent;
            var hasParent = !string.IsNullOrWhiteSpace(parent);

            switch (type)
            {
                case WorkItemType.Story:
                    if (!hasParent) codes.Add(StoryMissingParent);
                    if (!BodyChecks.HasUserNeed(body)) codes.Add(StoryMissingUserNeed);
                    if (!BodyChecks.HasAcceptanceCriteria(body)) codes.Add(StoryMissingAcceptanceCriteria);
                    if (!BodyChecks.HasTasks(body)) codes.Add(StoryMissingTasks);
                    if (meta != null)
                    {
                        if (!meta.Has("points")) codes.Add(StoryMissingPoints);
                        else if (!Sizing.IsValidPoints(meta.Points)) codes.Add(StoryInvalidPoints);
                        if (!meta.Has("sprint")) codes.Add(StoryMissingSprint);
                        if (meta.Has("effort")) codes.Add(StoryHasEffort);
                    }
                    break;
                case WorkItemType.Task:
                    if (!hasParent) codes.Add(TaskMissingParent);
                    break;
                case WorkItemType.Feature:
                    if (!hasParent) codes.Add(FeatureMissingParent);
                    if (!BodyChecks.HasSubstantiveText(body)) codes.Add(FeatureMissingDescription);
                    if (!BodyChecks.HasDoneWhen(body)) codes.Add(FeatureMissingDoneWhen);
                    if (meta != null)
                    {
                        if (!meta.Has("effort")) codes.Add(FeatureMissingEffort);
                        else if (!Sizing.IsValidEffort(meta.Effort)) codes.Add(FeatureInvalidEffort);
                        if (meta.Has("points")) codes.Add(FeatureHasPoints);
                    }
                    break;
                case WorkItemType.Epic:
                    if (!BodyChecks.HasOutcome(body)) codes.Add(EpicMissingOutcome);
                    if (!BodyChecks.HasSuccessPicture(body)) codes.Add(EpicMissingSuccessPicture);
                    if (meta != null && !meta.Has("theme")) codes.Add(EpicMissingTheme);
                    break;
                case WorkItemType.Theme:
                    if (!BodyChecks.HasPurpose(body)) codes.Add(ThemeMissingPurpose);
                    if (!BodyChecks.HasScope(body)) codes.Add(ThemeMissingScope);
                    break;
            }
            return codes;
        }
    }
}
