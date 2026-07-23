import { Canvas } from "../canvas/Canvas";
import type { Mode } from "../state/store";

export function ModeStage({ mode }: { mode: Exclude<Mode, "design"> }) {
  return (
    <div className="rs-mode-stage">
      <Canvas overlay={{ kind: mode === "tunnel" ? "sim" : "replay", nodes: {}, edges: {} }} readOnly />
    </div>
  );
}
