using System;
using System.Collections.Generic;
using System.Linq;
using Halyard.Core.Model;

namespace Halyard.Core.Analysis
{
    public sealed class HierarchyNode
    {
        public HierarchyNode(WorkItem item)
        {
            Item = item;
            Children = new List<HierarchyNode>();
        }

        public WorkItem Item { get; private set; }
        public HierarchyNode? Parent { get; internal set; }
        public List<HierarchyNode> Children { get; private set; }
        public int Depth { get { return Parent == null ? 0 : Parent.Depth + 1; } }

        public IEnumerable<HierarchyNode> Descendants()
        {
            foreach (var c in Children)
            {
                yield return c;
                foreach (var d in c.Descendants()) yield return d;
            }
        }

        /// <summary>Sum of valid story points across descendant Stories (and this node when it is a Story).</summary>
        public int RolledUpPoints()
        {
            var total = 0;
            foreach (var n in new[] { this }.Concat(Descendants()))
            {
                if (n.Item.Type != WorkItemType.Story || n.Item.Meta == null) continue;
                int p;
                if (int.TryParse(n.Item.Meta.Points, out p)) total += p;
            }
            return total;
        }
    }

    public sealed class HierarchyForest
    {
        public HierarchyForest(IReadOnlyList<HierarchyNode> roots, IReadOnlyList<HierarchyNode> orphans, IReadOnlyDictionary<string, HierarchyNode> byKey)
        {
            Roots = roots;
            Orphans = orphans;
            ByKey = byKey;
        }

        /// <summary>Nodes with no declared parent that are Themes (or top-level by declaration).</summary>
        public IReadOnlyList<HierarchyNode> Roots { get; private set; }
        /// <summary>Nodes whose declared parent did not resolve, plus non-Theme items with no parent.</summary>
        public IReadOnlyList<HierarchyNode> Orphans { get; private set; }
        public IReadOnlyDictionary<string, HierarchyNode> ByKey { get; private set; }
    }

    /// <summary>Builds a forest from resolved parent edges (produced by <see cref="TopologyAudit"/>).</summary>
    public static class HierarchyTree
    {
        public static HierarchyForest Build(IEnumerable<WorkItem> items, IReadOnlyDictionary<string, string> parentEdges)
        {
            var nodes = items.Where(i => i != null).ToDictionary(i => i.Key, i => new HierarchyNode(i), StringComparer.OrdinalIgnoreCase);
            var roots = new List<HierarchyNode>();
            var orphans = new List<HierarchyNode>();
            foreach (var node in nodes.Values)
            {
                string? parentKey;
                HierarchyNode? parent;
                if (parentEdges.TryGetValue(node.Item.Key, out parentKey) && nodes.TryGetValue(parentKey!, out parent) && parent != null && parent != node)
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                }
            }
            // Break any residual cycle so the tree is finite: detach the lowest-key node in each cycle.
            foreach (var node in nodes.Values.OrderBy(n => n.Item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var seen = new HashSet<HierarchyNode>();
                var cursor = node;
                while (cursor.Parent != null)
                {
                    if (!seen.Add(cursor))
                    {
                        node.Parent!.Children.Remove(node);
                        node.Parent = null;
                        break;
                    }
                    cursor = cursor.Parent;
                }
            }
            foreach (var node in nodes.Values)
            {
                if (node.Parent != null) continue;
                if (node.Item.Type == WorkItemType.Theme) roots.Add(node);
                else orphans.Add(node);
            }
            Comparison<HierarchyNode> order = (a, b) =>
            {
                var d = WorkItemTypes.Depth(a.Item.Type).CompareTo(WorkItemTypes.Depth(b.Item.Type));
                return d != 0 ? d : string.Compare(a.Item.Title, b.Item.Title, StringComparison.OrdinalIgnoreCase);
            };
            foreach (var n in nodes.Values) n.Children.Sort(order);
            roots.Sort(order);
            orphans.Sort(order);
            return new HierarchyForest(roots, orphans, nodes);
        }
    }
}
