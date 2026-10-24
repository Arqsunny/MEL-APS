# MEL Management System: Vision & Concept

Source: [MEL-Architecture-Specification.pdf](MEL-Architecture-Specification.pdf) (text copy: `.txt`).

## The idea in one paragraph
A single, bi-directionally synchronized **Master Equipment List** for all MEP/process equipment across multiple Revit models in Autodesk Construction Cloud (ACC). Non-BIM stakeholders (procurement, electrical, field) propose parameter changes in a web app. A **BIM Lead approves** them at a staging gate. Only then does a **headless Revit Design Automation** job write them into the cloud-workshared models, sync with central, and republish so the 3D viewer is current.

## Problem it solves
- Manual re-entry and data decay between Revit, procurement trackers and field data.
- Unreviewed outside edits corrupting central design models.
- Sync-lock friction (people waiting on checked-out worksets).
- No auditable history of who changed which parameter, when, and why.

## Core principles
| # | Principle | Meaning in practice |
|---|-----------|---------------------|
| 1 | Staging gate (Option A) | Nothing reaches a model without BIM Lead approval. |
| 2 | Deterministic identity | Revit `UniqueId` + `MEL_Equipment_GUID` + `MEL_Tag_Number`, with the GUID linking one piece of equipment across A/M/E/S models. |
| 3 | SI at rest | Values are normalized to SI on ingest and stored in SI. Display units are converted at the edge. |
| 4 | Zero desktop footprint | Writes happen in APS Design Automation, not on anyone's workstation. |
| 5 | Full audit | Every change records requester, reviewer, timestamps, old/new value and batch ID. |

## Actors
- **Designer (BIM author):** works in Revit, owns geometry and design parameters.
- **Contributor (procurement / electrical / field):** proposes changes in the web app.
- **BIM Lead:** reviews and approves or rejects staged changes.
- **System:** Design Automation worker, webhooks, publish pipeline.

## End-to-end flow
1. Contributor selects equipment in the 3D viewer or grid and **stages** a change (Pending).
2. BIM Lead reviews the old → new vector and **approves** or **rejects** (reason required).
3. Approved items are batched per model and dispatched as a **Design Automation WorkItem**.
4. The bundle opens the model, applies parameters by shared-parameter GUID, and syncs with central.
5. System triggers an ACC publish, and the Model Derivative job produces new SVF2.
6. The `extraction.finished` webhook refreshes the viewer, and the records become `CommittedToModel`.

## Stakeholder features
Approval-state heatmap, workset lock detection plus 15-minute soft locks, signed QR codes for field scanning, and a cross-discipline coordination matrix (a capacity change in the Mechanical model alerts the Electrical team).

## Success measures (from the spec)
- Full model sync processed in under 3 minutes.
- Zero unreviewed overrides in central models.
- 100% shared-identifier correlation across A/M/E federated models.
