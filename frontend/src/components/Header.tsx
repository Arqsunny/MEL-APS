import type { ApsStatus, CurrentUser, UnitSystem } from '../types';

interface Props {
  user: CurrentUser; onSignOut: () => void;
  units: UnitSystem; onUnits: (u: UnitSystem) => void;
  pendingCount: number; onReview: () => void;
  aps: ApsStatus | null; canReview: boolean; onConnectAps: () => void; onFindModel: () => void; syncing: boolean;
}

export default function Header({ user, onSignOut, units, onUnits, pendingCount, onReview, aps, canReview, onConnectAps, onFindModel, syncing }: Props) {
  return (
    <header>
      <div className="header-left">
        <div className="forma-badge"><i className="ph-bold ph-cube" /> Autodesk Forma</div>
        <div className="breadcrumb">
          <span>Healthcare Tech Campus Hub</span>
          <i className="ph ph-caret-right" />
          <span>Building B - Central Plant</span>
          <i className="ph ph-caret-right" />
          <span className="active">Proposal 04 (Cloud RVT Linked)</span>
        </div>
      </div>
      <div className="header-right">
        <div className="seg" title="Display units (storage is always SI)">
          {(['SI', 'Imperial'] as UnitSystem[]).map(u => (
            <button key={u} className={units === u ? 'on' : ''} onClick={() => onUnits(u)}>{u}</button>
          ))}
        </div>
        {aps && (
          <div className="hud-chip" title={aps.mode === 'Mock' ? 'Development mock: no real Revit model is touched' : aps.account ?? ''}>
            <i className="ph-fill ph-circle" style={{ fontSize: 8, color: aps.mode === 'Mock' ? 'var(--accent-amber)' : aps.connected ? 'var(--accent-green)' : '#ef4444' }} />
            <span>{aps.mode === 'Mock' ? 'APS: mock' : aps.connected ? `APS: ${aps.account ?? 'connected'}` : 'APS: not connected'}</span>
            {aps.mode !== 'Mock' && !aps.connected && canReview && (
              <button className="btn btn-secondary" style={{ padding: '1px 8px' }} onClick={onConnectAps}>Connect</button>
            )}
            {aps.mode !== 'Mock' && aps.connected && canReview && (
              <button className="btn btn-secondary" style={{ padding: '1px 8px' }} onClick={onFindModel}>Link model</button>
            )}
          </div>
        )}
        <div className="hud-chip" title={user.username}>
          <i className="ph ph-user" />
          <span>{user.displayName ?? user.username}</span>
          <span className="hint">· {user.roles.length ? user.roles.join(', ') : 'read only'}</span>
          <button onClick={onSignOut} title="Sign out"
                  style={{ background: 'none', border: 'none', color: 'var(--text-secondary)', cursor: 'pointer' }}>
            <i className="ph ph-sign-out" />
          </button>
        </div>
        <button className="btn btn-primary" onClick={onReview}>
          <i className={`ph-bold ${syncing ? 'ph-arrows-clockwise' : 'ph-paper-plane-tilt'}`} /> Review &amp; Sync to Revit
          <span style={{ background: 'rgba(255,255,255,0.2)', borderRadius: 10, padding: '1px 6px', fontSize: 10 }}>
            {pendingCount} Pending
          </span>
        </button>
      </div>
    </header>
  );
}
