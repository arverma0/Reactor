import { useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Glyph } from "./Glyph";

export function cx(...classes: (string | false | null | undefined)[]) {
  return classes.filter(Boolean).join(" ");
}

type ButtonVariant = "default" | "primary" | "ghost" | "danger" | "quiet";

export function Button({
  children,
  variant = "default",
  icon,
  onClick,
  disabled,
  title,
  size = "md",
  className,
  type = "button",
  active,
  style,
}: {
  children?: ReactNode;
  variant?: ButtonVariant;
  icon?: string;
  onClick?: (e: React.MouseEvent<HTMLButtonElement>) => void;
  disabled?: boolean;
  title?: string;
  size?: "sm" | "md";
  className?: string;
  type?: "button" | "submit";
  active?: boolean;
  style?: CSSProperties;
}) {
  return (
    <button
      type={type}
      className={cx("rs-btn", `rs-btn--${variant}`, `rs-btn--${size}`, !children && "rs-btn--icon", active && "is-active", className)}
      onClick={onClick}
      disabled={disabled}
      title={title}
      style={style}
    >
      {icon ? <Glyph name={icon} size={size === "sm" ? 14 : 16} /> : null}
      {children ? <span className="rs-btn__text">{children}</span> : null}
    </button>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="rs-kbd">{children}</kbd>;
}

export function Segmented<T extends string>({
  value,
  options,
  onChange,
  size = "md",
  className,
}: {
  value: T | undefined;
  options: { value: T; label: ReactNode; title?: string }[];
  onChange: (value: T) => void;
  size?: "sm" | "md";
  className?: string;
}) {
  return (
    <div className={cx("rs-seg", `rs-seg--${size}`, className)} role="radiogroup">
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          role="radio"
          aria-checked={option.value === value}
          className={cx("rs-seg__item", option.value === value && "is-on")}
          title={option.title}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

export function Toggle({ checked, onChange, label, hint }: { checked: boolean; onChange: (value: boolean) => void; label?: ReactNode; hint?: ReactNode }) {
  return (
    <label className={cx("rs-toggle", checked && "is-on")}>
      <button type="button" role="switch" aria-checked={checked} className="rs-toggle__track" onClick={() => onChange(!checked)}>
        <span className="rs-toggle__thumb" />
      </button>
      {label ? (
        <span className="rs-toggle__text">
          <span>{label}</span>
          {hint ? <span className="rs-toggle__hint">{hint}</span> : null}
        </span>
      ) : null}
    </label>
  );
}

export function Stepper({ value, onChange, min = 0, max = 99, placeholder }: { value: number | undefined; onChange: (value: number | undefined) => void; min?: number; max?: number; placeholder?: string }) {
  const clamp = (n: number) => Math.max(min, Math.min(max, n));
  return (
    <div className="rs-stepper">
      <button type="button" onClick={() => onChange(value === undefined ? min : value <= min ? undefined : clamp(value - 1))} aria-label="Decrease">
        <Glyph name="minus" size={12} />
      </button>
      <span className={cx("rs-stepper__value num", value === undefined && "is-empty")}>{value === undefined ? placeholder ?? "—" : value}</span>
      <button type="button" onClick={() => onChange(clamp((value ?? min - 1) + 1))} aria-label="Increase">
        <Glyph name="plus" size={12} />
      </button>
    </div>
  );
}

export function Field({ label, hint, children, inline, aside }: { label: ReactNode; hint?: ReactNode; children: ReactNode; inline?: boolean; aside?: ReactNode }) {
  return (
    <div className={cx("rs-field", inline && "rs-field--inline")}>
      <div className="rs-field__head">
        <span className="rs-field__label">{label}</span>
        {aside ? <span className="rs-field__aside">{aside}</span> : null}
      </div>
      <div className="rs-field__body">{children}</div>
      {hint ? <div className="rs-field__hint">{hint}</div> : null}
    </div>
  );
}

/** A text input that commits on every keystroke but keeps local state so caret position survives store updates. */
export function TextInput({
  value,
  onChange,
  placeholder,
  mono,
  onCommit,
  invalid,
  autoFocus,
  className,
  spellCheck = false,
}: {
  value: string;
  onChange?: (value: string) => void;
  onCommit?: (value: string) => void;
  placeholder?: string;
  mono?: boolean;
  invalid?: boolean;
  autoFocus?: boolean;
  className?: string;
  spellCheck?: boolean;
}) {
  const [local, setLocal] = useState(value);
  const focused = useRef(false);
  useEffect(() => {
    if (!focused.current) setLocal(value);
  }, [value]);
  return (
    <input
      className={cx("rs-input", mono && "mono", invalid && "is-invalid", className)}
      value={local}
      placeholder={placeholder}
      spellCheck={spellCheck}
      autoFocus={autoFocus}
      onFocus={() => (focused.current = true)}
      onBlur={() => {
        focused.current = false;
        if (onCommit && local !== value) onCommit(local);
      }}
      onKeyDown={(e) => {
        if (e.key === "Enter" && onCommit) (e.target as HTMLInputElement).blur();
        if (e.key === "Escape") {
          setLocal(value);
          (e.target as HTMLInputElement).blur();
        }
        e.stopPropagation();
      }}
      onChange={(e) => {
        setLocal(e.target.value);
        onChange?.(e.target.value);
      }}
    />
  );
}

export function TextArea({
  value,
  onChange,
  placeholder,
  mono,
  minRows = 3,
  maxRows = 18,
  className,
}: {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  mono?: boolean;
  minRows?: number;
  maxRows?: number;
  className?: string;
}) {
  const ref = useRef<HTMLTextAreaElement>(null);
  const [local, setLocal] = useState(value);
  const focused = useRef(false);
  useEffect(() => {
    if (!focused.current) setLocal(value);
  }, [value]);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "auto";
    const line = parseFloat(getComputedStyle(el).lineHeight) || 18;
    const pad = 16;
    el.style.height = `${Math.min(Math.max(el.scrollHeight, minRows * line + pad), maxRows * line + pad)}px`;
  }, [local, minRows, maxRows]);
  return (
    <textarea
      ref={ref}
      className={cx("rs-input rs-textarea", mono && "mono", className)}
      value={local}
      placeholder={placeholder}
      spellCheck={!mono}
      onFocus={() => (focused.current = true)}
      onBlur={() => (focused.current = false)}
      onKeyDown={(e) => e.stopPropagation()}
      onChange={(e) => {
        setLocal(e.target.value);
        onChange(e.target.value);
      }}
    />
  );
}

/** Positions a floating element under (or above) an anchor, in a portal. */
export function Popover({
  anchor,
  open,
  onClose,
  children,
  width,
  align = "start",
  className,
}: {
  anchor: HTMLElement | null;
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  width?: number | "anchor";
  align?: "start" | "end";
  className?: string;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [style, setStyle] = useState<CSSProperties>({ visibility: "hidden" });
  useLayoutEffect(() => {
    if (!open || !anchor) return;
    const place = () => {
      const rect = anchor.getBoundingClientRect();
      const el = ref.current;
      const w = width === "anchor" ? rect.width : width ?? el?.offsetWidth ?? 240;
      const h = el?.offsetHeight ?? 200;
      let top = rect.bottom + 6;
      if (top + h > window.innerHeight - 8 && rect.top - h - 6 > 8) top = rect.top - h - 6;
      let left = align === "end" ? rect.right - w : rect.left;
      left = Math.max(8, Math.min(left, window.innerWidth - w - 8));
      setStyle({ top, left, width: w, visibility: "visible" });
    };
    place();
    window.addEventListener("resize", place);
    return () => window.removeEventListener("resize", place);
  }, [open, anchor, width, align]);
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current?.contains(e.target as Node) || anchor?.contains(e.target as Node)) return;
      onClose();
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("mousedown", onDown, true);
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("mousedown", onDown, true);
      window.removeEventListener("keydown", onKey);
    };
  }, [open, anchor, onClose]);
  if (!open) return null;
  return createPortal(
    <div ref={ref} className={cx("rs-pop", className)} style={style} onKeyDown={(e) => e.stopPropagation()}>
      {children}
    </div>,
    document.body,
  );
}

