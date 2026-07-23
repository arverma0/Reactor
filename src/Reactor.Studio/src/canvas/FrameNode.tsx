import type { Node, NodeProps } from "@xyflow/react";
import { memo } from "react";
import { cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import type { FrameData } from "./build";

function FrameNodeImpl({ data, selected }: NodeProps<Node<FrameData>>) {
  return (
    <div className={cx("rs-frame", selected && "is-selected")} style={{ ["--depth" as string]: data.depth }}>
      <div className="rs-frame__tab">
        <Glyph name="group" size={12} />
        <span className="rs-frame__label">{data.label}</span>
        {data.className ? <span className="rs-frame__class mono">.{data.className}</span> : null}
        {data.model ? <span className="rs-frame__model mono">{data.model}</span> : null}
      </div>
    </div>
  );
}

export const FrameNode = memo(FrameNodeImpl);
