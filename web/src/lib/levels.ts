import type { LevelToken } from './schemas';

// The hierarchy, top to bottom. Order matters: it drives sorting, indentation and the level bars.
export const LEVELS: LevelToken[] = ['THEME', 'EPIC', 'FEATURE', 'STORY', 'TASK'];

export const LEVEL_LABEL: Record<LevelToken, string> = {
  THEME: 'Theme',
  EPIC: 'Epic',
  FEATURE: 'Feature',
  STORY: 'Story',
  TASK: 'Task',
  BUG: 'Bug',
  UNCLASSIFIED: 'Unclassified',
};

export const LEVEL_PLURAL: Record<LevelToken, string> = {
  THEME: 'Themes',
  EPIC: 'Epics',
  FEATURE: 'Features',
  STORY: 'Stories',
  TASK: 'Tasks',
  BUG: 'Bugs',
  UNCLASSIFIED: 'Unclassified',
};

/** CSS variable carrying the level's hue; usable in style={{ '--level': levelVar(t) }} or as a fill. */
export function levelVar(type: LevelToken | string): string {
  switch (type) {
    case 'THEME': return 'var(--lv-theme)';
    case 'EPIC': return 'var(--lv-epic)';
    case 'FEATURE': return 'var(--lv-feature)';
    case 'STORY': return 'var(--lv-story)';
    case 'TASK': return 'var(--lv-task)';
    case 'BUG': return 'var(--lv-bug)';
    default: return 'var(--lv-none)';
  }
}

export function levelDepth(type: LevelToken | string): number {
  const i = LEVELS.indexOf(type as LevelToken);
  return i < 0 ? 5 : i;
}

export function expectedParent(type: LevelToken | string): LevelToken | null {
  switch (type) {
    case 'EPIC': return 'THEME';
    case 'FEATURE': return 'EPIC';
    case 'STORY': return 'FEATURE';
    case 'TASK': return 'STORY';
    default: return null;
  }
}

export function isLevel(value: string): value is LevelToken {
  return value in LEVEL_LABEL;
}
