import { useState } from "react";
import { api } from "../../api/client";
import { Button, TextInput } from "../../components/controls";
import { Glyph } from "../../components/Glyph";
import { parseUsd } from "../../model/attributes";
import { formatUsd } from "../../model/cost";
import { toast } from "../../state/store";
import type { BudgetRecord } from "./replay";
import { refreshRuns, selectRun } from "./store";
import { suggestBudget, type FlightView } from "./view";

/**
 * A run that stopped at its cost budget: what it spent, where it stopped, and — for a run this server knows — a resume
 * with a higher budget (`POST /pipelines/{id}/resume` with `max_cost`), which continues at the stage it stopped before.
 */
export function BudgetStop({ view, budget, label }: { view: FlightView; budget: BudgetRecord; label: (id: string) => string }) {
  const [value, setValue] = useState(() => String(suggestBudget(budget.spent, budget.limit)));
  const [busy, setBusy] = useState(false);
  const amount = parseUsd(value);
  const valid = amount !== null && amount > budget.spent;
  const canResume = !!view.liveId && !view.active && !view.simulated;
  const resume = async () => {
    if (!valid || !view.liveId) return;
    setBusy(true);
    try {
      await api.resume(view.liveId, amount!);
      toast(`Resumed with a budget of ${formatUsd(amount!)}.`, "success");
      selectRun(view.key);
      void refreshRuns();
    } catch (e) {
      toast((e as Error).message, "error");
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="rs-budgetstop">
      <div className="rs-budgetstop__head">
        <Glyph name="stop" size={12} /> Stopped at its budget
      </div>
      <p>
        <span className="num">{formatUsd(budget.spent)}</span> spent of a <span className="num">{formatUsd(budget.limit)}</span> budget
        {budget.next ? (
          <>
            {" "}
            — it stopped before <b>{label(budget.next)}</b> and can go on from there
          </>
        ) : null}
        .
      </p>
      {canResume ? (
        <div className="rs-budgetstop__resume">
          <span className="label">new budget</span>
          <span className="rs-budgetstop__amount">
            <span className="faint">$</span>
            <TextInput value={value} onChange={setValue} onCommit={setValue} mono invalid={!valid} />
          </span>
          <Button size="sm" variant="primary" icon="play" disabled={!valid || busy} onClick={() => void resume()} title={valid ? undefined : `More than the ${formatUsd(budget.spent)} already spent`}>
            Resume with a higher budget
          </Button>
        </div>
      ) : view.dir && !view.simulated ? (
        <p className="faint">
          Resume it with a higher budget from the command line: <span className="mono">reactor resume .reactor/runs/{view.dir} --max-cost {suggestBudget(budget.spent, budget.limit)}</span>
        </p>
      ) : null}
    </div>
  );
}
