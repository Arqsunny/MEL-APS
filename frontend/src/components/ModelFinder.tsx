import { useEffect, useState } from 'react';
import { api } from '../api';
import type { CloudModel, Hub, LinkedModel, ModelScanResult, Project } from '../types';

interface Props {
  onClose: () => void;
  onLinked: () => Promise<void>;
  notify: (msg: string, error?: boolean) => void;
}

// BIM Lead helper: finds the Revit cloud model identifiers Design Automation needs and links them to the current model.
export default function ModelFinder({ onClose, onLinked, notify }: Props) {
  const [linked, setLinked] = useState<LinkedModel | null>(null);
  const [hubs, setHubs] = useState<Hub[]>([]);
  const [hubId, setHubId] = useState('');
  const [projects, setProjects] = useState<Project[]>([]);
  const [projectId, setProjectId] = useState('');
  const [search, setSearch] = useState('');
  const [scan, setScan] = useState<ModelScanResult | null>(null);
  const [region, setRegion] = useState('US');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function guard<T>(work: () => Promise<T>): Promise<T | undefined> {
    setBusy(true);
    setError(null);
    try { return await work(); } catch (e) { setError((e as Error).message); return undefined; } finally { setBusy(false); }
  }

  useEffect(() => {
    void guard(async () => {
      setLinked((await api.models()).find(m => m.modelGuid === 'demo-model') ?? null);
      const h = await api.hubs();
      setHubs(h);
      if (h.length === 1) setHubId(h[0].id);
    });
  }, []);

  useEffect(() => {
    setProjects([]); setProjectId(''); setScan(null);
    if (hubId) void guard(async () => setProjects(await api.projects(hubId)));
  }, [hubId]);

  async function runScan() {
    const r = await guard(() => api.findModels(hubId, projectId, search));
    if (!r) return;
    setScan(r);
    if (r.regionKnown && r.region) setRegion(r.region);
  }

  async function link(m: CloudModel) {
    const r = await guard(() => api.linkModel(region, m.projectGuid, m.modelGuid, m.revitVersion, { hubId, projectId, itemId: m.itemId }));
    if (r) { setLinked(r); notify(`Linked to ${m.name}.`); await onLinked(); }
  }

  const hubRegion = hubs.find(h => h.id === hubId)?.region;

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-content" style={{ width: 760 }} onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <i className="ph-bold ph-link" style={{ color: 'var(--accent-forma)', fontSize: 18 }} />
            <h3 style={{ fontSize: 14, fontWeight: 600 }}>Link to a Revit cloud model</h3>
          </div>
          <button onClick={onClose} style={{ background: 'none', border: 'none', color: 'var(--text-secondary)', cursor: 'pointer' }}>
            <i className="ph ph-x" style={{ fontSize: 18 }} />
          </button>
        </div>
        <div className="modal-body" style={{ maxHeight: 520 }}>
          <p className="hint" style={{ marginBottom: 10 }}>
            {linked?.cloudModelGuid
              ? <>Currently linked: <span className="mono">{linked.region} · Revit {linked.revitVersion ?? 'version unknown (link again)'} · {linked.cloudProjectGuid} · {linked.cloudModelGuid}</span></>
              : 'This model is not linked yet. Syncs to Revit stay blocked until it is.'}
          </p>
          {error && <p style={{ color: '#fca5a5', fontSize: 12, marginBottom: 8 }}>{error}</p>}

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8, marginBottom: 8 }}>
            <label className="prop-item"><span className="hint">Hub / account {hubRegion ? `(${hubRegion})` : ''}</span>
              <select value={hubId} onChange={e => setHubId(e.target.value)} disabled={busy}>
                <option value="">Select…</option>
                {hubs.map(h => <option key={h.id} value={h.id}>{h.name}</option>)}
              </select>
            </label>
            <label className="prop-item"><span className="hint">Project</span>
              <select value={projectId} onChange={e => { setProjectId(e.target.value); setScan(null); }} disabled={busy || !hubId}>
                <option value="">Select…</option>
                {projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </label>
          </div>
          <div style={{ display: 'flex', gap: 8, marginBottom: 10 }}>
            <input type="text" placeholder="File name contains… (optional, narrows the scan)" value={search}
                   onChange={e => setSearch(e.target.value)} onKeyDown={e => { if (e.key === 'Enter' && projectId) void runScan(); }} />
            <button className="btn btn-primary" disabled={busy || !projectId} onClick={() => void runScan()}>
              {busy ? 'Working…' : 'Find models'}
            </button>
          </div>

          {scan && (
            <>
              <p className="hint" style={{ marginBottom: 6 }}>
                Scanned {scan.scan.foldersVisited} folder(s), {scan.scan.revitFilesChecked} Revit file(s);{' '}
                {scan.scan.notCloudWorkshared} were plain uploaded RVTs (not cloud models, so not listed).
              </p>
              {scan.scan.notes.map((n, i) => <p key={i} style={{ color: '#fbbf24', fontSize: 11, marginBottom: 4 }}>{n}</p>)}
              {!scan.regionKnown && (
                <label className="prop-item" style={{ marginBottom: 8 }}>
                  <span className="hint">APS did not report this hub's region. Choose where the project is hosted:</span>
                  <select value={region} onChange={e => setRegion(e.target.value)}><option>US</option><option>EMEA</option></select>
                </label>
              )}
              {scan.models.length === 0 && <p className="hint">No Revit cloud models found{search ? ` matching "${search}"` : ''}.</p>}
              {scan.models.map(m => (
                <div className="review-row" key={m.itemId}>
                  <div style={{ minWidth: 0 }}>
                    <div><strong>{m.name}</strong> <span className="hint">· {m.folderPath}</span> <span className={`badge-status ${m.supported ? 'badge-approved' : 'badge-pending'}`}>Revit {m.revitVersion ?? '?'}</span></div>
                    {!m.supported && (
                      <div style={{ color: '#fbbf24', fontSize: 11 }}>
                        {m.revitVersion == null
                          ? 'APS did not report this model\'s Revit version, so the right engine cannot be chosen.'
                          : `Saved in Revit ${m.revitVersion}; engines exist only for Revit ${scan.supportedRevitYears.join(' and ')}. A different release cannot open it safely.`}
                      </div>
                    )}
                    <div className="mono hint">{region} · project {m.projectGuid}</div>
                    <div className="mono hint">model {m.modelGuid}</div>
                    {m.isComposite && <div className="hint" style={{ color: '#fbbf24' }}>Composite design (host + linked models)</div>}
                  </div>
                  <button className="btn btn-success" disabled={busy || !m.supported} title={m.supported ? undefined : 'No engine for this Revit release'} onClick={() => void link(m)}>Link</button>
                </div>
              ))}
            </>
          )}
        </div>
        <div className="modal-footer"><button className="btn btn-secondary" onClick={onClose}>Close</button></div>
      </div>
    </div>
  );
}
