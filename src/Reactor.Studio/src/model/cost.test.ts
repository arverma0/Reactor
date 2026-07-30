import { describe, expect, it } from "vitest";
import { costText, costUnitsText, formatUsd, modelPriceText, modelPriceTitle, perVisitCost, usageText } from "./cost";

describe("cost formatting", () => {
  it("formats dollars for lists, keeping small amounts readable", () => {
    expect(formatUsd(0.93)).toBe("$0.93");
    expect(formatUsd(12.4)).toBe("$12.40");
    expect(formatUsd(1234.4)).toBe("$1,234");
    expect(formatUsd(0.004)).toBe("$0.004");
    expect(formatUsd(0)).toBe("$0.00");
    expect(formatUsd(0.00001)).toBe("<$0.0001");
  });

  it("prefers dollars so runs on different backends compare, then native units", () => {
    expect(costText({ ai_units: 93.05, cost_usd: 0.9305, input_tokens: 236_000 })).toBe("$0.93");
    expect(costText({ ai_units: 93.05 })).toBe("93 AIU");
    expect(costText({ ai_units: 3.25 })).toBe("3.3 AIU");
    expect(costText({ premium_requests: 15 })).toBe("15 req");
    expect(costText({ input_tokens: 236_000, output_tokens: 29_000 })).toBe("265k tok");
    expect(costText({})).toBeNull();
    expect(costText(null)).toBeNull();
  });

  it("lists the native units behind a dollar figure", () => {
    expect(costUnitsText({ cost_usd: 0.93, ai_units: 93.05, premium_requests: 15, input_tokens: 236_000, output_tokens: 29_000 })).toBe("93 AIU · 15 premium req · 236k in / 29k out tok");
    expect(costUnitsText({ cost_usd: 1 })).toBeNull();
  });

  it("lists every usage total with dollars first", () => {
    expect(usageText({ tool_calls: 40, model_calls: 12, cache_read_tokens: 180_000, input_tokens: 236_000, output_tokens: 29_000, cost_usd: 4.5 })).toBe(
      "$4.50 · 236k in / 29k out tok · 180k cached · 12 model calls · 40 tool calls",
    );
    expect(usageText({ model_calls: 1 })).toBe("1 model call");
    expect(usageText({})).toBeNull();
    expect(usageText(null)).toBeNull();
  });

  it("orders a stage's per-visit cost with dollars first", () => {
    expect(perVisitCost({ input_tokens: 1200.4, ai_units: 2.5, cost_usd: 0.0251 })).toBe("$0.03 · 2.50 AIU · 1,200 input tokens");
  });

  it("shows model prices per 1M tokens, with the runtime multiplier when known", () => {
    expect(modelPriceText({ input_cost_per_million: 4, output_cost_per_million: 20 })).toBe("$4/$20");
    expect(modelPriceText({ input_cost_per_million: 0.1, output_cost_per_million: 0.5 })).toBe("$0.10/$0.50");
    expect(modelPriceText({ input_cost_per_million: 1.75, output_cost_per_million: 14, billing_multiplier: 1 })).toBe("1× · $1.75/$14");
    expect(modelPriceText({ billing_multiplier: 0.33 })).toBe("0.33×");
    expect(modelPriceText({})).toBeUndefined();
    expect(modelPriceTitle({ input_cost_per_million: 0.75, cached_input_cost_per_million: 0.075, output_cost_per_million: 3.75, price_source: "price_table" })).toBe(
      "USD per 1M tokens: input $0.75, output $3.75\ncached input $0.075\nfrom Reactor's Copilot price table",
    );
  });
});