export interface DropdownOption<T extends string> {
  value: T;
  label: ReactNode;
  /** Text used for filtering. */
  text?: string;
  detail?: ReactNode;
  group?: string;
  disabled?: boolean;
}

/** A searchable list picker. `allowCustom` lets the user type a value that is not in the list. */
export function Dropdown<T extends string>({
  value,
  options,
  onChange,
  placeholder = "Choose…",
  searchable,
  allowCustom,
  renderValue,
  mono,
  width,
  clearLabel,
}: {
  value: T | undefined;
  options: DropdownOption<T>[];
  onChange: (value: T | undefined) => void;
  placeholder?: string;
  searchable?: boolean;
  allowCustom?: boolean;
  renderValue?: (value: T | undefined) => ReactNode;
  mono?: boolean;
  width?: number;
  clearLabel?: string;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const anchor = useRef<HTMLButtonElement>(null);
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? options.filter((o) => (o.text ?? String(o.value)).toLowerCase().includes(q)) : options;
  }, [options, query]);
  const current = options.find((o) => o.value === value);
  const choose = (v: T | undefined) => {
    onChange(v);
    setOpen(false);
    setQuery("");
  };
  const custom = allowCustom && query.trim() && !options.some((o) => o.value === query.trim()) ? (query.trim() as T) : null;
  return (
    <>
      <button ref={anchor} type="button" className={cx("rs-select", mono && "mono", open && "is-open")} onClick={() => setOpen(!open)}>
        <span className={cx("rs-select__value", value === undefined && "is-placeholder")}>{renderValue ? renderValue(value) : current?.label ?? value ?? placeholder}</span>
        <Glyph name="chevronDown" size={12} />
      </button>
      <Popover anchor={anchor.current} open={open} onClose={() => setOpen(false)} width={width ?? "anchor"}>
        {searchable || allowCustom ? (
          <div className="rs-pop__search">
            <Glyph name="search" size={13} />
            <input
              autoFocus
              value={query}
              placeholder={allowCustom ? "Search or type a value…" : "Search…"}
              onChange={(e) => {
                setQuery(e.target.value);
                setActive(0);
              }}
              onKeyDown={(e) => {
                if (e.key === "ArrowDown") setActive((a) => Math.min(a + 1, filtered.length - 1));
                else if (e.key === "ArrowUp") setActive((a) => Math.max(a - 1, 0));
                else if (e.key === "Enter") {
                  if (custom && filtered.length === 0) choose(custom);
                  else if (filtered[active] && !filtered[active].disabled) choose(filtered[active].value);
                } else if (e.key === "Escape") setOpen(false);
              }}
            />
          </div>
        ) : null}
        <div className="rs-pop__list">
          {clearLabel ? (
            <button type="button" className={cx("rs-pop__item", value === undefined && "is-selected")} onClick={() => choose(undefined)}>
              <span className="faint">{clearLabel}</span>
            </button>
          ) : null}
          {custom ? (
            <button type="button" className="rs-pop__item" onClick={() => choose(custom)}>
              <span>
                Use <span className="mono">“{custom}”</span>
              </span>
            </button>
          ) : null}
          {filtered.map((option, i) => {
            const header = option.group && (i === 0 || filtered[i - 1].group !== option.group) ? <div className="rs-pop__group">{option.group}</div> : null;
            return (
              <div key={option.value}>
                {header}
                <button
                  type="button"
                  disabled={option.disabled}
                  className={cx("rs-pop__item", option.value === value && "is-selected", i === active && "is-active")}
                  onMouseEnter={() => setActive(i)}
                  onClick={() => choose(option.value)}
                >
                  <span className="rs-pop__label">{option.label}</span>
                  {option.detail ? <span className="rs-pop__detail">{option.detail}</span> : null}
                </button>
              </div>
            );
          })}
          {filtered.length === 0 && !custom ? <div className="rs-pop__empty">No matches</div> : null}
        </div>
      </Popover>
    </>
  );
}

