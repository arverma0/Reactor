import type { ModelDescriptor, RunCost } from "../api/types";

const compact = (n: number) => (n >= 1_000_000 ? `${(n / 1_000_000).toFixed(n >= 10_000_000 ? 0 : 1)}M` : n >= 10_000 ? `${Math.round(n / 1000)}k` : Math.round(n).toLocaleString("en-US"));

/** Dollars for display: "$0.93", "$12.40", "$1,234"; amounts under a cent keep their first significant digits ("$0.004"). */
export function formatUsd(usd: number): string {
  if (!Number.isFinite(usd)) return "—";
  const abs = Math.abs(usd);
  if (abs === 0) return "$0.00";
  if (abs < 0.0001) return usd < 0 ? "−<$0.0001" : "<$0.0001";
  if (abs < 0.01) return `$${usd.toFixed(-Math.floor(Math.log10(abs)))}`;
  if (abs >= 1000) return `$${Math.round(usd).toLocaleString("en-US")}`;
  return `$${usd.toFixed(2)}`;
}

/** AI units for display: one decimal under 10, whole units above. */
export function formatAiUnits(units: number): string {
  return `${units.toFixed(units < 10 ? 1 : 0)} AIU`;
}

/**
 * Short cost text for lists: dollars when known (`cost_usd`, normalized by every backend, so runs on different backends
 * compare), else AI units, premium requests, or tokens.
 */
export function costText(cost: RunCost | null | undefined): string | null {
  if (!cost) return null;
  if (cost.cost_usd !== undefined) return formatUsd(cost.cost_usd);
  if (cost.ai_units !== undefined) return formatAiUnits(cost.ai_units);
  if (cost.premium_requests !== undefined) return `${cost.premium_requests} req`;
  const tokens = (cost.input_tokens ?? 0) + (cost.output_tokens ?? 0);
  return tokens > 0 ? `${compact(tokens)} tok` : null;
}

/** The native units behind a cost (for details and tooltips): "93.1 AIU · 15 premium req · 236k in / 29k out tok". */
export function costUnitsText(cost: RunCost | null | undefined): string | null {
  if (!cost) return null;
  const parts: string[] = [];
  if (cost.ai_units !== undefined) parts.push(formatAiUnits(cost.ai_units));
  if (cost.premium_requests !== undefined) parts.push(`${cost.premium_requests} premium req`);
  if (cost.input_tokens !== undefined || cost.output_tokens !== undefined) parts.push(`${compact(cost.input_tokens ?? 0)} in / ${compact(cost.output_tokens ?? 0)} out tok`);
  return parts.length > 0 ? parts.join(" · ") : null;
}

/**
 * Every usage total, dollars first (as the engine's ledger orders them): "$4.50 · 93.1 AIU · 15 premium req · 236k in /
 * 29k out tok · 180k cached · 12 model calls · 40 tool calls"; null when nothing was recorded.
 */
export function usageText(cost: RunCost | null | undefined): string | null {
  if (!cost) return null;
  const parts: string[] = [];
  if (cost.cost_usd !== undefined) parts.push(formatUsd(cost.cost_usd));
  const units = costUnitsText(cost);
  if (units) parts.push(units);
  if (cost.cache_read_tokens) parts.push(`${compact(cost.cache_read_tokens)} cached`);
  if (cost.reasoning_tokens) parts.push(`${compact(cost.reasoning_tokens)} reasoning`);
  if (cost.model_calls !== undefined) parts.push(`${compact(cost.model_calls)} model call${cost.model_calls === 1 ? "" : "s"}`);
  if (cost.tool_calls !== undefined) parts.push(`${compact(cost.tool_calls)} tool call${cost.tool_calls === 1 ? "" : "s"}`);
  return parts.length > 0 ? parts.join(" · ") : null;
}

const rate = (usd: number) => (usd >= 1 ? `${Number(usd.toFixed(2))}` : usd.toFixed(Math.abs(usd * 100 - Math.round(usd * 100)) < 1e-9 ? 2 : 3));

const COST_ORDER = ["cost_usd", "ai_units", "premium_requests"];

/** A stage's mean cost per visit (flight record): dollars first (comparable across backends), then native units and counts. */
export function perVisitCost(cost: Record<string, number>): string {
  return Object.entries(cost)
    .sort(([a], [b]) => (COST_ORDER.indexOf(a) + 1 || 99) - (COST_ORDER.indexOf(b) + 1 || 99))
    .map(([k, v]) => (k === "cost_usd" ? formatUsd(v) : k === "ai_units" ? `${v.toFixed(2)} AIU` : `${Math.round(v).toLocaleString("en-US")} ${k.replace(/_/g, " ")}`))
    .join(" · ");
}

/**
 * The price shown next to a model in pickers: USD per 1M input/output tokens ("$4/$20"), with the Copilot billing
 * multiplier when the runtime reports one ("1× · $2/$10"); undefined when neither is known.
 */
export function modelPriceText(m: Pick<ModelDescriptor, "input_cost_per_million" | "output_cost_per_million" | "billing_multiplier">): string | undefined {
  const tokens = m.input_cost_per_million != null && m.output_cost_per_million != null ? `$${rate(m.input_cost_per_million)}/$${rate(m.output_cost_per_million)}` : undefined;
  const multiplier = m.billing_multiplier != null ? `${m.billing_multiplier}×` : undefined;
  return multiplier && tokens ? `${multiplier} · ${tokens}` : (multiplier ?? tokens);
}

/** A tooltip explaining {@link modelPriceText}. */
export function modelPriceTitle(m: Pick<ModelDescriptor, "input_cost_per_million" | "cached_input_cost_per_million" | "cache_write_cost_per_million" | "output_cost_per_million" | "price_source">): string | undefined {
  if (m.input_cost_per_million == null || m.output_cost_per_million == null) return undefined;
  const lines = [`USD per 1M tokens: input $${rate(m.input_cost_per_million)}, output $${rate(m.output_cost_per_million)}`];
  if (m.cached_input_cost_per_million != null) lines.push(`cached input $${rate(m.cached_input_cost_per_million)}`);
  if (m.cache_write_cost_per_million != null) lines.push(`cache write $${rate(m.cache_write_cost_per_million)}`);
  lines.push(m.price_source === "price_table" ? "from Reactor's Copilot price table" : "from the model catalog");
  return lines.join("\n");
}
