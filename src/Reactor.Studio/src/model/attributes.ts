/** Values of engine attributes the Studio edits, validated the way the engine parses them. */

const DURATION = /^(\d+(?:\.\d+)?)\s*(ms|s|m|h|d)?$/;
const UNIT_MS: Record<string, number> = { ms: 1, s: 1000, "": 1000, m: 60_000, h: 3_600_000, d: 86_400_000 };

/** Milliseconds of a duration attribute (`250ms`, `30s`, `5m`, `2h`, `1d`; a bare number is seconds), or null. */
export function parseDuration(text: string | undefined): number | null {
  const match = DURATION.exec((text ?? "").trim());
  return match ? Number(match[1]) * UNIT_MS[match[2] ?? ""] : null;
}

export const isDuration = (text: string | undefined) => parseDuration(text) !== null;

/** A duration in its most natural single unit, as the engine writes them (`15m`, `90s`, `1500ms`). */
export function formatDurationAttr(ms: number): string {
  if (ms % 1000 !== 0) return `${Math.round(ms)}ms`;
  const s = ms / 1000;
  if (s > 0 && s % 86400 === 0) return `${s / 86400}d`;
  if (s > 0 && s % 3600 === 0) return `${s / 3600}h`;
  if (s > 0 && s % 60 === 0) return `${s / 60}m`;
  return `${s}s`;
}

/** What the engine assumes for `soft_timeout` without one: 80% of a timeout of 5 minutes or more, else none. */
export function defaultSoftTimeout(timeoutMs: number | null): string {
  if (timeoutMs === null || timeoutMs < 300_000) return "off";
  return formatDurationAttr(Math.round((timeoutMs * 0.8) / 1000) * 1000);
}

/** What the engine assumes for `timeout_grace` without one: 10% of the timeout, between 1 and 15 minutes (at most the timeout). */
export function defaultTimeoutGrace(timeoutMs: number | null): string {
  if (timeoutMs === null) return "10% of timeout";
  return formatDurationAttr(Math.min(timeoutMs, Math.round(Math.min(900_000, Math.max(60_000, timeoutMs * 0.1)) / 1000) * 1000));
}

/** `soft_timeout` values that turn the reminder off. */
export const isOff = (text: string | undefined) => ["off", "none", "false", "no", "0", "0s"].includes((text ?? "").trim().toLowerCase());

/** `soft_timeout`: a duration or `off`. */
export const isSoftTimeout = (text: string | undefined) => isOff(text) || isDuration(text);

/** `max_cost`: a non-negative number of US dollars (`25`, `7.5`, `$12`). */
export function parseUsd(text: string | undefined): number | null {
  const trimmed = (text ?? "").trim().replace(/^\$/, "");
  if (!/^\d+(\.\d+)?$|^\.\d+$/.test(trimmed)) return null;
  const n = Number(trimmed);
  return Number.isFinite(n) && n >= 0 ? n : null;
}

/** `max_visits`: a positive whole number. */
export function parsePositiveInt(text: string | undefined): number | null {
  const trimmed = (text ?? "").trim();
  return /^\d+$/.test(trimmed) && Number(trimmed) > 0 ? Number(trimmed) : null;
}

/** `metrics_file`: a path inside the working directory (relative, without `..`). */
export function metricsFileProblem(text: string | undefined): string | null {
  const path = (text ?? "").trim();
  if (!path) return null;
  if (/^([A-Za-z]:)?[\\/]/.test(path)) return "Use a path relative to the working directory.";
  if (path.split(/[\\/]/).includes("..")) return "The path may not leave the working directory (no “..”).";
  return null;
}