export interface ComboOption {
  value: string;
  group?: string;
  detail?: ReactNode;
}

/**
 * A free-text input with suggestions: typing filters them, arrow keys and Enter pick one, and any other text is kept as
 * typed. Suggestions show while the input has focus (all of them when the text is already one of them).
 */
export function ComboInput({
  value,
  onChange,
  options,
  placeholder,
  className,
  invalid,
  title,
  width = 260,
}: {
  value: string;
  onChange: (value: string) => void;
  options: ComboOption[];
  placeholder?: string;
  className?: string;
  invalid?: boolean;
  title?: string;
  width?: number;
}) {
  const ref = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);
  const filtered = useMemo(() => {
    const q = value.trim().toLowerCase();
    if (!q || options.some((o) => o.value === value)) return options;
    return options.filter((o) => o.value.toLowerCase().includes(q));
  }, [options, value]);
  const choose = (v: string) => {
    onChange(v);
    setOpen(false);
    setActive(-1);
  };
  return (
    <>
      <input
        ref={ref}
        className={cx(className, invalid && "is-invalid")}
        value={value}
        placeholder={placeholder}
        title={title}
        spellCheck={false}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
        onChange={(e) => {
          onChange(e.target.value);
          setOpen(true);
          setActive(-1);
        }}
        onKeyDown={(e) => {
          e.stopPropagation();
          if (e.key === "ArrowDown") {
            e.preventDefault();
            setOpen(true);
            setActive((a) => Math.min(a + 1, filtered.length - 1));
          } else if (e.key === "ArrowUp") {
            e.preventDefault();
            setActive((a) => Math.max(a - 1, 0));
          } else if (e.key === "Enter" && open && filtered[active]) {
            e.preventDefault();
            choose(filtered[active].value);
          } else if (e.key === "Escape") setOpen(false);
        }}
      />
      <Popover anchor={ref.current} open={open && filtered.length > 0} onClose={() => setOpen(false)} width={width}>
        <div className="rs-pop__list">
          {filtered.map((option, i) => (
            <div key={option.value}>
              {option.group && (i === 0 || filtered[i - 1].group !== option.group) ? <div className="rs-pop__group">{option.group}</div> : null}
              <button
                type="button"
                className={cx("rs-pop__item", option.value === value && "is-selected", i === active && "is-active")}
                onMouseDown={(e) => e.preventDefault()}
                onMouseEnter={() => setActive(i)}
                onClick={() => choose(option.value)}
              >
                <span className="rs-pop__label mono">{option.value}</span>
                {option.detail ? <span className="rs-pop__detail">{option.detail}</span> : null}
              </button>
            </div>
          ))}
        </div>
      </Popover>
    </>
  );
}

