import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api';
import type { ParameterDefinition, ViewerPick, ViewerSession } from '../types';
import ProjectBrowser from './ProjectBrowser';

interface Props {
  params: ParameterDefinition[];
  selected: string | null;
  live: boolean;
  canBrowse: boolean;
  onSelect: (uniqueId: string) => void;
  onPick: (pick: ViewerPick) => Promise<void>;
  onConnect: () => void;
}

interface ViewTarget { urn: string; name: string }

/** Version ids from Data Management are ASCII. The viewer document id is "urn:" plus url-safe base64. */
export function toViewerUrn(versionId: string): string {
  const b64 = btoa(versionId).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `urn:${b64}`;
}

const VIEWER_JS = 'https://developer.api.autodesk.com/modelderivative/v2/viewers/7.*/viewer3D.min.js';
const VIEWER_CSS = 'https://developer.api.autodesk.com/modelderivative/v2/viewers/7.*/style.min.css';
const NESTED_JS = '/extensions/NestedViewerExtension/contents/main.js';
const NESTED_CSS = '/extensions/NestedViewerExtension/contents/main.css';

interface PropResult {
  externalId?: string;
  name?: string;
  properties?: { displayName?: string; displayValue?: unknown }[];
}

interface ApsViewer {
  start: () => number;
  finish: () => void;
  addEventListener: (event: string, cb: () => void) => void;
  getSelection: () => number[];
  select: (ids: number[]) => void;
  fitToView: (ids?: number[]) => void;
  getProperties: (dbId: number, cb: (result: PropResult) => void, err?: () => void) => void;
  loadDocumentNode: (doc: unknown, node: unknown, options?: { keepCurrentModels?: boolean }) => Promise<unknown>;
  loadExtension: (id: string, options?: object) => Promise<unknown>;
  getExtension?: (id: string) => unknown;
  resize?: () => void;
  model?: { getExternalIdMapping: (cb: (map: Record<string, number>) => void) => void };
}

let viewingReady: Promise<void> | null = null;
function ensureViewing(env: string, getToken: (done: (token: string, expires: number) => void) => void): Promise<void> {
  if (viewingReady) return viewingReady;
  viewingReady = loadAssets().then(() => new Promise<void>((resolve, reject) => {
    window.Autodesk.Viewing.Initializer(
      { env, api: 'streamingV2', getAccessToken: getToken },
      () => resolve(),
      () => { viewingReady = null; reject(new Error('APS Viewer failed to start.')); },
    );
  })).catch(err => { viewingReady = null; throw err; });
  return viewingReady;
}

declare global {
  interface Window { Autodesk?: any }
}

let nestedPromise: Promise<void> | null = null;
function loadNestedViewer(): Promise<void> {
  if (nestedPromise) return nestedPromise;
  nestedPromise = new Promise((resolve, reject) => {
    if (!document.querySelector(`link[href="${NESTED_CSS}"]`)) {
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = NESTED_CSS;
      document.head.appendChild(link);
    }
    const script = document.createElement('script');
    script.src = NESTED_JS;
    script.onload = () => resolve();
    script.onerror = () => { nestedPromise = null; reject(new Error('Viewer in Viewer could not be loaded.')); };
    document.body.appendChild(script);
  });
  return nestedPromise;
}

let assetsPromise: Promise<void> | null = null;
function loadAssets(): Promise<void> {
  if (window.Autodesk?.Viewing) return Promise.resolve();
  if (assetsPromise) return assetsPromise;
  assetsPromise = new Promise((resolve, reject) => {
    if (!document.querySelector(`link[href="${VIEWER_CSS}"]`)) {
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = VIEWER_CSS;
      document.head.appendChild(link);
    }
    const script = document.createElement('script');
    script.src = VIEWER_JS;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error('Could not load the APS Viewer.'));
    document.head.appendChild(script);
  });
  return assetsPromise;
}

