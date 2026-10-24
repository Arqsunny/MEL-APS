import { useState } from 'react';
import { api } from '../api';
import type { EquipmentItem, ParameterDefinition, StagedRecord, UnitSystem } from '../types';
import type { EquipmentStatus } from '../App';
import { displayUnit, toDisplay, toSi } from '../units';
import { StatusBadge } from './EquipmentGrid';

interface Props {
  item: EquipmentItem;
  params: ParameterDefinition[];
  units: UnitSystem;
  staged: StagedRecord[];
  canStage: boolean;
  status: EquipmentStatus;
  onChanged: () => Promise<void>;
  notify: (msg: string, error?: boolean) => void;
}

const SECTIONS = ['Engineering', 'Procurement'];

export default function Inspector({ item, params, units, staged, canStage, status, onChanged, notify }: Props) {
  const editable = params.filter(p => p.stageable && SECTIONS.includes(p.category));
  const initial = () => Object.fromEntries(editable.map(p => [p.guid, toDisplay(p, item.values[p.guid], units)]));
  const [form, setForm] = useState<Record<string, string>>(initial);
  const [busy, setBusy] = useState(false);

  // Dirty = differs from what the user was shown (avoids false changes from unit round-tripping).
  const shown = (p: ParameterDefinition) => toDisplay(p, item.values[p.guid], units);
  const changed = editable.filter(p => (form[p.guid] ?? '') !== shown(p));

  async function stage() {
    setBusy(true);
    try {
      for (const p of changed) {
        await api.stage({
          equipmentUniqueId: item.uniqueId,
          parameterGuid: p.guid,
          proposedValue: toSi(p, form[p.guid], units),
        });
      }
      notify(`${changed.length} change(s) staged; awaiting BIM Lead approval.`);
      await onChanged();
    } catch (err) {
      notify((err as Error).message, true);
    } finally {
      setBusy(false);
    }
  }

  const pending = staged.filter(s => s.status === 'Pending');

  return (
    <div className="inspector-container">
      <div className="inspector-header">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
          <div>
            <h2 style={{ fontSize: 15, fontWeight: 700 }}>{item.tag}</h2>
            <span style={{ fontSize: 11, color: 'var(--text-secondary)' }}>{item.familyType}</span>
          </div>
          <StatusBadge status={status} />
        </div>
        <div className="guid-tag" style={{ marginTop: 6 }}>UniqueId: {item.uniqueId}</div>
        <div className="guid-tag">MEL_Equipment_GUID: {item.equipmentGuid}</div>
      </div>
      <div className="inspector-scroll">
        {pending.length > 0 && (
          <div className="staged-change-alert">
            <div><strong>{pending.length} change(s) staged</strong><br />Pending BIM Lead review.</div>
            <i className="ph-bold ph-pencil-simple-line" />
          </div>
        )}
        {SECTIONS.map(section => (
          <div className="panel-section" key={section}>
            <div className="section-title"><span>{section}</span></div>
            <div className="prop-card">
              {editable.filter(p => p.category === section).map(p => {
                const unit = displayUnit(p, units);
                return (
                  <div className="prop-item" key={p.guid}>
                    <label>
                      <span>{p.name.replace(/^MEL_/, '').replace(/_/g, ' ')}</span>
                      <span className="guid-tag">{`{${p.guid.slice(0, 8)}…}`}</span>
                    </label>
                    <div className="input-group">
                      <input type={p.dataType === 'TEXT' ? 'text' : 'number'} value={form[p.guid] ?? ''}
                             onChange={e => setForm({ ...form, [p.guid]: e.target.value })} />
                      {unit && <span className="unit-indicator">{unit}</span>}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        ))}
        {staged.length > 0 && (
          <div className="panel-section">
            <div className="section-title"><span>History (this item)</span></div>
            {staged.map(s => {
              const p = params.find(x => x.guid === s.parameterGuid);
              const show = (v: string) => (p ? `${toDisplay(p, v, units)} ${displayUnit(p, units)}`.trim() : v);
              return (
                <div className="hint" key={s.id}>
                  {s.parameterName}: {show(s.oldValue)} → {show(s.proposedValue)} · <strong>{s.status}</strong>
                  {s.rejectionReason ? ` (${s.rejectionReason})` : ''}
                </div>
              );
            })}
          </div>
        )}
      </div>
      <div className="inspector-footer">
        <button className="btn btn-secondary" style={{ flex: 1 }} disabled={busy || changed.length === 0}
                onClick={() => setForm(initial())}>Revert</button>
        <button className="btn btn-primary" style={{ flex: 2 }} disabled={busy || !canStage || changed.length === 0}
                title={canStage ? undefined : 'Your account has no Contributor or BIM Lead role'}
                onClick={() => void stage()}>Stage Changes{changed.length ? ` (${changed.length})` : ''}</button>
      </div>
    </div>
  );
}
