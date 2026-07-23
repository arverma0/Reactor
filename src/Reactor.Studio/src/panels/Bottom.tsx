import { useEffect, useMemo, useState } from "react";
import { api } from "../api/client";
import { Button, Empty, Segmented, Spinner, cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { findNode, handlerOf, nodeLabel, startNode } from "../model/doc";
import { writeDot } from "../model/dot";
import type { StudioDoc } from "../model/types";
import { focusNode, select, useStudio } from "../state/store";

export function Problems() {
  const analysis = useStudio((s) => s.analysis);
  const doc = useStudio((s) => s.doc);
  const items = analysis.diagnostics;
  if (analysis.parseError) {
    return (
      <div className="rs-problems">
        <div className="rs-problem is-error">
          <span className="rs-problem__sev" />
          <span className="rs-problem__rule mono">syntax</span>
          <span className="rs-problem__msg">{analysis.parseError.message}</span>
        </div>
      </div>
    );
  }
  if (items.length === 0) {
    return <Empty title={analysis.status === "offline" ? "Can't reach the server to validate." : "No problems."}>{analysis.status === "offline" ? "Validation runs on the Reactor server." : "Every rule of the engine's validator passes."}</Empty>;
  }
  return (
    <div className="rs-problems">
      {items.map((d, i) => {
        const node = d.node_id ? findNode(doc, d.node_id) : undefined;
        const edge = d.edge ? doc.edges.find((e) => `${e.from} -> ${e.to}` === d.edge) : undefined;
        return (
          <button
            key={i}
            type="button"
            className={cx("rs-problem", `is-${d.severity}`)}
            onClick={() => {
              if (node) focusNode(node.id);
              else if (edge) select({ edges: [edge.uid] });
            }}
          >
            <span className="rs-problem__sev" />
            <span className="rs-problem__rule mono">{d.rule}</span>
            <span className="rs-problem__msg">
              {d.message}
              {d.fix ? <span className="rs-problem__fix"> — {d.fix}</span> : null}
            </span>
            <span className="rs-problem__where mono">{node ? nodeLabel(doc, node) : d.edge ?? "pipeline"}</span>
          </button>
        );
      })}
    </div>
  );
}

/** A plausible path from start to `target` (BFS over routes), used as the "earlier stages" of a prompt preview. */
function pathTo(doc: StudioDoc, target: string): string[] {
  const start = startNode(doc);
  if (!start) return [];
  const previous = new Map<string, string>();
  const queue = [start.id];
  const seen = new Set(queue);
  while (queue.length) {
    const id = queue.shift()!;
    if (id === target) break;
    for (const e of doc.edges.filter((x) => x.from === id)) {
      if (!seen.has(e.to)) {
        seen.add(e.to);
        previous.set(e.to, id);
        queue.push(e.to);
      }
    }
  }
  if (!previous.has(target)) return [];
  const path: string[] = [];
  let current = previous.get(target);
  while (current) {
    path.unshift(current);
    current = previous.get(current);
  }
  return path;
}

/** Shows exactly what the selected stage's agent would receive: preamble, task, artifact sections, status protocol. */
export function PromptPreview() {
  const doc = useStudio((s) => s.doc);
  const selected = useStudio((s) => s.selection.nodes[0]);
  const node = selected ? findNode(doc, selected) : undefined;
  const [fidelity, setFidelity] = useState<string>("node");
  const [result, setResult] = useState<{ prompt: string; fidelity: string; status_protocol: boolean } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const isLlm = node ? handlerOf(doc, node) === "codergen" : false;
  const history = useMemo(() => (node ? pathTo(doc, node.id) : []), [doc, node]);
  const dot = useMemo(() => writeDot(doc), [doc]);

  useEffect(() => {
    if (!node || !isLlm) return;
    let cancelled = false;
    setLoading(true);
    const timer = setTimeout(() => {
      api
        .promptPreview(dot, node.id, history, fidelity === "node" ? undefined : fidelity)
        .then((r) => {
          if (!cancelled) {
            setResult(r);
            setError(null);
          }
        })
        .catch((e) => !cancelled && setError((e as Error).message))
        .finally(() => !cancelled && setLoading(false));
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [dot, node, isLlm, history, fidelity]);

  if (!node || !isLlm) {
    return <Empty title="Select an agent stage.">The preview shows the exact prompt the stage's agent receives — context preamble, task, artifact hand-offs, and the status-file protocol.</Empty>;
  }
  return (
    <div className="rs-preview">
      <div className="rs-preview__bar">
        <span className="rs-preview__title">
          <Glyph name="prompt" size={13} /> {nodeLabel(doc, node)}
          <span className="faint"> as reached via {history.length ? history.map((id) => findNode(doc, id) ? nodeLabel(doc, findNode(doc, id)!) : id).join(" → ") : "the start"}</span>
        </span>
        <Segmented
          size="sm"
          value={fidelity}
          options={[
            { value: "node", label: "as configured" },
            { value: "full", label: "full" },
            { value: "compact", label: "compact" },
            { value: "summary:high", label: "summary" },
            { value: "truncate", label: "truncate" },
          ]}
          onChange={setFidelity}
        />
        {loading ? <Spinner /> : null}
        {result ? (
          <span className="rs-preview__meta mono">
            {result.fidelity} · {result.prompt.length.toLocaleString()} chars · ~{Math.ceil(result.prompt.length / 4).toLocaleString()} tokens{result.status_protocol ? " · status protocol" : ""}
          </span>
        ) : null}
        <Button size="sm" variant="ghost" icon="copy" title="Copy" onClick={() => result && void navigator.clipboard.writeText(result.prompt)} />
      </div>
      {error ? <div className="rs-preview__error">{error}</div> : null}
      <pre className="rs-preview__text">{result?.prompt ?? ""}</pre>
    </div>
  );
}
