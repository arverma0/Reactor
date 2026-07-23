/**
 * Transit-line palette (after Vignelli's subway diagram), tuned for a graphite background. Used for artifact pins and
 * conversation threads, which read like lines on a map.
 */
export const LINE_COLORS = [
  "#E8574D", // red
  "#F08A2E", // orange
  "#E9C24A", // yellow
  "#7CC46A", // green
  "#3DB5B0", // teal
  "#5B8DEF", // blue
  "#A07BE0", // violet
  "#E06FA0", // pink
  "#B8916B", // umber
  "#8FA3B3", // slate
] as const;

/** Stable small hash (FNV-1a). */
export function hashString(text: string): number {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return hash >>> 0;
}

export const lineColor = (name: string, override?: string): string => override ?? LINE_COLORS[hashString(name) % LINE_COLORS.length];