export function Menu({ items, onClose }: { items: ({ label: ReactNode; icon?: string; hint?: ReactNode; danger?: boolean; disabled?: boolean; run: () => void } | "divider")[]; onClose: () => void }) {
  return (
    <div className="rs-menu" role="menu">
      {items.map((item, i) =>
        item === "divider" ? (
          <div key={i} className="rs-menu__divider" />
        ) : (
          <button
            key={i}
            type="button"
            role="menuitem"
            className={cx("rs-menu__item", item.danger && "is-danger")}
            disabled={item.disabled}
            onClick={() => {
              item.run();
              onClose();
            }}
          >
            {item.icon ? <Glyph name={item.icon} size={14} /> : <span className="rs-menu__spacer" />}
            <span className="rs-menu__label">{item.label}</span>
            {item.hint ? <span className="rs-menu__hint">{item.hint}</span> : null}
          </button>
        ),
      )}
    </div>
  );
}

export function Dialog({ title, children, onClose, footer, width = 520, kicker }: { title: ReactNode; children: ReactNode; onClose: () => void; footer?: ReactNode; width?: number; kicker?: ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return createPortal(
    <div className="rs-dialog__scrim" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="rs-dialog" style={{ width }} role="dialog" onKeyDown={(e) => e.stopPropagation()}>
        <header className="rs-dialog__head">
          <div>
            {kicker ? <div className="label">{kicker}</div> : null}
            <h2>{title}</h2>
          </div>
          <Button variant="ghost" icon="close" onClick={onClose} title="Close" />
        </header>
        <div className="rs-dialog__body">{children}</div>
        {footer ? <footer className="rs-dialog__foot">{footer}</footer> : null}
      </div>
    </div>,
    document.body,
  );
}

export function Empty({ title, children, icon }: { title: ReactNode; children?: ReactNode; icon?: string }) {
  return (
    <div className="rs-empty">
      {icon ? <Glyph name={icon} size={20} /> : null}
      <div className="rs-empty__title serif">{title}</div>
      {children ? <div className="rs-empty__body">{children}</div> : null}
    </div>
  );
}

export function Spinner({ size = 14 }: { size?: number }) {
  return <span className="rs-spinner" style={{ width: size, height: size }} />;
}

export function Section({ title, children, aside, collapsible, defaultOpen = true, id }: { title: ReactNode; children: ReactNode; aside?: ReactNode; collapsible?: boolean; defaultOpen?: boolean; id?: string }) {
  const storageKey = id ? `reactor.section.${id}` : null;
  const [open, setOpen] = useState(() => (storageKey && localStorage.getItem(storageKey) !== null ? localStorage.getItem(storageKey) === "1" : defaultOpen));
  return (
    <section className={cx("rs-section", !open && "is-collapsed")}>
      <header
        className={cx("rs-section__head", collapsible && "is-collapsible")}
        onClick={() => {
          if (!collapsible) return;
          setOpen(!open);
          if (storageKey) localStorage.setItem(storageKey, !open ? "1" : "0");
        }}
      >
        {collapsible ? <Glyph name={open ? "chevronDown" : "chevronRight"} size={11} /> : null}
        <span className="label">{title}</span>
        <span className="rs-section__aside" onClick={(e) => e.stopPropagation()}>
          {aside}
        </span>
      </header>
      {open ? <div className="rs-section__body">{children}</div> : null}
    </section>
  );
}
