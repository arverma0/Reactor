import { NodeResizer, type Node, type NodeProps } from "@xyflow/react";
import { memo, useEffect, useRef, useState } from "react";
import { cx } from "../components/controls";
import type { NoteData } from "./build";
import { canvasActions } from "./actions";

function NoteNodeImpl({ data, selected }: NodeProps<Node<NoteData>>) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(data.text);
  const ref = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    if (!editing) setText(data.text);
  }, [data.text, editing]);
  useEffect(() => {
    if (editing) ref.current?.focus();
  }, [editing]);
  return (
    <div className={cx("rs-note", selected && "is-selected")} style={{ ["--note" as string]: data.color ?? "#e8b84a" }} onDoubleClick={() => setEditing(true)}>
      <NodeResizer isVisible={selected} minWidth={160} minHeight={80} lineClassName="rs-note__resize-line" handleClassName="rs-note__resize-handle" />
      {editing ? (
        <textarea
          ref={ref}
          className="rs-note__editor nodrag nowheel"
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            e.stopPropagation();
            if (e.key === "Escape") setEditing(false);
          }}
          onBlur={() => {
            setEditing(false);
            if (text !== data.text) canvasActions.editNote(data.id, text);
          }}
        />
      ) : (
        <div className="rs-note__text">{data.text || <span className="faint">Double-click to write a note…</span>}</div>
      )}
    </div>
  );
}

export const NoteNode = memo(NoteNodeImpl);
