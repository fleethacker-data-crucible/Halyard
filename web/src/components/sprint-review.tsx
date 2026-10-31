import { Mono, Section } from './ui';
import { SprintReviewReport } from '../lib/schemas';
import { useResource } from '../lib/useResource';

/** Planned versus delivered for each finished sprint, and the commitment the next plan can take from it. */
export function SprintReviewPanel() {
  const review = useResource('/api/sprint-review', SprintReviewReport);
  const r = review.data;
  if (!r) return null;
  return (
    <Section title="Sprint review" aside={r.recommendedShare != null ? <span className="text-[12px] text-ink-3">suggests {Math.round(r.recommendedShare * 100)}% ({r.recommendedCommitPoints} pts)</span> : null}>
      {r.sprints.length === 0 ? <p className="px-4 py-3 text-ink-2">{r.notes[0] ?? 'No finished sprint to review yet.'}</p> : (
        <table className="w-full text-[12.5px]">
          <thead><tr className="text-left text-ink-3"><th className="px-4 py-1.5">Sprint</th><th>Planned</th><th>Delivered</th><th>Slipped</th><th>Rate</th></tr></thead>
          <tbody>
            {r.sprints.map((s) => (
              <tr key={s.sprint} className="hairline">
                <td className="px-4 py-1.5"><Mono>{s.sprint}</Mono></td>
                <td>{s.pointsPlanned} pts</td>
                <td>{s.pointsDelivered} pts</td>
                <td>{s.pointsSlipped} pts</td>
                <td>{s.deliveryRate == null ? '—' : `${Math.round(s.deliveryRate * 100)}%`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {r.notes.length > 0 && r.sprints.length > 0 ? <ul className="px-4 py-2 text-[12px] text-ink-2">{r.notes.map((n) => <li key={n}>{n}</li>)}</ul> : null}
    </Section>
  );
}