function pickFromProperties(result: PropResult, params: ParameterDefinition[]): ViewerPick | null {
  const uniqueId = result.externalId?.trim();
  if (!uniqueId) return null;
  const byName = new Map(params.map(p => [p.name.toLowerCase(), p]));
  const props = result.properties ?? [];
  const text = (name: string) => {
    const found = props.find(p => (p.displayName ?? '').toLowerCase() === name.toLowerCase());
    return found?.displayValue == null ? '' : String(found.displayValue);
  };
  const values: Record<string, string> = {};
  let equipmentGuid: string | null = null;
  for (const prop of props) {
    const def = byName.get((prop.displayName ?? '').toLowerCase());
    if (!def || prop.displayValue == null || prop.displayValue === '') continue;
    const raw = String(prop.displayValue);
    if (def.name === 'MEL_Equipment_GUID') equipmentGuid = raw;
    else if (def.dataType === 'TEXT' || def.dataType === 'INTEGER') values[def.guid] = raw;
  }
  const family = text('Family') || text('Family Name');
  const typeName = text('Type Name') || text('Type');
  const familyType = [family, typeName].filter(Boolean).join(' : ') || text('Category');
  return {
    uniqueId,
    tag: text('MEL_Tag_Number') || result.name || 'Selected',
    familyType,
    equipmentGuid,
    values,
  };
}

// Real APS Viewer when an account is connected; otherwise the layout placeholder.
async function ensureExtension(viewer: ApsViewer, id: string, options?: object) {
  if (viewer.getExtension?.(id)) return;
  await viewer.loadExtension(id, options);
}

// Toolbar extensions. Document Browser is also named at construction, matching the hubs-browser sample.
// Visual Clusters and Viewer in Viewer are loaded once the model geometry exists, as before.
async function loadModelExtensions(viewer: ApsViewer) {
  const missing: string[] = [];
  try { await ensureExtension(viewer, 'Autodesk.VisualClusters'); }
  catch { missing.push('Visual Clusters'); }
  try { await ensureExtension(viewer, 'Autodesk.DocumentBrowser'); }
  catch { missing.push('Document Browser'); }
  try {
    await loadNestedViewer();
    await ensureExtension(viewer, 'NestedViewerExtension', { filter: ['2d', '3d'], crossSelection: true });
  } catch { missing.push('Viewer in Viewer'); }
  return missing;
}

function firstViewable(root: any) {
  return root.getDefaultGeometry?.()
    ?? root.search?.({ type: 'geometry', role: '3d' })?.[0]
    ?? root.search?.({ type: 'geometry', role: '2d' })?.[0]
    ?? null;
}

