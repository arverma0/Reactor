import type { CSSProperties, ReactNode } from "react";

/** Geometric pictograms on a 16×16 grid, 1.5 strokes with round joins. */
const PATHS: Record<string, ReactNode> = {
  // node kinds
  start: (
    <>
      <circle cx="8" cy="8" r="5.4" />
      <circle cx="8" cy="8" r="2" fill="currentColor" stroke="none" />
    </>
  ),
  exit: (
    <>
      <rect x="2.6" y="2.6" width="10.8" height="10.8" rx="1.6" />
      <rect x="5.9" y="5.9" width="4.2" height="4.2" rx="0.6" fill="currentColor" stroke="none" />
    </>
  ),
  agent: (
    <>
      <path d="M13.2 8.6a5.25 5.25 0 1 1-1.55-4.27" />
      <path d="M12.9 2.4v2.9H10" />
      <circle cx="8" cy="8.2" r="1.7" fill="currentColor" stroke="none" />
    </>
  ),
  tool: (
    <>
      <path d="M3 4.4 6.6 8 3 11.6" />
      <path d="M8.6 11.6H13" />
    </>
  ),
  human: (
    <>
      <circle cx="8" cy="5.1" r="2.45" />
      <path d="M3.1 13.6c.65-2.75 2.55-4.15 4.9-4.15s4.25 1.4 4.9 4.15" />
    </>
  ),
  router: (
    <>
      <path d="M1.8 8h4.4" />
      <path d="M6.2 8 9.7 4.5h4.5" />
      <path d="M6.2 8l3.5 3.5h4.5" />
      <circle cx="6.2" cy="8" r="1.1" fill="currentColor" stroke="none" />
    </>
  ),
  fanout: (
    <>
      <path d="M1.8 8h3.6" />
      <path d="M5.4 8c2.6 0 3-4.4 5.6-4.4h3.2" />
      <path d="M5.4 8h8.8" />
      <path d="M5.4 8c2.6 0 3 4.4 5.6 4.4h3.2" />
    </>
  ),
  join: (
    <>
      <path d="M1.8 3.6H5c2.6 0 3 4.4 5.6 4.4" />
      <path d="M1.8 8h8.8" />
      <path d="M1.8 12.4H5c2.6 0 3-4.4 5.6-4.4" />
      <path d="M10.6 8h3.6" />
    </>
  ),
  supervisor: (
    <>
      <path d="M1.7 8c1.6-3 3.7-4.6 6.3-4.6S12.7 5 14.3 8c-1.6 3-3.7 4.6-6.3 4.6S3.3 11 1.7 8Z" />
      <circle cx="8" cy="8" r="2" />
    </>
  ),
  custom: <path d="M8 1.8l5.4 3.1v6.2L8 14.2l-5.4-3.1V4.9Z" />,

  // modes & panels
  design: (
    <>
      <circle cx="8" cy="3" r="1.3" />
      <path d="M7.4 4.2 3.8 14" />
      <path d="M8.6 4.2 12.2 14" />
      <path d="M5 10.6h6" />
    </>
  ),
  tunnel: (
    <>
      <path d="M1.8 5.2h7.4a2 2 0 1 0-2-2" />
      <path d="M1.8 8.2h10a2.1 2.1 0 1 1-2.1 2.1" />
      <path d="M1.8 11.2h4.4" />
    </>
  ),
  flights: (
    <>
      <path d="M2 13.4C4.6 7.2 8.4 4 13.2 3.2" />
      <circle cx="13.2" cy="3.2" r="1.5" fill="currentColor" stroke="none" />
      <path d="M2 13.4h4" strokeDasharray="1 2" />
    </>
  ),
  library: (
    <>
      <rect x="2.4" y="2.4" width="4.6" height="4.6" rx="1" />
      <rect x="9" y="2.4" width="4.6" height="4.6" rx="1" />
      <rect x="2.4" y="9" width="4.6" height="4.6" rx="1" />
      <path d="M11.3 9.2v4.2M9.2 11.3h4.2" />
    </>
  ),
  files: <path d="M2 4.2c0-.55.45-1 1-1h3.2l1.6 1.7H13c.55 0 1 .45 1 1v6.9c0 .55-.45 1-1 1H3c-.55 0-1-.45-1-1Z" />,
  outline: (
    <>
      <path d="M5.6 4h8.4M5.6 8h8.4M5.6 12h8.4" />
      <circle cx="2.6" cy="4" r=".9" fill="currentColor" stroke="none" />
      <circle cx="2.6" cy="8" r=".9" fill="currentColor" stroke="none" />
      <circle cx="2.6" cy="12" r=".9" fill="currentColor" stroke="none" />
    </>
  ),
  models: (
    <>
      <rect x="4.4" y="4.4" width="7.2" height="7.2" rx="1.2" />
      <path d="M6.6 1.9v2.5M9.4 1.9v2.5M6.6 11.6v2.5M9.4 11.6v2.5M1.9 6.6h2.5M1.9 9.4h2.5M11.6 6.6h2.5M11.6 9.4h2.5" />
    </>
  ),
  code: <path d="M5.6 4.4 2 8l3.6 3.6M10.4 4.4 14 8l-3.6 3.6" />,
  problems: (
    <>
      <path d="M8 2.4 14.2 13H1.8Z" />
      <path d="M8 6.4v3.2" />
      <path d="M8 11.3v.05" />
    </>
  ),
  prompt: (
    <>
      <path d="M2.5 3.5h11v7.2H8.4L5 13.4v-2.7H2.5Z" />
      <path d="M5 6.2h6M5 8.2h3.6" />
    </>
  ),
  architect: (
    <>
      <path d="M2.4 13.6V2.4l11.2 11.2Z" />
      <path d="M5.2 10.8V8.3l2.5 2.5Z" />
      <path d="M2.4 5.4h1.4M2.4 8h1.4" />
    </>
  ),

  // actions
  play: <path d="M5 3.1v9.8L13 8Z" fill="currentColor" />,
  stop: <rect x="4" y="4" width="8" height="8" rx="1.2" fill="currentColor" />,
  pause: <path d="M5.5 3.5v9M10.5 3.5v9" />,
  undo: <path d="M5.4 3.6 2.4 6.6l3 3M2.4 6.6h7.8a3.4 3.4 0 0 1 0 6.8H7" />,
  redo: <path d="M10.6 3.6l3 3-3 3M13.6 6.6H5.8a3.4 3.4 0 0 0 0 6.8H9" />,
  layout: (
    <>
      <rect x="1.8" y="2.6" width="4.6" height="3.6" rx="0.8" />
      <rect x="9.6" y="9.8" width="4.6" height="3.6" rx="0.8" />
      <path d="M4.1 6.2v4.4c0 .55.45 1 1 1h4.5" />
      <path d="M8.1 9.9l1.5 1.7-1.5 1.7" />
    </>
  ),
  fit: <path d="M2 6V2h4M10 2h4v4M14 10v4h-4M6 14H2v-4" />,
  plus: <path d="M8 3v10M3 8h10" />,
  minus: <path d="M3 8h10" />,
  close: <path d="M4.2 4.2l7.6 7.6M11.8 4.2l-7.6 7.6" />,
  check: <path d="M3 8.6l3.1 3.1L13 4.8" />,
  cross: <path d="M4.5 4.5l7 7M11.5 4.5l-7 7" />,
  chevronDown: <path d="M4 6.2 8 10l4-3.8" />,
  chevronRight: <path d="M6.2 4 10 8l-3.8 4" />,
  chevronLeft: <path d="M9.8 4 6 8l3.8 4" />,
  chevronUp: <path d="M4 9.8 8 6l4 3.8" />,
  search: (
    <>
      <circle cx="7" cy="7" r="4.4" />
      <path d="M10.3 10.3 14 14" />
    </>
  ),
  note: (
    <>
      <path d="M2.6 2.6h10.8v7.2l-3.6 3.6H2.6Z" />
      <path d="M9.8 13.4V9.8h3.6" />
    </>
  ),
  group: <path d="M2.5 4.5v-2h2M7 2.5h2M11.5 2.5h2v2M13.5 7v2M13.5 11.5v2h-2M9 13.5H7M4.5 13.5h-2v-2M2.5 9V7" />,
  data: (
    <>
      <circle cx="8" cy="8" r="2.8" />
      <path d="M1.6 8h3.6M10.8 8h3.6" />
    </>
  ),
  save: <path d="M8 2.4v7.4M5 7l3 3 3-3M2.6 11v2.6h10.8V11" />,
  open: <path d="M2 4.2c0-.55.45-1 1-1h3.2l1.6 1.7H13c.55 0 1 .45 1 1v1.4H4.4L2 12.8Z" />,
  more: (
    <>
      <circle cx="3.5" cy="8" r="1.1" fill="currentColor" stroke="none" />
      <circle cx="8" cy="8" r="1.1" fill="currentColor" stroke="none" />
      <circle cx="12.5" cy="8" r="1.1" fill="currentColor" stroke="none" />
    </>
  ),
  steer: (
    <>
      <circle cx="8" cy="8" r="5.4" />
      <circle cx="8" cy="8" r="1.4" />
      <path d="M8 2.6v4M8 9.4v4M2.6 8h4M9.4 8h4" />
    </>
  ),
  fork: (
    <>
      <circle cx="4.2" cy="3.4" r="1.5" />
      <circle cx="4.2" cy="12.6" r="1.5" />
      <circle cx="11.8" cy="5.4" r="1.5" />
      <path d="M4.2 4.9v6.2M11.8 6.9c0 2.6-2.2 3.6-4.6 3.6H5.6" />
    </>
  ),
  clock: (
    <>
      <circle cx="8" cy="8" r="5.6" />
      <path d="M8 4.8v3.4l2.1 1.3" />
    </>
  ),
  retry: <path d="M13 8.4A5 5 0 1 1 11.5 4.6M13 2.4v2.8h-2.8" />,
  flag: <path d="M3.5 14V2.4M3.5 3h8.2L9.6 6l2.1 3H3.5" />,
  thread: (
    <>
      <path d="M6.6 9.4l2.8-2.8" />
      <path d="M7.2 4.6l1-1a2.6 2.6 0 0 1 3.7 3.7l-1 1" />
      <path d="M8.8 11.4l-1 1a2.6 2.6 0 0 1-3.7-3.7l1-1" />
    </>
  ),
  bolt: <path d="M9 1.6 3.4 9H8l-1 5.4L12.6 7H8Z" />,
  eye: (
    <>
      <path d="M1.7 8c1.6-3 3.7-4.6 6.3-4.6S12.7 5 14.3 8c-1.6 3-3.7 4.6-6.3 4.6S3.3 11 1.7 8Z" />
      <circle cx="8" cy="8" r="2" />
    </>
  ),
  key: (
    <>
      <circle cx="5" cy="8" r="2.8" />
      <path d="M7.8 8H14M11.6 8v2.4M13.6 8v1.8" />
    </>
  ),
  external: <path d="M9 2.6h4.4V7M13.4 2.6 7.6 8.4M11.4 9.6v3.8H2.6V4.6h3.8" />,
  trash: <path d="M2.6 4.2h10.8M6.2 4.2V2.6h3.6v1.6M4 4.2l.7 9.2h6.6l.7-9.2" />,
  copy: (
    <>
      <rect x="5.4" y="5.4" width="8.2" height="8.2" rx="1.2" />
      <path d="M10.6 5.4V3.4c0-.55-.45-1-1-1H3.4c-.55 0-1 .45-1 1v6.2c0 .55.45 1 1 1h2" />
    </>
  ),
  spark: <path d="M8 1.6c.5 3.4 2.3 5.4 5.6 6-3.3.6-5.1 2.6-5.6 6.2-.5-3.6-2.3-5.6-5.6-6.2 3.3-.6 5.1-2.6 5.6-6Z" />,
  dice: (
    <>
      <rect x="2.4" y="2.4" width="11.2" height="11.2" rx="2.2" />
      <circle cx="5.6" cy="5.6" r=".9" fill="currentColor" stroke="none" />
      <circle cx="10.4" cy="10.4" r=".9" fill="currentColor" stroke="none" />
      <circle cx="8" cy="8" r=".9" fill="currentColor" stroke="none" />
    </>
  ),
  gauge: (
    <>
      <path d="M2.4 11.6a5.6 5.6 0 1 1 11.2 0" />
      <path d="M8 11.6 10.8 6.6" />
    </>
  ),
  console: (
    <>
      <rect x="1.8" y="2.6" width="12.4" height="10.8" rx="1.4" />
      <path d="M4.4 6.2 6.6 8l-2.2 1.8M8 10h3.4" />
    </>
  ),
  link: <path d="M6.4 9.6 9.6 6.4M7 4.4l1.2-1.2a2.7 2.7 0 0 1 3.8 3.8L10.8 8.2M9 11.6l-1.2 1.2A2.7 2.7 0 0 1 4 9l1.2-1.2" />,
  sliders: <path d="M2.4 4.4h6.2M11.6 4.4h2M2.4 11.6h2M7.4 11.6h6.2M10.1 2.8v3.2M5.9 10v3.2" />,
};

export type GlyphName = keyof typeof PATHS;

export function Glyph({ name, size = 16, className, style, title }: { name: string; size?: number; className?: string; style?: CSSProperties; title?: string }) {
  return (
    <svg
      className={className}
      style={style}
      width={size}
      height={size}
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden={title ? undefined : true}
      role={title ? "img" : undefined}
    >
      {title ? <title>{title}</title> : null}
      {PATHS[name] ?? PATHS.custom}
    </svg>
  );
}

/** The Reactor mark: a containment ring, crosshair, and the signal core. */
export function Mark({ size = 22 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden>
      <circle cx="12" cy="12" r="8.6" stroke="var(--ink-0)" strokeWidth="1.5" />
      <path d="M12 2.2v4.2M12 17.6v4.2M2.2 12h4.2M17.6 12h4.2" stroke="var(--ink-0)" strokeWidth="1.5" strokeLinecap="round" />
      <circle cx="12" cy="12" r="3" fill="var(--signal)" />
    </svg>
  );
}
