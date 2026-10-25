import type { ApsStatus, AuthConfig, CurrentUser, EquipmentItem, FolderEntry, Hub, ItemVersion, LinkedModel, ModelScanResult, ParameterDefinition, Project, StagedRecord, SyncJobSummary, ViewerPick, ViewerSession } from './types';

export const MODEL_GUID = 'demo-model';

// The bearer token is kept in sessionStorage (cleared when the tab closes). The identity provider
// integration (MSAL for Entra ID) replaces setToken(); the API contract stays the same.
const TOKEN_KEY = 'mel.token';
export const getToken = () => { try { return sessionStorage.getItem(TOKEN_KEY); } catch { return null; } };
export const setToken = (t: string | null) => {
  try { t ? sessionStorage.setItem(TOKEN_KEY, t) : sessionStorage.removeItem(TOKEN_KEY); } catch { /* storage unavailable */ }
};

let onUnauthorized: () => void = () => {};
export const setUnauthorizedHandler = (fn: () => void) => { onUnauthorized = fn; };

async function call<T>(url: string, init?: RequestInit): Promise<T> {
  const token = getToken();
  const res = await fetch(url, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(init?.headers ?? {}) },
  });
  if (res.status === 401 && token) { setToken(null); onUnauthorized(); }
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error((body as { error?: string }).error ?? `${res.status} ${res.statusText}`);
  return body as T;
}

export const api = {
  authConfig: () => call<AuthConfig>('/api/auth/config'),
  devLogin: (username: string) => call<{ token: string }>('/api/auth/dev-login', { method: 'POST', body: JSON.stringify({ username }) }),
  me: () => call<CurrentUser>('/api/auth/me'),
  parameters: () => call<ParameterDefinition[]>('/api/parameters'),
  equipment: () => call<EquipmentItem[]>(`/api/equipment/${MODEL_GUID}`),
  staged: () => call<StagedRecord[]>(`/api/EquipmentSync/staged/${MODEL_GUID}`),
  stage: (r: { equipmentUniqueId: string; parameterGuid: string; proposedValue: string }) =>
    call('/api/EquipmentSync/stage', { method: 'POST', body: JSON.stringify({ modelGuid: MODEL_GUID, ...r }) }),
  approve: (id: string) =>
    call(`/api/EquipmentSync/approve/${id}`, { method: 'POST', body: JSON.stringify({}) }),
  reject: (id: string, reason: string) =>
    call(`/api/EquipmentSync/reject/${id}`, { method: 'POST', body: JSON.stringify({ reason }) }),
  checkModel: () => call<{ jobId: string }>('/api/sync/check/' + MODEL_GUID, { method: 'POST' }),
  syncJobs: () => call<SyncJobSummary[]>(`/api/sync/jobs/${MODEL_GUID}?limit=5`),
  apsStatus: () => call<ApsStatus>('/api/aps/status'),
  models: () => call<LinkedModel[]>('/api/models'),
  viewerSession: () => call<ViewerSession>('/api/models/' + MODEL_GUID + '/viewer'),
  ensureEquipment: (pick: ViewerPick) =>
    call<EquipmentItem>('/api/equipment/' + MODEL_GUID + '/from-viewer', { method: 'PUT', body: JSON.stringify(pick) }),
  hubs: () => call<Hub[]>('/api/aps/browse/hubs'),
  projects: (hubId: string) => call<Project[]>(`/api/aps/browse/hubs/${encodeURIComponent(hubId)}/projects`),
  contents: (hubId: string, projectId: string, folderId?: string) =>
    call<FolderEntry[]>(`/api/aps/browse/hubs/${encodeURIComponent(hubId)}/projects/${encodeURIComponent(projectId)}/contents${folderId ? `?folderId=${encodeURIComponent(folderId)}` : ''}`),
  versions: (hubId: string, projectId: string, itemId: string) =>
    call<ItemVersion[]>(`/api/aps/browse/hubs/${encodeURIComponent(hubId)}/projects/${encodeURIComponent(projectId)}/versions?itemId=${encodeURIComponent(itemId)}`),
  itemTip: (hubId: string, projectId: string, itemId: string) =>
    call<ItemVersion>(`/api/aps/browse/hubs/${encodeURIComponent(hubId)}/projects/${encodeURIComponent(projectId)}/tip?itemId=${encodeURIComponent(itemId)}`),
  findModels: (hubId: string, projectId: string, search: string) =>
    call<ModelScanResult>(`/api/aps/browse/hubs/${encodeURIComponent(hubId)}/projects/${encodeURIComponent(projectId)}/models?search=${encodeURIComponent(search)}`),
  linkModel: (region: string, projectGuid: string, modelGuid: string, revitVersion: number | null, acc?: { hubId: string; projectId: string; itemId: string }) =>
    call<LinkedModel>(`/api/models/${MODEL_GUID}/cloud-link`, {
      method: 'PUT',
      body: JSON.stringify({
        region, projectGuid, modelGuid, revitVersion,
        accHubId: acc?.hubId, accProjectId: acc?.projectId, accItemId: acc?.itemId,
      }),
    }),
  apsConnect: () => call<{ url: string }>('/api/aps/connect', { method: 'POST' }),
  dispatch: (equipmentUniqueIds: string[]) =>
    call<{ jobId: string; itemsQueued: number }>('/api/EquipmentSync/dispatch-revit-sync', {
      method: 'POST',
      body: JSON.stringify({ modelGuid: MODEL_GUID, equipmentUniqueIds }),
    }),
};
