import type { EquipmentItem } from '../types';
import type { EquipmentStatus } from '../App';

interface Props {
  equipment: EquipmentItem[];
  selected: string | null;
  status: Record<string, EquipmentStatus>;
  onSelect: (uniqueId: string) => void;
}

const POSITIONS = [
  { top: 80, left: 40 }, { top: 80, left: 140 }, { top: 150, left: 40 }, { top: 80, left: 240 },
];

// Placeholder for the APS Viewer v7 (docs/PLAN.md, Phase 3). Selection contract is the same:
// the viewer will report the Revit UniqueId (externalId) and the grid/inspector follow it.
export default function ViewerPane({ equipment, selected, status, onSelect }: Props) {
  return (
    <div className="viewer-container">
      <div className="viewport-mock">
        <div className="grid-lines" />
        <div className="mock-building-mass">
          <span style={{ position: 'absolute', top: 10, left: 12, fontSize: 11, color: 'var(--text-muted)', fontWeight: 600 }}>
            LEVEL 01 - MECHANICAL ROOF YARD
          </span>
          {equipment.map((e, i) => {
            const pos = POSITIONS[i % POSITIONS.length];
            const cls = ['mock-equipment', e.uniqueId === selected ? 'active' : '', status[e.uniqueId] ?? ''].join(' ');
            return (
              <div key={e.uniqueId} className={cls} style={pos} onClick={() => onSelect(e.uniqueId)}>{e.tag}</div>
            );
          })}
        </div>
      </div>
      <div className="viewer-hud-top">
        <div className="hud-chip">
          <i className="ph-fill ph-circle" style={{ color: 'var(--accent-amber)', fontSize: 8 }} />
          <span>Viewer placeholder (APS Viewer not connected)</span>
        </div>
        <div className="hud-chip"><i className="ph ph-git-branch" /><span>Model: demo-model</span></div>
      </div>
    </div>
  );
}
