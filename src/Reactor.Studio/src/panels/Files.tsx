import { useEffect, useMemo, useState } from "react";
import { api } from "../api/client";
import type { WorkspaceFile } from "../api/types";
import { Button, Dialog, Empty, Field, Spinner, TextInput, cx } from "../components/controls";
import { Glyph } from "../components/Glyph";
import { newDocument, openFile, recentFiles, saveDocument } from "../state/files";
import { useStudio } from "../state/store";

export function Files() {
  const [files, setFiles] = useState<WorkspaceFile[] | null>(null);
  const [root, setRoot] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const current = useStudio((s) => s.file.path);
  const connected = useStudio((s) => s.connected);

  const refresh = () => {
    setError(null);
    api
      .workspace()
      .then((r) => {
        setFiles(r.files);
        setRoot(r.root);
      })
      .catch((e) => setError((e as Error).message));
  };
  useEffect(refresh, [connected]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (files ?? []).filter((f) => !q || f.path.toLowerCase().includes(q) || (f.goal ?? "").toLowerCase().includes(q));
  }, [files, query]);

  const folders = useMemo(() => {
    const map = new Map<string, WorkspaceFile[]>();
    for (const f of filtered) {
      const dir = f.path.includes("/") ? f.path.slice(0, f.path.lastIndexOf("/")) : "";
      map.set(dir, [...(map.get(dir) ?? []), f]);
    }
    return [...map.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [filtered]);

  const recent = recentFiles().filter((p) => p !== current).slice(0, 4);

  return (
    <div className="rs-files">
      <div className="rs-panel-actions">
        <Button size="sm" icon="plus" onClick={() => void newDocument()}>
          New
        </Button>
        <SaveAsButton />
        <Button size="sm" variant="ghost" icon="retry" title="Refresh" onClick={refresh} />
      </div>
      <div className="rs-panel-search">
        <Glyph name="search" size={13} />
        <input value={query} placeholder="Filter pipelines" onChange={(e) => setQuery(e.target.value)} onKeyDown={(e) => e.stopPropagation()} />
      </div>
      <div className="rs-panel-scroll">
        {recent.length > 0 && !query ? (
          <>
            <div className="rs-files__dir label">Recent</div>
            {recent.map((path) => (
              <button key={path} type="button" className="rs-file" onClick={() => void openFile(path)}>
                <Glyph name="clock" size={13} />
                <span className="rs-file__name">{path}</span>
              </button>
            ))}
          </>
        ) : null}
        {error ? <Empty title="Can't list the workspace">{error}</Empty> : null}
        {!files && !error ? (
          <div className="rs-files__loading">
            <Spinner />
          </div>
        ) : null}
        {files && files.length === 0 ? <Empty title="No pipelines yet" icon="files">Create one with New — it is saved as a .dot file in the workspace.</Empty> : null}
        {folders.map(([dir, list]) => (
          <div key={dir || "/"}>
            <div className="rs-files__dir label">{dir || "workspace root"}</div>
            {list.map((f) => (
              <button key={f.path} type="button" className={cx("rs-file", f.path === current && "is-current", f.error && "has-error")} onClick={() => void openFile(f.path)} title={f.error ?? f.goal ?? f.path}>
                <Glyph name={f.error ? "problems" : "code"} size={13} />
                <span className="rs-file__body">
                  <span className="rs-file__name">{f.name}</span>
                  <span className="rs-file__meta">{f.error ? "parse error" : f.goal ?? `${f.node_count ?? "?"} stages`}</span>
                </span>
                {f.has_layout ? <span className="rs-file__badge" title="Has a layout sidecar">◧</span> : null}
              </button>
            ))}
          </div>
        ))}
        {root ? <div className="rs-files__root mono" title={root}>{root}</div> : null}
      </div>
    </div>
  );
}

export function SaveAsButton({ label = "Save as" }: { label?: string }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button size="sm" variant="quiet" icon="save" onClick={() => setOpen(true)}>
        {label}
      </Button>
      {open ? <SaveAsDialog onClose={() => setOpen(false)} /> : null}
    </>
  );
}

export function SaveAsDialog({ onClose }: { onClose: () => void }) {
  const doc = useStudio((s) => s.doc);
  const current = useStudio((s) => s.file.path);
  const [path, setPath] = useState(current ?? `pipelines/${doc.name || "pipeline"}.dot`);
  const valid = /\.dot$/i.test(path.trim()) && !path.includes("..");
  const save = async () => {
    if (!valid) return;
    const saved = await saveDocument(path.trim());
    if (saved) onClose();
  };
  return (
    <Dialog
      kicker="Workspace"
      title="Save pipeline"
      onClose={onClose}
      width={480}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" onClick={() => void save()} disabled={!valid}>
            Save
          </Button>
        </>
      }
    >
      <Field label="Path in the workspace" hint="The layout is saved next to it as name.layout.json. The .dot file stays runnable with reactor run.">
        <TextInput value={path} onChange={setPath} mono autoFocus onCommit={(v) => setPath(v)} invalid={!valid} />
      </Field>
    </Dialog>
  );
}
