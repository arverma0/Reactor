// Terminal output (tool stdout, agent tool results, failure reasons) can carry ANSI escape sequences; rendered as is,
// they show up as boxes ("□[39m"). Mirrors the engine's Reactor.Execution.AnsiText.

const isControl = (c: number) => c < 0x20 || (c >= 0x7f && c <= 0x9f);

/** Matches any character stripAnsi removes or interprets (control characters other than tab and newline). */
const CONTROL = /[\x00-\x08\x0b-\x1f\x7f-\x9f]/;

/**
 * Plain text from terminal output, as a terminal would show it: ANSI/VT escape sequences (CSI such as colors and cursor
 * movement, OSC such as hyperlinks and titles, DCS/SOS/PM/APC strings, other escapes; 7- and 8-bit forms) are removed,
 * a carriage return inside a line starts the line over (progress output keeps its last state), a backspace removes the
 * character before it, CRLF becomes LF, and other control characters are dropped. Tabs and newlines are kept.
 */
export function stripAnsi(text: string | null | undefined): string {
  if (!text) return "";
  if (!CONTROL.test(text)) return text;
  const lines: string[] = [];
  let line = "";
  let i = 0;
  while (i < text.length) {
    const c = text.charCodeAt(i);
    if (c === 0x0a) {
      lines.push(line);
      line = "";
      i++;
    } else if (c === 0x0d) {
      let next = i;
      while (next < text.length && text.charCodeAt(next) === 0x0d) next++;
      // A carriage return inside a line (not part of a line ending): what follows overwrites the line.
      if (!endsLine(text, next)) line = "";
      i = next;
    } else if (c === 0x08) {
      line = line.slice(0, -1);
      i++;
    } else if (c === 0x09) {
      line += "\t";
      i++;
    } else if (c === 0x1b) {
      i = skipEscape(text, i + 1);
    } else if (c === 0x9b) {
      i = skipControlSequence(text, i + 1);
    } else if (c === 0x90 || c === 0x98 || c === 0x9d || c === 0x9e || c === 0x9f) {
      i = skipControlString(text, i + 1);
    } else if (isControl(c)) {
      i++;
    } else {
      let end = i + 1;
      while (end < text.length && !isControl(text.charCodeAt(end))) end++;
      line += text.slice(i, end);
      i = end;
    }
  }
  lines.push(line);
  return lines.join("\n");
}

/**
 * Raw terminal output made legible without interpreting it: escape and other control characters are shown as Unicode
 * control pictures (ESC → ␛), so `\u001b[31m` reads as `␛[31m`. Tabs and line endings are kept.
 */
export function showControls(text: string | null | undefined): string {
  if (!text) return "";
  return text.replace(/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/g, (ch) => String.fromCharCode(ch === "\x7f" ? 0x2421 : 0x2400 + ch.charCodeAt(0)));
}

// True when only carriage returns and escape sequences (which print nothing) are left before the end of the line.
function endsLine(text: string, i: number): boolean {
  while (i < text.length) {
    const c = text.charCodeAt(i);
    if (c === 0x0a) return true;
    if (c === 0x0d) i++;
    else if (c === 0x1b) i = skipEscape(text, i + 1);
    else if (c === 0x9b) i = skipControlSequence(text, i + 1);
    else if (c === 0x90 || c === 0x98 || c === 0x9d || c === 0x9e || c === 0x9f) i = skipControlString(text, i + 1);
    else return false;
  }
  return true;
}

// After ESC: CSI and control strings, nF sequences (intermediate bytes, then a final byte), and two-character escapes.
function skipEscape(text: string, i: number): number {
  if (i >= text.length) return i;
  const c = text.charCodeAt(i);
  if (c === 0x5b) return skipControlSequence(text, i + 1); // [
  if (c === 0x5d || c === 0x50 || c === 0x58 || c === 0x5e || c === 0x5f) return skipControlString(text, i + 1); // ] P X ^ _
  if (c >= 0x20 && c <= 0x2f) {
    while (i < text.length && text.charCodeAt(i) >= 0x20 && text.charCodeAt(i) <= 0x2f) i++;
    return i < text.length && text.charCodeAt(i) >= 0x30 && text.charCodeAt(i) <= 0x7e ? i + 1 : i;
  }
  // A lone ESC (before a control or non-ASCII character) is dropped by itself.
  return c >= 0x30 && c <= 0x7e ? i + 1 : i;
}

// CSI: parameter bytes 0x30-0x3F, intermediate bytes 0x20-0x2F, then a final byte 0x40-0x7E.
function skipControlSequence(text: string, i: number): number {
  while (i < text.length && text.charCodeAt(i) >= 0x30 && text.charCodeAt(i) <= 0x3f) i++;
  while (i < text.length && text.charCodeAt(i) >= 0x20 && text.charCodeAt(i) <= 0x2f) i++;
  return i < text.length && text.charCodeAt(i) >= 0x40 && text.charCodeAt(i) <= 0x7e ? i + 1 : i;
}

// OSC, DCS, SOS, PM, and APC strings end at BEL or ST (ESC \ or 0x9C); an unterminated one ends at the end of the line
// or at the next escape sequence.
function skipControlString(text: string, i: number): number {
  while (i < text.length) {
    const c = text.charCodeAt(i);
    if (c === 0x07 || c === 0x9c) return i + 1;
    if (c === 0x1b) return text.charCodeAt(i + 1) === 0x5c ? i + 2 : i;
    if (c === 0x0a) return i;
    i++;
  }
  return i;
}
