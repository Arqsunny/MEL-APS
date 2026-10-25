export type ApprovalStatus = 'Pending' | 'Approved' | 'Rejected' | 'Dispatched' | 'CommittedToModel';

export interface ParameterDefinition {
  name: string;
  category: string;
  dataType: string;
  siUnit: string | null;
  guid: string;
  description: string;
  userModifiable: boolean;
  stageable: boolean;
}

export interface EquipmentItem {
  modelGuid: string;
  uniqueId: string;
  equipmentGuid: string;
  tag: string;
  familyType: string;
  discipline: string;
  /** keyed by parameter GUID, SI units */
  values: Record<string, string>;
}

export interface StagedRecord {
  id: string;
  modelGuid: string;
  equipmentUniqueId: string;
  parameterGuid: string;
  parameterName: string;
  oldValue: string;
  proposedValue: string;
  status: ApprovalStatus;
  requestedBy: string;
  reviewedBy: string | null;
  rejectionReason: string | null;
  syncJobId: string | null;
  /** why the last sync attempt did not apply this change (it is back in Approved and can be retried) */
  lastError: string | null;
}

export type SyncJobStatus = 'Queued' | 'Submitted' | 'Succeeded' | 'PartiallySucceeded' | 'Failed';

export interface SyncJobSummary {
  id: string;
  status: SyncJobStatus;
  runner: string;
  requestedBy: string;
  createdAt: string;
  completedAt: string | null;
  error: string | null;
  items: number;
}

export interface ApsStatus { mode: string; connected: boolean; account: string | null; supportedRevitYears?: number[] }

export interface Hub { id: string; name: string; region: string | null }
export interface Project { id: string; name: string }
export interface FolderEntry { id: string; name: string; folder: boolean }
export interface ItemVersion { id: string; name: string }
export interface CloudModel {
  itemId: string; name: string; folderPath: string; projectGuid: string; modelGuid: string;
  isComposite: boolean | null; lastModified: string | null;
  /** Revit release the model was last saved in; null when APS does not report it */
  revitVersion: number | null;
  /** true when we run an engine for that release */
  supported: boolean;
}
export interface ModelScanResult {
  hub: string | null; region: string | null; regionKnown: boolean; supportedRevitYears: number[]; models: CloudModel[];
  scan: { foldersVisited: number; revitFilesChecked: number; notCloudWorkshared: number; truncated: boolean; notes: string[] };
}
export interface LinkedModel { modelGuid: string; displayName: string; region: string | null; cloudProjectGuid: string | null; cloudModelGuid: string | null; revitVersion: number | null }

export interface ViewerSession { documentUrn: string; accessToken: string; expiresIn: number; region: string; name: string }

/** An element picked in the viewer. values are TEXT/INTEGER only, keyed by parameter GUID. */
export interface ViewerPick {
  uniqueId: string;
  tag: string;
  familyType: string;
  equipmentGuid: string | null;
  values: Record<string, string>;
}

export type Role = 'Contributor' | 'BimLead';

export interface CurrentUser {
  id: string | null;
  username: string;
  displayName: string | null;
  roles: Role[];
}

export interface DevUser { username: string; displayName: string; roles: Role[] }
export interface AuthConfig { mode: 'dev' | 'oidc'; devUsers: DevUser[] | null }
export type UnitSystem = 'SI' | 'Imperial';
