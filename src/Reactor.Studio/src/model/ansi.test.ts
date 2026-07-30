import { describe, expect, it } from "vitest";
import { showControls, stripAnsi } from "./ansi";

const ESC = "\u001b";

describe("stripAnsi", () => {
  it("removes escape sequences of every kind", () => {
    expect(stripAnsi(`${ESC}[31mred${ESC}[0m`)).toBe("red");
    expect(stripAnsi(`${ESC}[1;38;2;255;0;0mbold${ESC}[22;39m`)).toBe("bold");
    expect(stripAnsi(`${ESC}[?25lcursor${ESC}[?25h`)).toBe("cursor");
    expect(stripAnsi(`x${ESC}[2 qy`)).toBe("xy");
    expect(stripAnsi("\u009b31mC1\u009b0m")).toBe("C1");
    expect(stripAnsi(`${ESC}]0;title\u0007text`)).toBe("text");
    expect(stripAnsi(`${ESC}]8;;https://example.com${ESC}\\link${ESC}]8;;${ESC}\\`)).toBe("link");
    expect(stripAnsi(`${ESC}Pq#0;2${ESC}\\done`)).toBe("done");
    expect(stripAnsi(`${ESC}(Bascii${ESC}7${ESC}c`)).toBe("ascii");
    expect(stripAnsi(`cut ${ESC}[31`)).toBe("cut ");
    expect(stripAnsi(`${ESC}]0;never terminated\nnext`)).toBe("\nnext");
  });

  it("renders the flight-log line from the backlog report legibly", () => {
    const raw = `Tool command exited with code 1: …${ESC}[39m\n     ${ESC}[90m24|${ESC}[39m       ${ESC}[35mconst${ESC}[39m env ${ESC}[33m=${ESC}[39m envelope(sys${ESC}[33m.${ESC}[39mid)`;
    expect(stripAnsi(raw)).toBe("Tool command exited with code 1: …\n     24|       const env = envelope(sys.id)");
  });

  it("keeps the last state of carriage-return progress lines and normalizes line endings", () => {
    expect(stripAnsi("10%\r50%\r100%\n")).toBe("100%\n");
    expect(stripAnsi(`Downloading\r${ESC}[KDone\nnext`)).toBe("Done\nnext");
    expect(stripAnsi("one\r\ntwo\r\n")).toBe("one\ntwo\n");
    expect(stripAnsi(`colored\r${ESC}[0m\r\nnext`)).toBe("colored\nnext");
    expect(stripAnsi("end\r")).toBe("end");
  });

  it("applies backspaces and drops other control characters", () => {
    expect(stripAnsi("ab\bc")).toBe("ac");
    expect(stripAnsi("line\n\bnext")).toBe("line\nnext");
    expect(stripAnsi("bell\u0007 nul\u0000 del\u007f\ttab")).toBe("bell nul del\ttab");
  });

  it("returns plain text unchanged", () => {
    const text = "PASS  src/a.test.ts\n✓ unicode — ✗ ×\n";
    expect(stripAnsi(text)).toBe(text);
    expect(stripAnsi(undefined)).toBe("");
    expect(stripAnsi(null)).toBe("");
  });
});

describe("showControls", () => {
  it("shows escape characters as control pictures and keeps line endings", () => {
    expect(showControls(`${ESC}[31mred${ESC}[0m\r\nnext\tline\u0007`)).toBe("␛[31mred␛[0m\r\nnext\tline␇");
  });
});
