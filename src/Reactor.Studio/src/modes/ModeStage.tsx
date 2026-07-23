import { FlightsMode } from "./flights/FlightsMode";
import { TunnelMode } from "./tunnel/TunnelMode";
import type { Mode } from "../state/store";

export function ModeStage({ mode }: { mode: Exclude<Mode, "design"> }) {
  return mode === "flights" ? <FlightsMode /> : <TunnelMode />;
}
