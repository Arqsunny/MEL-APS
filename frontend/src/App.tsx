import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, getToken, setToken, setUnauthorizedHandler } from './api';
import type { ApsStatus, CurrentUser, EquipmentItem, ParameterDefinition, StagedRecord, SyncJobSummary, UnitSystem, ViewerPick } from './types';
import Header from './components/Header';
import Login from './components/Login';
import ViewerPane from './components/ViewerPane';
import EquipmentGrid from './components/EquipmentGrid';
import Inspector from './components/Inspector';
import ReviewModal from './components/ReviewModal';
import ModelFinder from './components/ModelFinder';

export type EquipmentStatus = 'none' | 'pending' | 'approved' | 'rejected' | 'syncing' | 'committed';

export default function App() {
  const [params, setParams] = useState<ParameterDefinition[]>([]);
  const [equipment, setEquipment] = useState<EquipmentItem[]>([]);
  const [staged, setStaged] = useState<StagedRecord[]>([]);
  const [jobs, setJobs] = useState<SyncJobSummary[]>([]);
  const [aps, setAps] = useState<ApsStatus | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [authChecked, setAuthChecked] = useState(false);
  const [units, setUnits] = useState<UnitSystem>('Imperial');
  const [reviewOpen, setReviewOpen] = useState(false);
  const [finderOpen, setFinderOpen] = useState(false);
  const [toast, setToast] = useState<{ msg: string; error?: boolean } | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const notify = useCallback((msg: string, error = false) => {
    setToast({ msg, error });
    window.setTimeout(() => setToast(null), 3500);
  }, []);

  const signOut = useCallback(() => {
    setToken(null);
    setUser(null);
    setEquipment([]);
    setStaged([]);
    setSelected(null);
    setReviewOpen(false);
  }, []);

  const loadUser = useCallback(async () => {
    if (!getToken()) { setUser(null); setAuthChecked(true); return; }
    try { setUser(await api.me()); } catch { setToken(null); setUser(null); }
    setAuthChecked(true);
  }, []);

  useEffect(() => { setUnauthorizedHandler(signOut); void loadUser(); }, [signOut, loadUser]);

  const refresh = useCallback(async () => {
    try {
      const [p, e, s, j, a] = await Promise.all([api.parameters(), api.equipment(), api.staged(), api.syncJobs(), api.apsStatus()]);
      setParams(p);
      setEquipment(e);
      setStaged(s);
      setJobs(j);
      setAps(a);
      setSelected(prev => prev ?? e[0]?.uniqueId ?? null);
      setLoadError(null);
    } catch (err) {
      setLoadError((err as Error).message);
    }
  }, []);

  useEffect(() => { if (user) void refresh(); }, [user, refresh]);

  // The sync runs in the background (Design Automation can take minutes): poll while anything is in flight.
  const activeJob = jobs.some(j => j.status === 'Queued' || j.status === 'Submitted');   // includes connectivity checks (no changes)
  const syncing = staged.some(s => s.status === 'Dispatched') || activeJob;
  const wasSyncing = useRef(false);
  useEffect(() => {
    if (!user || !syncing) return;
    const t = window.setInterval(() => void refresh(), 2500);
    return () => window.clearInterval(t);
  }, [user, syncing, refresh]);

  useEffect(() => {
    if (wasSyncing.current && !syncing) {
      const j = jobs[0];
      if (j && j.items === 0) {
        if (j.status === 'Succeeded') notify('Connectivity check passed: the model opened and closed cleanly; nothing was changed.');
        else notify(`Connectivity check failed: ${j.error ?? j.status}`, true);
      } else if (j?.status === 'Succeeded') notify('Sync finished: all changes are in the model.');
      else if (j) notify(`Sync ${j.status === 'PartiallySucceeded' ? 'finished with problems' : 'failed'}: open Review & Sync for the reasons.`, true);
    }
    wasSyncing.current = syncing;
  }, [syncing, jobs, notify]);

  // Back from the APS sign-in (?aps=connected | ?aps=error).
  useEffect(() => {
    if (!user) return;
    const q = new URLSearchParams(window.location.search).get('aps');
    if (!q) return;
    window.history.replaceState({}, '', window.location.pathname);
    if (q === 'connected') { notify('APS account connected.'); void refresh(); } else notify('APS connection failed. Try again.', true);
  }, [user, notify, refresh]);

  async function connectAps() {
    try { window.location.href = (await api.apsConnect()).url; } catch (err) { notify((err as Error).message, true); }
  }

  async function onViewerPick(pick: ViewerPick) {
    const item = await api.ensureEquipment(pick);
    setSelected(item.uniqueId);
    await refresh();
    setSelected(item.uniqueId);
  }

  const statusByEquipment = useMemo(() => {
    const m: Record<string, EquipmentStatus> = {};
    for (const e of equipment) {
      const mine = staged.filter(s => s.equipmentUniqueId === e.uniqueId);
      m[e.uniqueId] = mine.some(s => s.status === 'Dispatched') ? 'syncing'
        : mine.some(s => s.status === 'Pending') ? 'pending'
        : mine.some(s => s.status === 'Approved') ? 'approved'
        : mine.some(s => s.status === 'CommittedToModel') ? 'committed'
        : mine.some(s => s.status === 'Rejected') ? 'rejected' : 'none';
    }
    return m;
  }, [equipment, staged]);

  const canStage = !!user && (user.roles.includes('Contributor') || user.roles.includes('BimLead'));
  const canReview = !!user && user.roles.includes('BimLead');
  const pendingCount = staged.filter(s => s.status === 'Pending').length;
  const current = equipment.find(e => e.uniqueId === selected) ?? null;

  if (!authChecked) return null;
  if (!user) return <Login onSignedIn={() => void loadUser()} />;

  if (loadError) {
    return (
      <div style={{ padding: 32 }}>
        <h2>Cannot reach the MEL API</h2>
        <p className="hint" style={{ margin: '8px 0' }}>{loadError}</p>
        <p className="hint">Start the backend (<code>dotnet run</code> in <code>backend/</code>, http://localhost:5080) and reload.</p>
        <button className="btn btn-primary" style={{ marginTop: 12 }} onClick={() => void refresh()}>Retry</button>
      </div>
    );
  }

  return (
    <>
      <Header user={user} onSignOut={signOut} units={units} onUnits={setUnits}
              pendingCount={pendingCount} onReview={() => setReviewOpen(true)}
              aps={aps} canReview={canReview} onConnectAps={() => void connectAps()} onFindModel={() => setFinderOpen(true)} syncing={syncing} />
      <div className="workspace">
        <ViewerPane params={params} selected={selected} canBrowse={canReview}
                    live={aps?.mode === 'Live' && !!aps.connected} onSelect={setSelected}
                    onPick={onViewerPick} onConnect={() => void connectAps()} />
        <EquipmentGrid equipment={equipment} params={params} units={units} selected={selected}
                       status={statusByEquipment} onSelect={setSelected} />
        {current && (
          <Inspector key={current.uniqueId + units + JSON.stringify(current.values)} item={current} params={params} units={units}
                     staged={staged.filter(s => s.equipmentUniqueId === current.uniqueId)}
                     canStage={canStage} status={statusByEquipment[current.uniqueId]}
                     onChanged={refresh} notify={notify} />
        )}
      </div>
      {reviewOpen && (
        <ReviewModal staged={staged} jobs={jobs} equipment={equipment} params={params} units={units} user={user} canReview={canReview}
                     onClose={() => setReviewOpen(false)} onChanged={refresh} notify={notify} />
      )}
      {finderOpen && <ModelFinder onClose={() => setFinderOpen(false)} onLinked={refresh} notify={notify} />}
      {toast && <div className={`toast${toast.error ? ' error' : ''}`}>{toast.msg}</div>}
    </>
  );
}
