import { useState } from 'react';
import { api } from '../api';
import type { CurrentUser, EquipmentItem, ParameterDefinition, StagedRecord, SyncJobSummary, UnitSystem } from '../types';
import { displayUnit, toDisplay } from '../units';

interface Props {
  staged: StagedRecord[];
  jobs: SyncJobSummary[];
  equipment: EquipmentItem[];
  params: ParameterDefinition[];
  units: UnitSystem;
  user: CurrentUser;
  canReview: boolean;
  onClose: () => void;
  onChanged: () => Promise<void>;
  notify: (msg: string, error?: boolean) => void;
}

export default function ReviewModal({ staged, jobs, equipment, params, units, user, canReview, onClose, onChanged, notify }: Props) {
  const [busy, setBusy] = useState(false);
  const isLead = canReview;
  const pending = staged.filter(s => s.status === 'Pending');
  const approved = staged.filter(s => s.status === 'Approved');
  const inFlight = staged.filter(s => s.status === 'Dispatched');

  const tagOf = (uid: string) => equipment.find(e => e.uniqueId === uid)?.tag ?? uid;
  const fmt = (s: StagedRecord, v: string) => {
    const p = params.find(x => x.guid === s.parameterGuid);
    return p ? `${toDisplay(p, v, units)} ${displayUnit(p, units)}`.trim() : v;
  };

  async function run(action: () => Promise<unknown>, okMsg: string) {
    setBusy(true);
    try {
      await action();
      notify(okMsg);
      await onChanged();
    } catch (err) {
      notify((err as Error).message, true);
    } finally {
      setBusy(false);
    }
  }

  function reject(s: StagedRecord) {
    const reason = window.prompt('Reason for rejection (required):');
    if (reason?.trim()) void run(() => api.reject(s.id, reason.trim()), 'Change rejected.');
  }

  function dispatch() {
    const ids = [...new Set(approved.map(s => s.equipmentUniqueId))];
    // Returns immediately (202): the sync runs in the background and the grid shows "Syncing…" until it finishes.
    void run(async () => { await api.dispatch(ids); }, 'Sync queued. You can keep working; the result will appear here.');
  }

  const row = (s: StagedRecord, actions: React.ReactNode) => (
    <div className="review-row" key={s.id}>
      <div>
        <div className="mono" style={{ color: 'var(--accent-cyan)' }}>{tagOf(s.equipmentUniqueId)} · {s.parameterName}</div>
        <div>{fmt(s, s.oldValue)} → <strong>{fmt(s, s.proposedValue)}</strong>
          <span className="hint"> · by {s.requestedBy}</span></div>
        {s.lastError && s.status === 'Approved' && (
          <div style={{ color: '#fca5a5', fontSize: 11, marginTop: 2 }}>Last sync attempt failed: {s.lastError}</div>
        )}
      </div>
      <div style={{ display: 'flex', gap: 6 }}>{actions}</div>
    </div>
  );

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-content" onClick={e => e.stopPropagation()}>
        <div className="modal-header">
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <i className="ph-bold ph-git-merge" style={{ color: 'var(--accent-forma)', fontSize: 18 }} />
            <h3 style={{ fontSize: 14, fontWeight: 600 }}>BIM Lead Revit Sync Authorization</h3>
          </div>
          <button onClick={onClose} style={{ background: 'none', border: 'none', color: 'var(--text-secondary)', cursor: 'pointer' }}>
            <i className="ph ph-x" style={{ fontSize: 18 }} />
          </button>
        </div>
        <div className="modal-body">
          {!isLead && <p className="hint" style={{ marginBottom: 10 }}>Only BIM Leads can approve, reject or dispatch.</p>}

          <div className="section-title" style={{ marginBottom: 8 }}><span>Pending review ({pending.length})</span></div>
          {pending.length === 0 && <p className="hint" style={{ marginBottom: 12 }}>Nothing pending.</p>}
          {pending.map(s => row(s, isLead && (s.requestedBy.toLowerCase() === user.username.toLowerCase()
            ? <span className="hint">Your request: another BIM Lead must review it</span>
            : <>
              <button className="btn btn-success" disabled={busy}
                      onClick={() => void run(() => api.approve(s.id), 'Change approved.')}>Approve</button>
              <button className="btn btn-secondary" disabled={busy} onClick={() => reject(s)}>Reject</button>
            </>
          )))}

          <div className="section-title" style={{ margin: '14px 0 8px' }}><span>Approved, ready to dispatch ({approved.length})</span></div>
          {approved.length === 0 && <p className="hint">Nothing approved yet.</p>}
          {approved.map(s => row(s, null))}

          {inFlight.length > 0 && (
            <>
              <div className="section-title" style={{ margin: '14px 0 8px' }}><span>Syncing to Revit ({inFlight.length})</span></div>
              {inFlight.map(s => row(s, <span className="hint"><i className="ph ph-arrows-clockwise" /> in progress</span>))}
            </>
          )}

          {(jobs.length > 0 || isLead) && (
            <>
              <div className="section-title" style={{ margin: '14px 0 8px' }}>
                <span>Recent syncs</span>
                {isLead && (
                  <button className="btn btn-secondary" style={{ padding: '1px 8px' }} title="Opens the linked model in Design Automation without changing or saving anything"
                          disabled={busy || inFlight.length > 0 || jobs.some(j => j.status === 'Queued' || j.status === 'Submitted')}
                          onClick={() => void run(async () => { await api.checkModel(); }, 'Connectivity check queued. It opens the linked model without changing anything.')}>
                    Test connection
                  </button>
                )}
              </div>
              {jobs.length === 0 && <p className="hint">No syncs yet.</p>}
              {jobs.map(j => (
                <div className="review-row" key={j.id}>
                  <div>
                    <div className="mono">{new Date(j.createdAt).toLocaleString()} · {j.items === 0 ? 'Connectivity check' : `${j.items} change(s)`} · {j.runner}</div>
                    {j.error && <div style={{ color: '#fca5a5', fontSize: 11 }}>{j.error}</div>}
                  </div>
                  <span className={`badge-status ${j.status === 'Succeeded' ? 'badge-approved' : j.status === 'Failed' ? 'badge-rejected' : 'badge-pending'}`}>{j.status}</span>
                </div>
              ))}
            </>
          )}
        </div>
        <div className="modal-footer">
          <button className="btn btn-secondary" onClick={onClose}>Close</button>
          <button className="btn btn-success" disabled={busy || !isLead || approved.length === 0} onClick={dispatch}>
            <i className="ph-bold ph-play" /> Authorize &amp; Sync to Revit
          </button>
        </div>
      </div>
    </div>
  );
}
