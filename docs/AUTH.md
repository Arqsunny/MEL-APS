# Authentication & Authorization

## Model
| Concern | Implementation |
|---------|----------------|
| Authentication | JWT bearer tokens, validated by ASP.NET Core (`AddJwtBearer`). Production: any OIDC provider via `Auth:Authority` + `Auth:Audience` (e.g. Microsoft Entra ID). |
| Authorization | Role policies enforced on the server. The UI only hides buttons; it is never the control. |
| Default | Everything under `/api` requires a signed-in user (fallback policy). Only `GET /api/auth/config`, `POST /api/auth/dev-login` (Development only) and the static SPA shell are anonymous. |
| Identity in audit | `Actor` (username) and `ActorId` (immutable `sub`/`oid`) come from the validated token. Request bodies can no longer name the requester/reviewer. |

## Roles
| Role | Can |
|------|-----|
| *(none)* | Read equipment, staged changes, audit trail |
| `Contributor` | + stage changes |
| `BimLead` | + stage, approve, reject, dispatch to Revit |

Policies: `CanStage` = Contributor or BimLead; `CanReview` = BimLead.

**Segregation of duties:** nobody can approve or reject a change they requested (403). A second BIM Lead must review it. This makes the audit trail meaningful: requester and reviewer are provably different people.

## Configuration
| Setting | Purpose |
|---------|---------|
| `Auth:Authority` | OIDC issuer, e.g. `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `Auth:Audience` | The API's application ID URI / client ID |
| `Auth:DevTokens:*` | Local token issuer for demo users (`Enabled`, `SigningKey`, `Users`). **Development only.** |

Claims read: `preferred_username` (name), `sub`/`oid` (id), `roles` (roles; Entra *app roles* arrive in this claim).

**Fail-fast guards** (verified): the app refuses to start if (a) it is not in Development and neither `Auth:Authority`+`Auth:Audience` is set, or (b) `Auth:DevTokens:Enabled` is true outside Development, or (c) the dev signing key is under 32 bytes. In Production `dev-login` returns 404 and `auth/config` reports `mode: "oidc"`.

## Local development
`appsettings.Development.json` enables the dev issuer with four demo users (`contributor@`, `lead@`, `lead2@`, `viewer@demo.local`). The SPA login screen lists them. The signing key there is for local use only and protects nothing real.

## Connecting Entra ID (needs your tenant admin)
1. Register an **API app** (Expose an API → application ID URI, e.g. `api://mel`). Set `Auth:Audience` to it.
2. Define **app roles** `Contributor` and `BimLead` (allowed member types: Users/Groups) and assign groups/users to them in *Enterprise applications*.
3. Register a **SPA app** (redirect URI of the site) with permission to call the API; users sign in with MSAL.
4. Set `Auth__Authority` and `Auth__Audience` in the deployment environment.
5. In the SPA, replace the dev login with MSAL (`@azure/msal-react`) and pass the access token to `setToken()` in `frontend/src/api.ts`. The API contract does not change.

Not done yet: step 5 (browser SSO), token refresh, and mapping Entra *groups* (rather than app roles) to roles.

## Known limits / next hardening
- The token is held in `sessionStorage` in dev mode; with MSAL, use its token cache and silent refresh.
- No rate limiting or account lockout on the dev issuer (it is not for production).
- Roles are global, not per project/model. Per-model authorization (a BIM Lead for one model only) is a future requirement if needed.
- A BIM Lead who also holds `Contributor` is treated the same; there is no separate "admin" role.
