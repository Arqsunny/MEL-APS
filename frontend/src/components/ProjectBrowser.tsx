import { useEffect, useState } from 'react';
import { api } from '../api';

interface Props {
  onOpen: (versionId: string, name: string) => void;
  onLayout: () => void;
}

type Kind = 'hub' | 'project' | 'folder' | 'item' | 'version';

interface BrowserNode {
  key: string;
  label: string;
  kind: Kind;
  hubId: string;
  projectId?: string;
  folderId?: string;
  itemId?: string;
  versionId?: string;
  open: boolean;
  loading: boolean;
  loaded: boolean;
  children: BrowserNode[];
  error?: string;
}

const ICONS: Record<Kind, string> = {
  hub: 'ph-buildings',
  project: 'ph-folder-notch',
  folder: 'ph-folder',
  item: 'ph-file',
  version: 'ph-clock',
};

// Same tree as the APS hubs browser: hub, project, folder, file, then version.
// Each level is loaded when it is opened, so a large project is not scanned up front.
export default function ProjectBrowser({ onOpen, onLayout }: Props) {
  const [collapsed, setCollapsed] = useState(false);
  const [nodes, setNodes] = useState<BrowserNode[]>([]);
  const [rootError, setRootError] = useState<string | null>(null);
  const [active, setActive] = useState<string | null>(null);

  useEffect(() => { onLayout(); }, [collapsed, onLayout]);

  useEffect(() => {
    let cancelled = false;
    api.hubs().then(hubs => {
      if (cancelled) return;
      setNodes(hubs.map(h => ({
        key: `hub|${h.id}`, label: h.name, kind: 'hub', hubId: h.id,
        open: false, loading: false, loaded: false, children: [],
      })));
    }).catch(err => { if (!cancelled) setRootError((err as Error).message); });
    return () => { cancelled = true; };
  }, []);

  function patch(key: string, change: (node: BrowserNode) => BrowserNode) {
    const walk = (list: BrowserNode[]): BrowserNode[] => list.map(n => {
      if (n.key === key) return change(n);
      if (n.children.length === 0) return n;
      return { ...n, children: walk(n.children) };
    });
    setNodes(list => walk(list));
  }

  async function load(node: BrowserNode) {
    patch(node.key, n => ({ ...n, loading: true, error: undefined, open: true }));
    try {
      const children = await childrenOf(node);
      patch(node.key, n => ({ ...n, loading: false, loaded: true, open: true, children }));
    } catch (err) {
      patch(node.key, n => ({ ...n, loading: false, open: true, error: (err as Error).message }));
    }
  }

  async function showVersions(node: BrowserNode) {
    if (node.open) { patch(node.key, n => ({ ...n, open: false })); return; }
    if (node.loaded) { patch(node.key, n => ({ ...n, open: true })); return; }
    await load(node);
  }

  async function toggle(node: BrowserNode) {
    if (node.kind === 'version' && node.versionId) {
      setActive(node.key);
      onOpen(node.versionId, node.label);
      return;
    }
    if (node.kind === 'item' && node.hubId && node.projectId && node.itemId) {
      try {
        const tip = await api.itemTip(node.hubId, node.projectId, node.itemId);
        setActive(node.key);
        onOpen(tip.id, tip.name || node.label);
      } catch (err) {
        patch(node.key, n => ({ ...n, error: (err as Error).message }));
      }
      return;
    }
    if (node.open) { patch(node.key, n => ({ ...n, open: false })); return; }
    if (node.loaded) { patch(node.key, n => ({ ...n, open: true })); return; }
    await load(node);
  }

  return (
    <aside className={`project-browser${collapsed ? ' collapsed' : ''}`}>
      <div className="browser-head">
        <span className="browser-title">Projects</span>
        <button className="toolbar-btn" title={collapsed ? 'Show projects' : 'Hide projects'}
                onClick={() => setCollapsed(v => !v)}>
          <i className={`ph-bold ${collapsed ? 'ph-list' : 'ph-caret-left'}`} />
        </button>
      </div>
      <div className="browser-tree">
        {rootError && <div className="browser-error">{rootError}</div>}
        {nodes.map(n => <Branch key={n.key} node={n} active={active} onToggle={toggle} onVersions={showVersions} />)}
      </div>
    </aside>
  );
}

function Branch({ node, active, onToggle, onVersions }: {
  node: BrowserNode;
  active: string | null;
  onToggle: (node: BrowserNode) => void;
  onVersions: (node: BrowserNode) => void;
}) {
  const isActive = node.key === active;
  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'center' }}>
        {node.kind === 'item' && (
          <button className="toolbar-btn" style={{ width: 18, height: 18 }} title="Versions"
                  onClick={() => void onVersions(node)}>
            <i className={`ph-bold ${node.open ? 'ph-caret-down' : 'ph-caret-right'}`} style={{ fontSize: 10 }} />
          </button>
        )}
        <button className={`browser-row${isActive ? ' active' : ''}`} title={node.label} onClick={() => void onToggle(node)}>
          <i className={`ph ${ICONS[node.kind]}`} style={{ fontSize: 14, color: 'var(--text-secondary)' }} />
          <span>{node.loading ? `${node.label} …` : node.label}</span>
        </button>
      </div>
      {node.error && <div className="browser-error">{node.error}</div>}
      {node.open && node.children.length > 0 && (
        <div className="browser-children">
          {node.children.map(child => <Branch key={child.key} node={child} active={active} onToggle={onToggle} onVersions={onVersions} />)}
        </div>
      )}
    </div>
  );
}

async function childrenOf(node: BrowserNode): Promise<BrowserNode[]> {
  if (node.kind === 'hub') {
    const projects = await api.projects(node.hubId);
    return projects.map(p => ({
      key: `project|${node.hubId}|${p.id}`, label: p.name, kind: 'project' as const,
      hubId: node.hubId, projectId: p.id, open: false, loading: false, loaded: false, children: [],
    }));
  }
  if (node.kind === 'project' && node.projectId) {
    return entries(node.hubId, node.projectId, await api.contents(node.hubId, node.projectId));
  }
  if (node.kind === 'folder' && node.projectId && node.folderId) {
    return entries(node.hubId, node.projectId, await api.contents(node.hubId, node.projectId, node.folderId));
  }
  if (node.kind === 'item' && node.projectId && node.itemId) {
    const versions = await api.versions(node.hubId, node.projectId, node.itemId);
    return versions.map(v => ({
      key: `version|${v.id}`, label: v.name, kind: 'version' as const,
      hubId: node.hubId, projectId: node.projectId, versionId: v.id,
      open: false, loading: false, loaded: true, children: [],
    }));
  }
  return [];
}

function entries(hubId: string, projectId: string, rows: { id: string; name: string; folder: boolean }[]): BrowserNode[] {
  return rows.map(row => ({
    key: `${row.folder ? 'folder' : 'item'}|${hubId}|${projectId}|${row.id}`,
    label: row.name,
    kind: row.folder ? 'folder' : 'item',
    hubId,
    projectId,
    folderId: row.folder ? row.id : undefined,
    itemId: row.folder ? undefined : row.id,
    open: false, loading: false, loaded: false, children: [],
  }));
}