export default function ViewerPane({ params, selected, live, canBrowse, onSelect, onPick, onConnect }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const viewerRef = useRef<ApsViewer | null>(null);
  const mapRef = useRef<Record<string, number>>({});
  const fromViewer = useRef(false);
  const picked = useRef(false);
  const paramsRef = useRef(params);
  paramsRef.current = params;
  const [message, setMessage] = useState<string | null>(live ? 'Opening the linked model…' : null);
  const [needsReconnect, setNeedsReconnect] = useState(false);
  const [fileName, setFileName] = useState<string | null>(null);
  const [target, setTarget] = useState<ViewTarget | null>(null);
  const [viewerEpoch, setViewerEpoch] = useState(0);

  const relayout = useCallback(() => {
    requestAnimationFrame(() => viewerRef.current?.resize?.());
  }, []);

  function openVersion(versionId: string, name: string) {
    picked.current = true;
    const label = name || 'Selected file';
    setFileName(label);
    setTarget({ urn: toViewerUrn(versionId), name: label });
  }

  useEffect(() => {
    if (!live || !host.current) return;
    let cancelled = false;
    let viewer: ApsViewer | null = null;
    const hostEl = host.current;

    function tokenProvider(done: (token: string, expires: number) => void) {
      api.viewerSession().then(s => done(s.accessToken, s.expiresIn)).catch(() => done('', 60));
    }

    (async () => {
      let session: ViewerSession;
      try { session = await api.viewerSession(); }
      catch (err) {
        if (cancelled) return;
        const msg = (err as Error).message;
        setMessage(msg);
        setNeedsReconnect(/reconnect/i.test(msg));
        return;
      }
      if (cancelled) return;
      setFileName(session.name);
      setTarget(current => picked.current && current ? current : { urn: session.documentUrn, name: session.name });
      setMessage('Loading the viewable…');
      try { await loadAssets(); }
      catch (err) {
        if (!cancelled) setMessage((err as Error).message);
        return;
      }
      if (cancelled || !window.Autodesk) return;

      const env = session.region === 'EMEA' ? 'AutodeskProduction2' : 'AutodeskProduction';
      await ensureViewing(env, tokenProvider);
      if (cancelled) return;

      viewer = new window.Autodesk.Viewing.GuiViewer3D(hostEl, { extensions: ['Autodesk.DocumentBrowser'] }) as ApsViewer;
      if (viewer.start() > 0) { setMessage('APS Viewer failed to start.'); return; }
      viewerRef.current = viewer;
      setViewerEpoch(n => n + 1);

      viewer.addEventListener(window.Autodesk.Viewing.SELECTION_CHANGED_EVENT, () => {
        if (!viewer) return;
        const ids = viewer.getSelection();
        if (ids.length !== 1) return;
        viewer.getProperties(ids[0], result => {
          const pick = pickFromProperties(result, paramsRef.current);
          if (!pick) return;
          fromViewer.current = true;
          onSelect(pick.uniqueId);
          void onPick(pick).catch(err => setMessage((err as Error).message));
        });
      });
    })().catch(err => { if (!cancelled) setMessage((err as Error).message); });

    return () => {
      cancelled = true;
      viewer?.finish();
      if (viewerRef.current === viewer) viewerRef.current = null;
    };
    // The viewer is created once per connection. The model it shows follows `target`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [live]);

  useEffect(() => {
    const viewer = viewerRef.current;
    if (!viewer || !target || !window.Autodesk) return;
    let cancelled = false;
    mapRef.current = {};
    setFileName(target.name);
    setMessage('Loading the viewable…');
    window.Autodesk.Viewing.Document.load(target.urn, (doc: any) => {
      if (cancelled) return;
      const viewable = firstViewable(doc.getRoot());
      if (!viewable) { setMessage('This file has no published viewable. Publish it from Revit and try again.'); return; }
      viewer.loadDocumentNode(doc, viewable, { keepCurrentModels: false }).then(async () => {
        if (cancelled || !viewer.model) return;
        setMessage(null);
        viewer.resize?.();
        viewer.model.getExternalIdMapping((map: Record<string, number>) => { mapRef.current = map; });
        const missing = await loadModelExtensions(viewer);
        if (!cancelled && missing.length) setMessage(`${missing.join(', ')} could not be loaded.`);
      }).catch(() => { if (!cancelled) setMessage('The view failed to load.'); });
    }, () => { if (!cancelled) setMessage('This file has no published viewable. Publish it from Revit and try again.'); });
    return () => { cancelled = true; };
  }, [target, viewerEpoch]);

  useEffect(() => {
    const viewer = viewerRef.current;
    if (!viewer || !selected) return;
    if (fromViewer.current) { fromViewer.current = false; return; }
    const dbId = mapRef.current[selected];
    if (dbId == null) return;
    viewer.select([dbId]);
    viewer.fitToView([dbId]);
  }, [selected]);

  if (!live) {
    return (
      <div className="viewer-container">
        <div className="viewer-stage">
        <div className="viewport-mock">
          <div className="grid-lines" />
          <div className="mock-building-mass" />
        </div>
        <div className="viewer-hud-top">
          <div className="hud-chip">
            <i className="ph-fill ph-circle" style={{ color: 'var(--accent-amber)', fontSize: 8 }} />
            <span>Connect the APS account to view the linked model</span>
          </div>
        </div>
        </div>
      </div>
    );
  }

  return (
    <div className="viewer-container">
      {canBrowse && <ProjectBrowser onOpen={openVersion} onLayout={relayout} />}
      <div className="viewer-stage">
      <div ref={host} className="aps-viewer" />
      <div className="viewer-hud-top">
        <div className="hud-chip">
          <i className="ph-fill ph-cube" style={{ color: 'var(--accent-cyan)', fontSize: 12 }} />
          <span>{fileName ?? 'Linked model'}</span>
        </div>
        <div className="hud-chip"><span>Click a piece of equipment to use its real Revit id</span></div>
        {message && <div className="hud-chip"><span>{message}</span></div>}
        {needsReconnect && <button className="btn btn-secondary" onClick={onConnect}>Reconnect</button>}
      </div>
      </div>
    </div>
  );
}
