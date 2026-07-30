/**
 * Where a re-fly runs. Mirrors the server's policy for forks (PipelineRunManager) so the Re-fly dialog can say which
 * directory a fork will use before it is started; the server still decides.
 *
 * - By default a fork continues in the flight's own working directory (from its manifest); a flight that did not record
 *   one (older run directories) re-flies in the server's working directory.
 * - The server may always use its own directories: its working directory and anything under its `.reactor` directory
 *   (the Architect's scratch directories). Any other directory needs a server started with `--allow-client-workdir`.
 * - A directory other than the flight's must be confirmed: the files the flight worked on may not be there.
 */

export type WorkdirOrigin = "source_run" | "request" | "server";

export interface ForkWorkdirInput {
  /** The flight's recorded working directory, or null when it did not record one. */
  source: string | null;
  /** The server's working directory. */
  server: string;
  /** The server lets clients choose working directories (`--allow-client-workdir`). */
  allowClient: boolean;
  /** A directory chosen instead of the default (absolute), if any. */
  override?: string | null;
}

export interface ForkWorkdirPlan {
  /** The directory the fork runs in. */
  directory: string;
  origin: WorkdirOrigin;
  /** Why the server will refuse the directory, or null. */
  blocked: string | null;
  /** The directory is not the flight's, so the user has to confirm it. */
  needsConfirmation: boolean;
  /** Something to tell the user about the choice, or null. */
  note: string | null;
}

const isWindowsPath = (path: string) => /^[A-Za-z]:([\\/]|$)|^\\\\/.test(path);

/** True for a full path (`C:\…`, `\\server\share\…`, or `/…`). */
export const isAbsolutePath = (path: string) => isWindowsPath(path.trim()) || path.trim().startsWith("/");

/** Trims whitespace and trailing separators (keeping roots); Windows paths use backslashes. */
export function normalizePath(path: string): string {
  const trimmed = path.trim();
  const windows = isWindowsPath(trimmed);
  let out = windows ? trimmed.replace(/\//g, "\\") : trimmed;
  const root = windows ? (/^[A-Za-z]:\\/.test(out) ? 3 : 2) : 1;
  while (out.length > root && /[\\/]$/.test(out)) out = out.slice(0, -1);
  return out;
}

const key = (path: string) => {
  const normalized = normalizePath(path);
  return isWindowsPath(normalized) ? normalized.toLowerCase() : normalized;
};

/** Same directory, ignoring trailing separators (and case and separator style for Windows paths). */
export const samePath = (a: string, b: string) => key(a) === key(b);

/** `path` is strictly inside `root`. */
export function isWithin(path: string, root: string): boolean {
  const inner = key(path);
  const outer = key(root);
  const separator = isWindowsPath(outer) ? "\\" : "/";
  return inner.length > outer.length && inner.startsWith(outer.endsWith(separator) ? outer : outer + separator);
}

/** A directory the server may use without `--allow-client-workdir`: its working directory or one under its `.reactor`. */
export function isServerDirectory(directory: string, server: string): boolean {
  const base = normalizePath(server);
  const separator = isWindowsPath(base) ? "\\" : "/";
  return samePath(directory, base) || isWithin(directory, `${base.endsWith(separator) ? base : base + separator}.reactor`);
}

/** A long path shortened to its last directories (`…\project\atlas`), at most about `max` characters. */
export function shortPath(path: string, max = 44): string {
  const normalized = normalizePath(path);
  if (normalized.length <= max) return normalized;
  const separator = isWindowsPath(normalized) ? "\\" : "/";
  const parts = normalized.split(/[\\/]/).filter(Boolean);
  let tail = parts[parts.length - 1] ?? normalized;
  for (let i = parts.length - 2; i >= 0; i--) {
    const longer = `${parts[i]}${separator}${tail}`;
    if (longer.length + 2 > max) break;
    tail = longer;
  }
  return `…${separator}${tail}`;
}

/** What the dialog says when the server refuses a fork's working directory (its own error is phrased for API clients). */
export function describeForkRefusal(
  refusal: { code: string; error: string; working_directory?: string },
  context: { source: string | null; server: string; allowClient: boolean },
): string {
  const directory = refusal.working_directory ?? "";
  const elsewhere = context.allowClient ? "the server's directory or another one" : `the server's directory (${context.server})`;
  switch (refusal.code) {
    case "working_directory_missing":
      return context.source && samePath(directory, context.source)
        ? `This flight's directory, ${context.source}, no longer exists. Restore it, or re-fly in ${elsewhere}.`
        : `${directory || "The directory"} does not exist.`;
    case "working_directory_not_allowed":
      return context.source && samePath(directory, context.source)
        ? `The server may not use this flight's directory, ${context.source}. Restart it from that directory or with --allow-client-workdir, or re-fly in ${elsewhere}.`
        : `The server may not use ${directory || "this directory"}: it was not started with --allow-client-workdir.`;
    case "working_directory_confirmation_required":
      return "This is not the flight's directory: confirm below to re-fly here anyway.";
    default:
      return refusal.error;
  }
}

export function planForkWorkdir({ source, server, allowClient, override }: ForkWorkdirInput): ForkWorkdirPlan {
  const requested = override?.trim() ? normalizePath(override) : null;
  const flown = source?.trim() ? normalizePath(source) : null;
  const directory = requested ?? flown ?? normalizePath(server);
  const origin: WorkdirOrigin = requested ? "request" : flown ? "source_run" : "server";
  const permitted = allowClient || isServerDirectory(directory, server);
  let blocked: string | null = null;
  if (!permitted) {
    blocked =
      origin === "request"
        ? `This server can only use its own working directory (${server}). Start it with --allow-client-workdir to re-fly elsewhere.`
        : `This flight worked in ${flown}, but the server works in ${server} and does not let clients choose another working directory. ` +
          `To re-fly there, restart the server from that directory or with --allow-client-workdir — or re-fly in the server's directory.`;
  }
  return {
    directory,
    origin,
    blocked,
    needsConfirmation: !!flown && !samePath(directory, flown),
    note: flown
      ? null
      : `This flight did not record its working directory, so it re-flies in ${origin === "server" ? "the server's working directory" : "the directory you chose"}. Check that it has the files the flight worked on.`,
  };
}
