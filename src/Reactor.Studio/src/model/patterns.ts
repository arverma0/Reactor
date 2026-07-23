import { addEdge, addGroup, addNode, uniqueNodeId } from "./doc";
import type { NodeKind } from "./handlers";
import { KINDS } from "./handlers";
import type { Attrs, StudioDoc } from "./types";

export interface PatternNode {
  key: string;
  kind: NodeKind;
  attrs: Attrs;
  x: number;
  y: number;
}

export interface PatternSpec {
  id: string;
  name: string;
  blurb: string;
  /** Glyph for the library. */
  icon: string;
  nodes: PatternNode[];
  edges: { from: string; to: string; attrs?: Attrs }[];
  /** Wrap the inserted stages in a group with this label. */
  group?: string;
}

const shape = (kind: NodeKind): Attrs => (kind === "agent" ? {} : { shape: KINDS[kind].shape });

export const PATTERNS: PatternSpec[] = [
  {
    id: "plan-build-verify",
    name: "Plan · build · verify",
    blurb: "A plan file feeds the build; a real test command gates it; failures loop to a fixer.",
    icon: "agent",
    group: "Build loop",
    nodes: [
      { key: "plan", kind: "agent", x: 0, y: 0, attrs: { label: "Plan", prompt: "Read the goal ($goal) and the repository. Write PLAN.md: small, ordered, independently verifiable steps.", outputs: "plan:PLAN.md" } },
      { key: "implement", kind: "agent", x: 340, y: 0, attrs: { label: "Implement", prompt: "Implement PLAN.md step by step. Keep the build green after every step.", inputs: "plan", goal_gate: "true", max_retries: "1" } },
      { key: "verify", kind: "tool", x: 680, y: 0, attrs: { label: "Run tests", tool_command: "dotnet test", timeout: "15m", outputs: "test_report" } },
      { key: "fix", kind: "agent", x: 680, y: 230, attrs: { label: "Fix", prompt: "The tests failed. Read test_report, find the root cause, and fix the code — not the tests, unless a test is wrong.", inputs: "test_report, plan" } },
    ],
    edges: [
      { from: "plan", to: "implement" },
      { from: "implement", to: "verify" },
      { from: "verify", to: "fix", attrs: { condition: "outcome=fail" } },
      { from: "fix", to: "verify" },
    ],
  },
  {
    id: "test-fix",
    name: "Test–fix loop",
    blurb: "Run a command; on failure an agent fixes and the command runs again.",
    icon: "tool",
    nodes: [
      { key: "test", kind: "tool", x: 0, y: 0, attrs: { label: "Run tests", tool_command: "npm test", timeout: "10m", outputs: "test_report" } },
      { key: "fix", kind: "agent", x: 0, y: 230, attrs: { label: "Fix failures", prompt: "The test command failed. Read test_report and fix the code.", inputs: "test_report" } },
    ],
    edges: [
      { from: "test", to: "fix", attrs: { condition: "outcome=fail" } },
      { from: "fix", to: "test" },
    ],
  },
  {
    id: "design-fight",
    name: "Design fight",
    blurb: "Two designers compete in parallel; a judge picks the stronger design.",
    icon: "fanout",
    group: "Design fight",
    nodes: [
      { key: "contest", kind: "fanout", x: 0, y: 120, attrs: { label: "Competing designs", join_policy: "wait_all" } },
      { key: "design_a", kind: "agent", x: 300, y: 0, attrs: { label: "Design A", prompt: "Propose the simplest design that fully meets $goal. Write DESIGN-A.md with the approach, trade-offs, and risks.", outputs: "design_a:DESIGN-A.md", reasoning_effort: "high" } },
      { key: "design_b", kind: "agent", x: 300, y: 240, attrs: { label: "Design B", prompt: "Propose a robust, extensible design for $goal. Write DESIGN-B.md with the approach, trade-offs, and risks.", outputs: "design_b:DESIGN-B.md", reasoning_effort: "high" } },
      { key: "judge", kind: "join", x: 640, y: 120, attrs: { label: "Judge", prompt: "Compare the competing designs (parallel.results, DESIGN-A.md, DESIGN-B.md). Pick the one most likely to ship correctly. Reply BEST: <branch id> and one paragraph of rationale." } },
    ],
    edges: [
      { from: "contest", to: "design_a" },
      { from: "contest", to: "design_b" },
      { from: "design_a", to: "judge" },
      { from: "design_b", to: "judge" },
    ],
  },
  {
    id: "review-panel",
    name: "Review panel",
    blurb: "Specialist reviewers in parallel; their findings are gathered and applied.",
    icon: "join",
    group: "Review panel",
    nodes: [
      { key: "reviews", kind: "fanout", x: 0, y: 200, attrs: { label: "Reviews", join_policy: "wait_all", max_parallel: "3" } },
      { key: "security_review", kind: "agent", x: 300, y: 0, attrs: { label: "Security review", prompt: "Review the change for security problems. List concrete findings with files and lines.", outputs: "security_findings" } },
      { key: "performance_review", kind: "agent", x: 300, y: 200, attrs: { label: "Performance review", prompt: "Review the change for performance problems. List concrete findings with files and lines.", outputs: "performance_findings" } },
      { key: "clarity_review", kind: "agent", x: 300, y: 400, attrs: { label: "Clarity review", prompt: "Review the change for readability and maintainability. List concrete findings.", outputs: "clarity_findings" } },
      { key: "gather", kind: "join", x: 640, y: 200, attrs: { label: "Gather" } },
      { key: "apply", kind: "agent", x: 900, y: 200, attrs: { label: "Apply findings", prompt: "Apply the worthwhile findings. Skip the ones that are wrong and say why.", inputs: "security_findings, performance_findings, clarity_findings" } },
    ],
    edges: [
      { from: "reviews", to: "security_review" },
      { from: "reviews", to: "performance_review" },
      { from: "reviews", to: "clarity_review" },
      { from: "security_review", to: "gather" },
      { from: "performance_review", to: "gather" },
      { from: "clarity_review", to: "gather" },
      { from: "gather", to: "apply" },
    ],
  },
  {
    id: "sign-off",
    name: "Human sign-off",
    blurb: "A person approves, or sends the work back with the revision loop.",
    icon: "human",
    nodes: [
      { key: "signoff", kind: "human", x: 0, y: 0, attrs: { label: "Ship it?", "human.timeout": "4h", "human.default_choice": "revise" } },
      { key: "revise", kind: "agent", x: 0, y: 240, attrs: { label: "Revise", prompt: "The reviewer asked for changes. Address their concerns, then summarize what changed." } },
    ],
    edges: [
      { from: "signoff", to: "revise", attrs: { label: "[R] Revise" } },
      { from: "revise", to: "signoff" },
    ],
  },
  {
    id: "critic-loop",
    name: "Critic loop",
    blurb: "A critic approves or returns the draft; the writer revises until it passes.",
    icon: "retry",
    nodes: [
      { key: "draft", kind: "agent", x: 0, y: 0, attrs: { label: "Draft", prompt: "Produce the first draft for $goal.", outputs: "draft" } },
      { key: "critique", kind: "agent", x: 340, y: 0, attrs: { label: "Critique", prompt: "Critique the draft against the goal. If it meets the bar, choose Approve. Otherwise choose Revise and list what must change.", inputs: "draft", outputs: "critique" } },
      { key: "revise", kind: "agent", x: 340, y: 240, attrs: { label: "Revise", prompt: "Revise the draft to address every point in the critique.", inputs: "draft, critique", outputs: "draft" } },
    ],
    edges: [
      { from: "draft", to: "critique" },
      { from: "critique", to: "revise", attrs: { label: "Revise" } },
      { from: "revise", to: "critique" },
    ],
  },
  {
    id: "best-of-n",
    name: "Best of three",
    blurb: "Three independent attempts at the same task; a judge keeps the best.",
    icon: "dice",
    group: "Best of three",
    nodes: [
      { key: "attempts", kind: "fanout", x: 0, y: 200, attrs: { label: "Three attempts", max_parallel: "3" } },
      { key: "attempt_1", kind: "agent", x: 300, y: 0, attrs: { label: "Attempt 1", prompt: "$goal" } },
      { key: "attempt_2", kind: "agent", x: 300, y: 200, attrs: { label: "Attempt 2", prompt: "$goal" } },
      { key: "attempt_3", kind: "agent", x: 300, y: 400, attrs: { label: "Attempt 3", prompt: "$goal" } },
      { key: "pick", kind: "join", x: 640, y: 200, attrs: { label: "Pick the best", prompt: "Compare the three attempts (parallel.results). Reply BEST: <branch id> and why." } },
    ],
    edges: [
      { from: "attempts", to: "attempt_1" },
      { from: "attempts", to: "attempt_2" },
      { from: "attempts", to: "attempt_3" },
      { from: "attempt_1", to: "pick" },
      { from: "attempt_2", to: "pick" },
      { from: "attempt_3", to: "pick" },
    ],
  },
  {
    id: "spec-first",
    name: "Spec first",
    blurb: "Write the spec, then the tests that prove it, then the code that passes them.",
    icon: "flag",
    nodes: [
      { key: "spec", kind: "agent", x: 0, y: 0, attrs: { label: "Spec", prompt: "Write SPEC.md for $goal: inputs, outputs, edge cases, and examples.", outputs: "spec:SPEC.md" } },
      { key: "tests", kind: "agent", x: 340, y: 0, attrs: { label: "Tests first", prompt: "Write failing tests that pin down every behavior in SPEC.md.", inputs: "spec" } },
      { key: "build", kind: "agent", x: 680, y: 0, attrs: { label: "Make them pass", prompt: "Implement the code so every test passes. Do not weaken the tests.", inputs: "spec", goal_gate: "true" } },
    ],
    edges: [
      { from: "spec", to: "tests" },
      { from: "tests", to: "build" },
    ],
  },
  {
    id: "supervisor",
    name: "Supervisor",
    blurb: "Runs a child pipeline, watches it, and steers it when it runs long.",
    icon: "supervisor",
    nodes: [
      { key: "supervise", kind: "supervisor", x: 0, y: 0, attrs: { label: "Supervise worker", "manager.poll_interval": "30s", "manager.actions": "observe,steer,wait", "manager.steer_prompt": "Wrap up: finish the current change, make sure it builds, and stop." } },
    ],
    edges: [],
  },
];

/** Inserts a pattern at a canvas position with fresh, unique ids. */
export function insertPattern(doc: StudioDoc, spec: PatternSpec, at: { x: number; y: number }): { doc: StudioDoc; ids: string[] } {
  const taken = new Set(doc.nodes.map((n) => n.id));
  const ids = new Map<string, string>();
  let next = doc;
  for (const node of spec.nodes) {
    const id = uniqueNodeId(next, node.key, taken);
    taken.add(id);
    ids.set(node.key, id);
    next = addNode(next, { id, attrs: { ...shape(node.kind), ...node.attrs }, group: null }, { x: Math.round(at.x + node.x), y: Math.round(at.y + node.y) });
  }
  for (const edge of spec.edges) {
    next = addEdge(next, ids.get(edge.from)!, ids.get(edge.to)!, { ...(edge.attrs ?? {}) }).doc;
  }
  if (spec.group) next = addGroup(next, spec.group, [...ids.values()]).doc;
  return { doc: next, ids: [...ids.values()] };
}
