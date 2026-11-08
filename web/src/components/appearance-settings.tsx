// Settings > Appearance: Theme, Make it yours and Crew orb, as Lazarette and Semaphore lay them out (Lazarette
// ui-src/src/views/settings/SettingsView.svelte, Semaphore web/src/components/ThemeCards.tsx). Everything here lives in
// this browser (lib/theme.ts, lib/motion.ts, lib/orb-prefs.ts); a theme exported here imports into Lazarette and
// Semaphore, and the other way round.
import type { ReactNode } from 'react';
import { LuDownload, LuMonitor, LuMoon, LuRotateCcw, LuSparkles, LuSun, LuUpload } from 'react-icons/lu';
import { setReduceMotionChoice, useReduceMotionChoice } from '../lib/motion';
import { ORB_LOOKS, ORB_THEMES, setOrbPrefs, useOrbPrefs, type OrbLook, type OrbTalk, type OrbTheme } from '../lib/orb-prefs';
import { theme, useTheme, type ThemeSpec } from '../lib/theme';
import { contrastRatio, edited, exportTheme as themeJson, isPreset as presetAsItComes, parseTheme, PRESETS, sameTheme, surprise } from '../lib/theme-presets';
import { Button, Section, Select, cn, useToast } from './ui';

function Row({ id, label, hint, children }: { id?: string; label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1.5 text-[13px]">
      <span className="min-w-0">
        {id ? <label htmlFor={id} className="font-medium">{label}</label> : <span className="font-medium">{label}</span>}
        {hint ? <span className="block text-[12px] text-ink-2">{hint}</span> : null}
      </span>
      {children}
    </div>
  );
}

function Toggle({ label, hint, checked, onChange, disabled }: { label: string; hint: string; checked: boolean; onChange: (on: boolean) => void; disabled?: boolean }) {
  return (
    <label className={cn('flex cursor-pointer items-center justify-between gap-4 text-[13px]', disabled && 'opacity-50')}>
      <span>
        <span className="font-medium">{label}</span>
        <span className="block text-[12px] text-ink-2">{hint}</span>
      </span>
      <input type="checkbox" className="switch" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
    </label>
  );
}

/** A preset's swatch: its tint behind a bar of its accent, cut to its shape. */
function Swatch({ p }: { p: ThemeSpec }) {
  const bg = p.tint || (p.mode === 'light' ? '#fbfaf2' : '#071a26');
  const radius = p.shape === 'pill' ? '999px' : p.shape === 'sharp' ? '2px' : '6px';
  return (
    <span aria-hidden className="flex h-9 w-full overflow-hidden rounded-ctl border border-line-2" style={{ background: bg }}>
      <span className="m-auto h-4 w-10" style={{ background: p.accent || '#2bb3a3', borderRadius: radius }} />
    </span>
  );
}

const MODES = [
  { v: 'dark', l: 'Night watch', I: LuMoon },
  { v: 'light', l: 'Chart room', I: LuSun },
  { v: 'system', l: 'Follow Windows', I: LuMonitor },
] as const;

