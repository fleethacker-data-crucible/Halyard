using System;
using System.Threading.Tasks;
using Halyard.Core.Planning;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    /// <summary>Planned versus delivered per finished sprint, and the commitment the next plan should use.</summary>
    public sealed partial class HalyardHost
    {
        private void RegisterSprintReviewRoutes()
        {
            Router.Get("/api/sprint-review", r =>
            {
                if (Snapshots.Current == null) throw new BadRequestException("Synchronize a repository first.");
                var sprints = 6;
                int asked;
                if (int.TryParse(Query(r, "sprints"), out asked)) sprints = Math.Min(Math.Max(asked, 1), 12);
                System.Collections.Generic.List<Model.WorkItem> all;
                var visible = VisibleItems(Query(r, "label") ?? Settings.LabelFilter, out all);
                return Task.FromResult(Json.Ok(SprintReview.Build(visible, Team, DateTimeOffset.UtcNow, sprints)));
            });
        }
    }
}
