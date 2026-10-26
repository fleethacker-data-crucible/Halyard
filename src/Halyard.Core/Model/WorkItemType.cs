using System;
using System.Collections.Generic;

namespace Halyard.Core.Model
{
    /// <summary>
    /// Halyard work-item levels. THEME → EPIC → FEATURE → STORY → TASK is the locked topology;
    /// BUG is tolerated as a leaf type; UNCLASSIFIED means nothing explicit was declared.
    /// </summary>
    public enum WorkItemType
    {
        Unclassified = 0,
        Theme = 1,
        Epic = 2,
        Feature = 3,
        Story = 4,
        Task = 5,
        Bug = 6,
    }

    public static class WorkItemTypes
    {
        public static readonly WorkItemType[] Hierarchy =
        {
            WorkItemType.Theme, WorkItemType.Epic, WorkItemType.Feature, WorkItemType.Story, WorkItemType.Task,
        };

        public static readonly WorkItemType[] Classified =
        {
            WorkItemType.Theme, WorkItemType.Epic, WorkItemType.Feature, WorkItemType.Story, WorkItemType.Task, WorkItemType.Bug,
        };

        /// <summary>The type a child normally belongs under. Themes have no parent; bugs are unconstrained.</summary>
        public static WorkItemType? ExpectedParent(WorkItemType type)
        {
            switch (type)
            {
                case WorkItemType.Epic: return WorkItemType.Theme;
                case WorkItemType.Feature: return WorkItemType.Epic;
                case WorkItemType.Story: return WorkItemType.Feature;
                case WorkItemType.Task: return WorkItemType.Story;
                default: return null;
            }
        }

        public static int Depth(WorkItemType type)
        {
            switch (type)
            {
                case WorkItemType.Theme: return 0;
                case WorkItemType.Epic: return 1;
                case WorkItemType.Feature: return 2;
                case WorkItemType.Story: return 3;
                case WorkItemType.Task: return 4;
                case WorkItemType.Bug: return 4;
                default: return 5;
            }
        }

        public static string ToToken(WorkItemType type)
        {
            return type == WorkItemType.Unclassified ? "UNCLASSIFIED" : type.ToString().ToUpperInvariant();
        }

        public static string ToLabel(WorkItemType type)
        {
            return "type::" + type.ToString().ToLowerInvariant();
        }

        public static bool TryParse(string text, out WorkItemType type)
        {
            type = WorkItemType.Unclassified;
            if (string.IsNullOrWhiteSpace(text)) return false;
            switch (text.Trim().ToUpperInvariant())
            {
                case "THEME": type = WorkItemType.Theme; return true;
                case "EPIC": type = WorkItemType.Epic; return true;
                case "FEATURE": type = WorkItemType.Feature; return true;
                case "STORY": type = WorkItemType.Story; return true;
                case "TASK": type = WorkItemType.Task; return true;
                case "BUG": type = WorkItemType.Bug; return true;
                default: return false;
            }
        }

        public static WorkItemType Parse(string text)
        {
            WorkItemType t;
            return TryParse(text, out t) ? t : WorkItemType.Unclassified;
        }

        public static IEnumerable<WorkItemType> All()
        {
            return (WorkItemType[])Enum.GetValues(typeof(WorkItemType));
        }
    }
}