export function AppearanceSettings() {
  const toast = useToast();
  const spec = useTheme();
  const reduce = useReduceMotionChoice();
  const orb = useOrbPrefs();
  const isPreset = presetAsItComes(spec);
  // An edited preset is named Custom, as in Lazarette.
  const set = (next: Partial<ThemeSpec>) => theme.replace(edited(spec, next));
  const pageBg = spec.mode === 'light' ? '#fbfaf2' : '#071a26';
  const accentWarning = spec.accent && contrastRatio(spec.accent, pageBg) < 3 ? 'This accent is hard to see on the page background (contrast under 3:1).' : '';

  const exportTheme = () => {
    const url = URL.createObjectURL(new Blob([themeJson(spec)], { type: 'application/json' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = `halyard-theme-${spec.name.toLowerCase().replace(/[^a-z0-9]+/g, '-') || 'custom'}.json`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };
  const importTheme = async (input: HTMLInputElement) => {
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    if (file.size > 64 * 1024) { toast.push('err', 'That file is too large to be a theme.'); return; }
    const imported = parseTheme(await file.text());
    if (imported) { theme.replace(imported); toast.push('ok', `Theme "${imported.name}" applied.`); }
    else toast.push('err', 'That is not a theme file.');
  };
  const tile = (on: boolean) => cn('flex flex-col gap-2 rounded-ctl border p-2 text-left text-[12px]', on ? 'border-accent bg-surface-2' : 'border-line-2 hover:bg-surface-2');

  return (
    <>
      <Section title="Theme" aside={<span className="text-[12px] text-ink-3">Kept in this browser</span>}>
        <div className="flex flex-col gap-3 px-4 py-3">
          <p className="text-[12.5px] text-ink-2">Pick a preset, then make it yours. Export a theme to share it; a theme from Lazarette or Semaphore imports here too. The hierarchy's level colours stay the same in every theme.</p>
          <div className="grid grid-cols-3 gap-2 sm:grid-cols-5" role="radiogroup" aria-label="Theme presets">
            {PRESETS.map((p) => {
              const on = sameTheme(p, spec);
              return (
                <button key={p.name} type="button" role="radio" aria-checked={on} onClick={() => theme.replace(p)} className={tile(on)}>
                  <Swatch p={p} />
                  <span className="font-medium">{p.name}</span>
                </button>
              );
            })}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" onClick={() => theme.replace(surprise(spec))}><LuSparkles size={14} />Surprise me</Button>
            <Button size="sm" variant="quiet" onClick={() => theme.reset()}><LuRotateCcw size={14} />Back to Fleet</Button>
            <span className="flex-1" />
            <Button size="sm" variant="quiet" onClick={exportTheme}><LuDownload size={14} />Export</Button>
            <label className="inline-flex h-7 cursor-pointer items-center gap-1.5 rounded-ctl px-2 text-[12.5px] text-ink-2 hover:bg-surface-2 hover:text-ink">
              <LuUpload size={14} />Import
              <input type="file" accept="application/json,.json" className="sr-only" onChange={(e) => void importTheme(e.currentTarget)} />
            </label>
          </div>
        </div>
      </Section>

      <Section title="Make it yours" aside={<span className="text-[12px] text-ink-3">{isPreset ? `Editing ${spec.name}` : `Custom: ${spec.name}`}</span>}>
        <div className="grid gap-5 px-4 py-3 md:grid-cols-2">
          <div className="grid content-start gap-3">
            <div className="grid grid-cols-3 gap-2" role="radiogroup" aria-label="Mode">
              {MODES.map((o) => (
                <button key={o.v} type="button" role="radio" aria-checked={spec.mode === o.v} onClick={() => set({ mode: o.v })}
                  className={cn('flex min-h-9 items-center justify-center gap-2 rounded-ctl border px-2 text-[12.5px]', spec.mode === o.v ? 'border-accent bg-surface-2 font-medium' : 'border-line-2 hover:bg-surface-2')}>
                  <o.I size={15} aria-hidden />{o.l}
                </button>
              ))}
            </div>
            <Row label="Accent" hint="Buttons, links and the active page.">
              <span className="flex items-center gap-2">
                <input type="color" value={spec.accent || '#2bb3a3'} onChange={(e) => set({ accent: e.target.value })} aria-label="Accent colour" className="h-8 w-12 cursor-pointer rounded-ctl border border-line bg-transparent p-0.5" />
                {spec.accent ? <Button size="sm" variant="quiet" aria-label="Use Halyard's own accent" title="Use Halyard's own accent" onClick={() => set({ accent: '' })}><LuRotateCcw size={14} /></Button> : null}
              </span>
            </Row>
            {accentWarning ? <p className="text-[12px] text-warn">{accentWarning}</p> : null}
            <Row label="Tint" hint="Mixed into the page, panels and cards.">
              <span className="flex items-center gap-2">
                <input type="color" value={spec.tint || pageBg} onChange={(e) => set({ tint: e.target.value, tintStrength: spec.tintStrength || 30 })} aria-label="Tint colour" className="h-8 w-12 cursor-pointer rounded-ctl border border-line bg-transparent p-0.5" />
                <input type="range" min={0} max={40} step={5} value={spec.tintStrength} onChange={(e) => set({ tintStrength: Number(e.target.value) })} aria-label="Tint strength" className="h-8 w-24 accent-[var(--accent)]" />
              </span>
            </Row>
            <Toggle label="Glow" hint="A soft halo behind the emblem and primary buttons." checked={spec.glow} onChange={(glow) => set({ glow })} />
            <Toggle label="High contrast" hint="Stronger borders, text and focus rings." checked={spec.contrast === 'high'} onChange={(on) => set({ contrast: on ? 'high' : 'normal' })} />
            <Toggle label="Reduce motion" hint="Turns off animations even if Windows allows them. The orb then stays still." checked={reduce} onChange={setReduceMotionChoice} />
          </div>
          <div className="grid content-start gap-3">
            <Row id="t-shape" label="Shape" hint="Corners on buttons, panels and dialogs.">
              <Select id="t-shape" className="w-48" value={spec.shape} onChange={(e) => set({ shape: e.target.value as ThemeSpec['shape'] })}>
                <option value="sharp">Sharp</option><option value="soft">Soft (Fleet)</option><option value="round">Round</option><option value="pill">Pill</option>
              </Select>
            </Row>
            <Row id="t-density" label="Density" hint="How much fits on the screen.">
              <Select id="t-density" className="w-48" value={spec.density} onChange={(e) => set({ density: e.target.value as ThemeSpec['density'] })}>
                <option value="compact">Compact</option><option value="comfortable">Comfortable</option><option value="spacious">Spacious</option>
              </Select>
            </Row>
            <Row id="t-type" label="Type" hint="All ship with Windows.">
              <Select id="t-type" className="w-48" value={spec.type} onChange={(e) => set({ type: e.target.value as ThemeSpec['type'] })}>
                <option value="inter">Fleet</option><option value="system">Windows system font</option><option value="humanist">Segoe UI</option><option value="serif">Serif</option><option value="mono">Monospace</option>
              </Select>
            </Row>
            <Row id="t-motion" label="Motion" hint="Transitions and animations.">
              <Select id="t-motion" className="w-48" value={spec.motion} onChange={(e) => set({ motion: e.target.value as ThemeSpec['motion'] })}>
                <option value="full">Full</option><option value="calm">Calm (shorter)</option><option value="off">Off</option>
              </Select>
            </Row>
            <Row id="t-pattern" label="Background pattern">
              <Select id="t-pattern" className="w-48" value={spec.pattern} onChange={(e) => set({ pattern: e.target.value as ThemeSpec['pattern'] })}>
                <option value="none">None</option><option value="grid">Grid</option><option value="dots">Dots</option><option value="waves">Waves</option><option value="rope">Rope</option><option value="stars">Stars</option>
              </Select>
            </Row>
          </div>
        </div>
      </Section>

      <Section title="Crew orb">
        <div className="grid gap-3 px-4 py-3">
          <p className="text-[12.5px] text-ink-2">The orb has no menu of its own: hovering does nothing. Its look, theme and how you talk to it are chosen here, as in Lazarette and Semaphore.</p>
          <Toggle label="Show the orb" hint="Shows what the crew is doing. Drag it anywhere; click it (or press and hold) to talk. Off: the Crew button opens the panel." checked={orb.on} onChange={(on) => setOrbPrefs({ on })} />
          {orb.on ? (
            <div className="grid gap-3 border-l-2 border-line-2 pl-4">
              <Row id="s-orb-look" label="Look">
                <Select id="s-orb-look" className="w-48" value={orb.look} onChange={(e) => setOrbPrefs({ look: e.target.value as OrbLook })}>
                  {ORB_LOOKS.map((l) => <option key={l.id} value={l.id}>{l.label}</option>)}
                </Select>
              </Row>
              <Row id="s-orb-theme" label="Colours">
                <Select id="s-orb-theme" className="w-48" value={orb.theme} onChange={(e) => setOrbPrefs({ theme: e.target.value as OrbTheme })}>
                  {ORB_THEMES.map((t) => <option key={t.id} value={t.id}>{t.label}</option>)}
                </Select>
              </Row>
              <Row id="s-orb-talk" label="Talk to the orb" hint="Click it, or press and hold it while you speak.">
                <Select id="s-orb-talk" className="w-48" value={orb.talk} onChange={(e) => setOrbPrefs({ talk: e.target.value as OrbTalk })}>
                  <option value="click">Click</option><option value="hold">Press and hold</option>
                </Select>
              </Row>
            </div>
          ) : null}
        </div>
      </Section>
    </>
  );
}
