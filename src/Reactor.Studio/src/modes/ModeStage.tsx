import { Canvas } from "../canvas/Canvas";
import type { Mode } from "../state/store";
import { FlightsMode } from "./flights/FlightsMode";

export function ModeStage({ mode }: { mode: Exclude<Mode, "design"> }) {
  if (mode === "flights") return <FlightsMode />;
  return (
    <div className="rs-mode-stage">
      <Canvas overlay={{ kind: "sim", nodes: {}, edges: {} }} readOnly />
    </div>
  );
}
