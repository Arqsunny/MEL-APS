import { useMemo, useState } from 'react';
import type { EquipmentItem, ParameterDefinition, UnitSystem } from '../types';
import type { EquipmentStatus } from '../App';
import { displayUnit, toDisplay } from '../units';

interface Props {
  equipment: EquipmentItem[];
  params: ParameterDefinition[];
  units: UnitSystem;
  selected: string | null;
  status: Record<string, EquipmentStatus>;
  onSelect: (uniqueId: string) => void;
}

export const BADGES: Record<EquipmentStatus, { cls: string; icon: string; label: string }> = {
  none: { cls: 'badge-none', icon: 'ph-minus-circle', label: 'No changes' },
  pending: { cls: 'badge-pending', icon: 'ph-clock', label: 'Staged Changes' },
  approved: { cls: 'badge-approved', icon: 'ph-thumbs-up', label: 'Lead Approved' },
  rejected: { cls: 'badge-rejected', icon: 'ph-x-circle', label: 'Rejected' },
  syncing: { cls: 'badge-committed', icon: 'ph-arrows-clockwise', label: 'Syncing…' },
  committed: { cls: 'badge-committed', icon: 'ph-check-circle', label: 'Synced to RVT' },
};

export function StatusBadge({ status }: { status: EquipmentStatus }) {
  const b = BADGES[status];
  return <span className={`badge-status ${b.cls}`}><i className={`ph-fill ${b.icon}`} /> {b.label}</span>;
}

export default function EquipmentGrid({ equipment, params, units, selected, status, onSelect }: Props) {
  const [filter, setFilter] = useState('');
  const byName = useMemo(() => Object.fromEntries(params.map(p => [p.name, p])), [params]);

  const val = (e: EquipmentItem, name: string) => {
    const p = byName[name];
    return p ? `${toDisplay(p, e.values[p.guid], units)} ${displayUnit(p, units)}`.trim() : '';
  };

  const rows = equipment.filter(e =>
    `${e.tag} ${e.familyType} ${e.uniqueId}`.toLowerCase().includes(filter.toLowerCase()));

  return (
    <div className="grid-container">
      <div className="grid-header">
        <div className="discipline-tabs">
          <button className="tab-btn active">All Disciplines</button>
        </div>
        <input type="text" placeholder="Filter equipment..." value={filter}
               onChange={e => setFilter(e.target.value)}
               style={{ padding: '4px 8px', fontSize: 11, width: 180 }} />
      </div>
      <div className="table-wrapper">
        <table>
          <thead>
            <tr>
              <th>Mark / Tag</th><th>Family &amp; Type</th><th>Revit UniqueId</th><th>Lead Time</th>
              <th>Operating Weight</th><th>Procurement Status</th><th>Staging Status</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(e => (
              <tr key={e.uniqueId} className={e.uniqueId === selected ? 'selected' : ''} onClick={() => onSelect(e.uniqueId)}>
                <td><strong>{e.tag}</strong></td>
                <td>{e.familyType}</td>
                <td className="guid-tag">{e.uniqueId.slice(0, 22)}...</td>
                <td>{val(e, 'MEL_Lead_Time_Weeks')}</td>
                <td>{val(e, 'MEL_Operating_Weight')}</td>
                <td>{val(e, 'MEL_Procurement_Status')}</td>
                <td><StatusBadge status={status[e.uniqueId] ?? 'none'} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
