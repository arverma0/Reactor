/** A stage visit's record of what it changed in its working directory (`<stage>/changes.json`). */
export interface StageChanges {
  mode: string;
  workspace: string;
  workspaceLabel: string;
  added: number;
  modified: number;
  deleted: number;
  entries: ChangeEntry[];
  /** The listing stops short of every change (counts are still complete), or a file walk hit its limit. */
  truncated: boolean;
  /** Other stages changed the same directory at the same time, so some of these changes may be theirs. */
  concurrentWith: string[];
  concurrent: boolean;
  scope?: { patterns: string[]; enforced: boolean; violations: string[]; attributed: string[] };
  promotion?: { branch: string; files?: number; conflicts: string[]; error?: string };
  skipped: string[];
  before?: string;
  after?: string;
  error?: string;
}

export interface ChangeEntry {
  path: string;
  change: "added" | "modified" | "deleted";
  size?: number;
  oldSize?: number;
  outOfScope: boolean;
  type?: string;
}

const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? v : 0);
const str = (v: unknown) => (typeof v === "string" ? v : "");
const strings = (v: unknown) => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
const record = (v: unknown) => (v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : undefined);
const snapshotId = (v: unknown) => {
  const r = record(v);
  return r ? str(r.commit) || str(r.id) || undefined : undefined;
};

/** Reads a changes.json document (a string or parsed JSON); null when it is not one. */
export function parseChanges(raw: unknown): StageChanges | null {
  let value = raw;
  if (typeof value === "string") {
    try {
      value = JSON.parse(value);
    } catch {
      return null;
    }
  }
  const json = record(value);
  if (!json || !Array.isArray(json.changes)) return null;
  const summary = record(json.summary) ?? {};
  const scope = record(json.write_scope);
  const violations = new Set(strings(scope?.violations));
  const promotion = record(json.promotion);
  const entries: ChangeEntry[] = json.changes
    .map(record)
    .filter((c): c is Record<string, unknown> => !!c && typeof c.path === "string")
    .map((c) => ({
      path: str(c.path),
      change: c.change === "added" || c.change === "deleted" ? c.change : "modified",
      size: typeof c.size === "number" ? c.size : undefined,
      oldSize: typeof c.old_size === "number" ? c.old_size : undefined,
      outOfScope: c.out_of_scope === true || violations.has(str(c.path)),
      type: typeof c.type === "string" ? c.type : undefined,
    }));
  return {
    mode: str(json.mode),
    workspace: str(json.workspace),
    workspaceLabel: str(json.workspace_label),
    added: num(summary.added),
    modified: num(summary.modified),
    deleted: num(summary.deleted),
    entries,
    truncated: json.truncated === true,
    concurrent: json.concurrent === true,
    concurrentWith: strings(json.concurrent_with),
    scope: scope ? { patterns: strings(scope.patterns), enforced: scope.enforced === true, violations: [...violations], attributed: strings(scope.attributed_to_concurrent) } : undefined,
    promotion: promotion
      ? { branch: str(promotion.branch), files: typeof promotion.files === "number" ? promotion.files : undefined, conflicts: strings(promotion.conflicts), error: str(promotion.error) || undefined }
      : undefined,
    skipped: strings(json.skipped),
    before: snapshotId(json.before),
    after: snapshotId(json.after),
    error: str(json.error) || undefined,
  };
}

/** "+2 ~1 −0" */
export const changeCounts = (c: { added: number; modified: number; deleted: number }) => `+${c.added} ~${c.modified} −${c.deleted}`;

/** Entries to show first: changes outside the write scope, then by path. */
export function orderedEntries(changes: StageChanges): ChangeEntry[] {
  return [...changes.entries].sort((a, b) => Number(b.outOfScope) - Number(a.outOfScope) || a.path.localeCompare(b.path));
}

/** A byte count for people: 512 B, 1.4 KB, 3.2 MB. */
export function formatBytes(bytes: number | undefined): string {
  if (bytes === undefined) return "";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
