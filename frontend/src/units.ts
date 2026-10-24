import type { ParameterDefinition, UnitSystem } from './types';

// SI is the storage unit (spec section 4); display units are converted at the edge only.
interface Conversion { unit: string; factor: number }
const IMPERIAL: Record<string, Conversion> = {
  MEL_Operating_Weight: { unit: 'lbs', factor: 2.2046226218 }, // kg -> lb
  MEL_Design_Airflow: { unit: 'CFM', factor: 2.11888 },        // L/s -> CFM
};

const isNumeric = (p: ParameterDefinition) => p.dataType !== 'TEXT';

export function displayUnit(p: ParameterDefinition, system: UnitSystem): string {
  if (system === 'Imperial' && IMPERIAL[p.name]) return IMPERIAL[p.name].unit;
  return p.siUnit ?? '';
}

export function toDisplay(p: ParameterDefinition, si: string | undefined, system: UnitSystem): string {
  if (si === undefined || si === '') return '';
  const c = system === 'Imperial' ? IMPERIAL[p.name] : undefined;
  if (!c || !isNumeric(p)) return si;
  const n = Number(si);
  return Number.isFinite(n) ? String(Math.round(n * c.factor * 10) / 10) : si;
}

export function toSi(p: ParameterDefinition, display: string, system: UnitSystem): string {
  const c = system === 'Imperial' ? IMPERIAL[p.name] : undefined;
  if (!c || !isNumeric(p)) return display;
  const n = Number(display);
  return Number.isFinite(n) ? String(Math.round((n / c.factor) * 100) / 100) : display;
}
