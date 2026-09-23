# Back Office (OperationsApi) — Frontend Integration Guide

This is the complete, current contract for the Back Office API (`Api/OperationsApi`). It supersedes
`docs/backoffice-api.md`, which predates the roles/permissions rework and is missing several screens — do
not use that file as a reference; it describes a binary Admin/Viewer model that no longer exists.

Everything below was verified against the current endpoint source, and then **exercised over HTTP against a
running host** — every route, the browser auth flow, permission gating with a genuinely restricted user, and
the client snippet in section 0 exactly as printed. Section 23b records what that covered and what it did
not.

**New here? Start at section 0.** It gets a backend running, hands you a working API client, and lists the
five conventions that otherwise cost an afternoon each.

**Related documents**
- `docs/merchant-portal-frontend-integration.md` — the **merchant-facing portal** API (`Api/MerchantPortalApi`,
  the backend for `apps/merchant`). A different host, different cookie, different permission vocabulary. Do not
  mix the two.
- `docs/dev-sample-data.md` — how to populate a local database with realistic merchants, deposits and
  withdrawals so these screens have something to render.

---

## 0. Start here

### Get a backend running

```powershell
./tools/dev/Setup-LocalEnv.ps1                                              # database + all migrations
dotnet run --project src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi
```

That serves `http://localhost:54001`. Dev staff login is `admin` / `ChangeMe_DevOnly!1`, seeded on boot.

An empty database makes every screen look broken in the same way, so populate it before building anything:
see section 23. It takes a minute and is worth it — several screens are unreadable without realistic data.

### CORS is an allow-list, so your dev server port matters

`http://localhost:5173` and `:5174` are allow-listed out of the box. A credentialed request needs an **exact**
origin, never `*`, so if your dev server picks a different port the browser will refuse every call. Add yours
to `Cors:AllowedOrigins` in the Ops host's `appsettings.Development.json`.

### The five things that will bite you

1. **Check `isSuccess`, not the HTTP status alone.** Both are meaningful; the envelope is the contract.
2. **Branch on `errorCode`, never on `error`.** The prose is display text and gets reworded.
3. **Cookie-authenticated writes need `X-CSRF-Token`.** Reads do not. Bearer requests are exempt.
4. **Rates are percent on the wire, basis points internally.** Send percent. Section 5.
5. **Money crosses as a display decimal AND an exact base-unit string.** Render the decimal, compare and
   total the integer. Section 5.

### A client that gets all five right

Copy this. It is the whole contract in about forty lines, and it was run against a live host exactly as
printed — login, an authenticated read, session restore, a CSRF-carrying write, a typed 404, and the 401
redirect path all behave as shown.

```ts
const BASE = 'http://localhost:54001/api/v1/ops';

export interface Envelope<T> {
  isSuccess: boolean;
  data: T | null;
  error: string | null;
  errorCode: string | null;
}

export class ApiError extends Error {
  constructor(readonly code: string, message: string, readonly status: number) {
    super(message);
  }
}

// Yours to supply: clear local session state and route to the login screen. It is called only on a 401,
// so leaving it undefined breaks nothing until a session expires, which is the worst time to find out.
declare function redirectToLogin(): void;

// The CSRF token comes from the login response and from /auth/me. It is deliberately readable by JS:
// useless on its own, because the session itself lives in an httpOnly cookie the page cannot touch.
let csrfToken: string | null = null;
export const setCsrfToken = (t: string | null) => { csrfToken = t; };

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const method = (init.method ?? 'GET').toUpperCase();
  const unsafe = !['GET', 'HEAD', 'OPTIONS'].includes(method);

  const res = await fetch(BASE + path, {
    ...init,
    // Without this the session cookie is never sent and every call is 401.
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...(unsafe && csrfToken ? { 'X-CSRF-Token': csrfToken } : {}),
      ...init.headers,
    },
  });

  // Every response carries the envelope, including binding failures and server faults — see section 2.
  const body = (await res.json()) as Envelope<T>;

  if (!body.isSuccess) {
    // 401 means the session is gone: send the user to login rather than showing an error toast.
    if (res.status === 401) redirectToLogin();
    throw new ApiError(body.errorCode ?? 'unknown', body.error ?? 'Request failed', res.status);
  }

  return body.data as T;
}
```

Login, which is where the CSRF token comes from:

```ts
const session = await api<{ token: string; csrfToken: string; permissions: string[] }>(
  '/auth/login',
  { method: 'POST', body: JSON.stringify({ username, password }) },
);
setCsrfToken(session.csrfToken);
```

On a page reload the cookie survives but the in-memory token does not. Call `GET /auth/me` on boot: it
returns the permissions **and** a fresh `csrfToken`, so it doubles as the session-restore call.

### Drive navigation off `permissions`

The session's `permissions` array is what every screen's visibility should key on. `"*"` is the Admin
wildcard and passes everything. Hide what the user cannot use — but do not rely on hiding it: the server
refuses a denied call with 403 `ops.permission_denied` whether or not the button was on screen. Verified
with a genuinely restricted user, section 23b.

---

## 0a. Every route, at a glance

Generated from the endpoint source, so it cannot drift from what the host actually serves. The last column
is the section in this document that describes the payload.

`{id}` stands for a GUID path parameter. `_any session_` means any authenticated staff member, with no
specific permission — used only for login, the session endpoints, and the two landing-page reads where
gating would hand a new account a blank screen.

| Method | Route | Permission | §  |
|---|---|---|---|
| GET | `/api/v1/ops/accounts` | `ops.accounts.view` | 7 |
| POST | `/api/v1/ops/accounts` | `ops.accounts.manage` | 7 |
| GET | `/api/v1/ops/accounts/{id}` | `ops.accounts.view` | 7 |
| POST | `/api/v1/ops/accounts/{id}/reset-password` | `ops.accounts.manage` | 7 |
| PATCH | `/api/v1/ops/accounts/{id}/role` | `ops.accounts.manage` | 7 |
| PATCH | `/api/v1/ops/accounts/{id}/status` | `ops.accounts.manage` | 7 |
| GET | `/api/v1/ops/audit` | `ops.audit.view` | 8 |
| POST | `/api/v1/ops/auth/login` | _any session_ | 3 |
| POST | `/api/v1/ops/auth/logout` | _any session_ | 3 |
| GET | `/api/v1/ops/auth/me` | _any session_ | 3 |
| POST | `/api/v1/ops/callbacks/{type}/{referenceId}/resend` | `ops.callbacks.manage` | 16 |
| GET | `/api/v1/ops/compliance/addresses` | `ops.compliance.view` | 22b |
| POST | `/api/v1/ops/compliance/deposit-addresses/screen` | `ops.compliance.manage` | 22b |
| GET | `/api/v1/ops/compliance/policy` | `ops.compliance.view` | 22b |
| PUT | `/api/v1/ops/compliance/policy` | `ops.compliance.manage` | 22b |
| GET | `/api/v1/ops/compliance/policy/history` | `ops.compliance.view` | 22b |
| GET | `/api/v1/ops/compliance/screenings` | `ops.compliance.view` | 22b |
| GET | `/api/v1/ops/compliance/screenings/{screeningId}` | `ops.compliance.view` | 22b |
| POST | `/api/v1/ops/compliance/screenings/latest` | `ops.compliance.view` | 22b |
| POST | `/api/v1/ops/compliance/screenings/re-screen` | `ops.compliance.manage` | 22b |
| GET | `/api/v1/ops/dashboard` | _any session_ | 16b |
| GET | `/api/v1/ops/energy/operations` | `ops.energy.view` | 22 |
| GET | `/api/v1/ops/energy/resources` | `ops.energy.view` | 22 |
| GET | `/api/v1/ops/merchants` | `ops.merchants.view` | 9 |
| POST | `/api/v1/ops/merchants` | `ops.merchants.manage` | 9 |
| GET | `/api/v1/ops/merchants/{id}` | `ops.merchants.view` | 9 |
| GET | `/api/v1/ops/merchants/{id}/allowed-ips` | `ops.merchants.view` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/allowed-ips` | `ops.merchants.manage` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/approval-threshold` | `ops.fees.manage` | 9 |
| POST | `/api/v1/ops/merchants/{id}/balance/credit` | `ops.balances.adjust` | 9 |
| POST | `/api/v1/ops/merchants/{id}/balance/debit` | `ops.balances.adjust` | 9 |
| GET | `/api/v1/ops/merchants/{id}/balance/history` | `ops.merchants.view` | 9 |
| POST | `/api/v1/ops/merchants/{id}/close` | `ops.merchants.manage` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/deposit-limits` | `ops.fees.manage` | 9 |
| GET | `/api/v1/ops/merchants/{id}/fees` | `ops.fees.view` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/fees` | `ops.fees.manage` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/payout-approval` | `ops.merchants.manage` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/profile` | `ops.merchants.manage` | 9 |
| POST | `/api/v1/ops/merchants/{id}/regenerate-key` | `ops.merchants.rotate-key` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/settlement-period` | `ops.merchants.manage` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/settlement-wallet` | `ops.merchants.manage` | 9 |
| POST | `/api/v1/ops/merchants/{id}/settlement-wallets` | `ops.merchants.manage` | 19 |
| POST | `/api/v1/ops/merchants/{id}/settlement-wallets/{walletId}/activate` | `ops.merchants.manage` | 19 |
| POST | `/api/v1/ops/merchants/{id}/settlement-wallets/{walletId}/retire` | `ops.merchants.manage` | 19 |
| PATCH | `/api/v1/ops/merchants/{id}/status` | `ops.merchants.manage` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/withdrawal-cap` | `ops.fees.manage` | 9 |
| PUT | `/api/v1/ops/merchants/{id}/withdrawal-limits` | `ops.fees.manage` | 9 |
| GET | `/api/v1/ops/merchants/next-code` | `ops.merchants.view` | 9 |
| POST | `/api/v1/ops/payment-intents/{reference}/fail` | `ops.deposits.manage` | 12 |
| GET | `/api/v1/ops/permissions` | `ops.roles.view` | 3 |
| GET | `/api/v1/ops/reconciliation` | _any session_ | 16b |
| GET | `/api/v1/ops/roles` | `ops.roles.view` | 6 |
| POST | `/api/v1/ops/roles` | `ops.roles.manage` | 6 |
| DELETE | `/api/v1/ops/roles/{id}` | `ops.roles.manage` | 6 |
| GET | `/api/v1/ops/roles/{id}` | `ops.roles.view` | 6 |
| PUT | `/api/v1/ops/roles/{id}` | `ops.roles.manage` | 6 |
| PUT | `/api/v1/ops/roles/{id}/permissions` | `ops.roles.manage` | 6 |
| GET | `/api/v1/ops/settlement-activity` | `ops.treasury.manage` | 20b |
| GET | `/api/v1/ops/sweeps` | `ops.sweep.view` | 22 |
| GET | `/api/v1/ops/sweeps/policies` | `ops.sweep.view` | 22 |
| POST | `/api/v1/ops/sweeps/scan/{chain}` | `ops.sweep.manage` | 22 |
| GET | `/api/v1/ops/sweeps/settings` | `ops.sweep.view` | 22 |
| PUT | `/api/v1/ops/sweeps/settings/{chain}` | `ops.sweep.manage` | 22 |
| GET | `/api/v1/ops/transactions` | `ops.transactions.view` | 13 |
| GET | `/api/v1/ops/transactions/deposits` | `ops.deposits.view` | 13 |
| GET | `/api/v1/ops/transactions/deposits/{systemOrderNumber}` | `ops.deposits.view` | 13 |
| GET | `/api/v1/ops/transactions/withdrawals` | `ops.withdrawals.view` | 13 |
| GET | `/api/v1/ops/transactions/withdrawals/{systemOrderNumber}` | `ops.withdrawals.view` | 13 |
| POST | `/api/v1/ops/treasury/cold-wallet` | `ops.treasury.manage` | 20 |
| GET | `/api/v1/ops/treasury/cold-wallets` | `ops.treasury.manage` | 20 |
| POST | `/api/v1/ops/treasury/cold-wallets` | `ops.treasury.manage` | 20 |
| POST | `/api/v1/ops/treasury/cold-wallets/{walletId}/activate` | `ops.treasury.manage` | 20 |
| POST | `/api/v1/ops/treasury/cold-wallets/{walletId}/re-screen` | `ops.treasury.manage` | 20 |
| POST | `/api/v1/ops/treasury/cold-wallets/{walletId}/retire` | `ops.treasury.manage` | 20 |
| GET | `/api/v1/ops/treasury/hot-pool` | `ops.treasury.manage` | 20 |
| POST | `/api/v1/ops/treasury/top-up` | `ops.treasury.manage` | 20 |
| GET | `/api/v1/ops/wallets` | `ops.wallets.view` | 11 |
| GET | `/api/v1/ops/wallets/{id}` | `ops.wallets.view` | 11 |
| POST | `/api/v1/ops/wallets/{id}/resume` | `ops.wallets.manage` | 11 |
| POST | `/api/v1/ops/wallets/{id}/suspend` | `ops.wallets.manage` | 11 |
| POST | `/api/v1/ops/withdrawals/{withdrawalId}/approve` | `ops.withdrawals.approve` | 14 |
| POST | `/api/v1/ops/withdrawals/{withdrawalId}/audit-approve` | `ops.withdrawals.approve` | 14 |
| POST | `/api/v1/ops/withdrawals/{withdrawalId}/audit-reject` | `ops.withdrawals.approve` | 14 |
| POST | `/api/v1/ops/withdrawals/{withdrawalId}/cancel` | `ops.withdrawals.manage` | 14 |
| POST | `/api/v1/ops/withdrawals/{withdrawalId}/record-settlement` | `ops.withdrawals.manage` | 14 |
| POST | `/api/v1/ops/withdrawals/{withdrawalId}/reject` | `ops.withdrawals.approve` | 14 |
| POST | `/api/v1/ops/withdrawals/{withdrawalId}/release` | `ops.withdrawals.manage` | 14 |

---

## 0b. Suggested build order

Each row lists everything that screen needs. Later rows depend on earlier ones existing.

| # | Screen | Endpoints | Notes |
|---|---|---|---|
| 1 | Login + session shell | `POST /auth/login`, `GET /auth/me`, `POST /auth/logout` | Nothing else works until the cookie, CSRF token and permission-driven nav are right. |
| 2 | Dashboard | `GET /dashboard`, `GET /reconciliation` | No permission gate. Degrades when Mongo is down — see section 16b, and render `null` counts as unknown, never as zero. |
| 3 | Merchants list + detail | `GET /merchants`, `GET /merchants/{id}` | The spine of the product. Detail carries settlement wallets with their screening verdicts. |
| 4 | Merchant terms | fees, limits, caps, settlement-period, approval-threshold | Section 19. All percent-on-the-wire. |
| 5 | Transactions | `GET /transactions/deposits`, `.../withdrawals`, detail endpoints | Section 12-13. Use the effective-status vocabulary in section 18. |
| 6 | Withdrawal approvals | `POST /withdrawals/{id}/approve`, `/reject`, `/release`, `/cancel` | The first screen that moves money. Read section 13 carefully. |
| 7 | Roles + accounts | sections 6 and 7 | Needed before anyone but the seeded admin can use the portal. |
| 8 | Wallets, sweeps, energy, reconciliation | sections 11, 21, 22 | Read-only operational views. |
| 9 | Treasury | section 20 | Hot-pool balances and top-up recording. Nothing on this screen sends funds. |
| 10 | Compliance | section 22b | Screening evidence, the policy settings screen, and the deposit-address sweep. |

Screens with no backend at all are listed in section 24. Do not start those.

---

## 1. Base URL

Dev default: `http://localhost:54001` (HTTPS on `54000`, self-signed dev cert — not trusted by default).
Production base URL is whatever the deployment target configures; ask ops/DevOps for the real value, it is
not baked into this doc.

All routes are prefixed `/api/v1/ops/...`. There is no versioning beyond `v1` today.

---

## 2. Response envelope — every single endpoint, success or failure

```json
{ "isSuccess": true,  "data": { /* endpoint-specific */ }, "error": null, "errorCode": null }
{ "isSuccess": false, "data": null, "error": "human-readable message", "errorCode": "stable.code" }
```

Always check `isSuccess`, not just the HTTP status — but the HTTP status is also meaningful (see below).

**The envelope is guaranteed on every response, including the ones no endpoint produced.** A request that
fails model binding before a handler runs (a required query parameter missing, a malformed JSON body) and an
unhandled server fault both used to escape as a raw framework response with no envelope and no code. A
host-wide handler now catches both, so a client never has to special-case "sometimes there is no envelope".
Verified by probing every route.

**`errorCode` is the field to branch on.** It is a stable dotted string; `error` is display prose that
gets reworded and localised, so never pattern-match on it. Both fields are present on every response
(null on success), so the envelope shape never changes between the two paths.

Codes come from two places:

- **Module/domain failures** carry the module's own code — `wallet.not_found`, `wallet.not_suspended`,
  `withdrawal.duplicate_reference`, `payment_intent.invalid_state_transition`, and so on. The prefix is
  the owning module.
- **Host-level input validation and auth** use the `ops.*` catalog:

| Code | Status | Raised when |
|---|---|---|
| `ops.unauthenticated` | 401 | No/invalid/expired session (bearer header or cookie) |
| `ops.invalid_credentials` | 401 | Login failed |
| `ops.csrf_invalid` | 403 | Cookie-authenticated unsafe method without a matching `X-CSRF-Token` |
| `ops.permission_denied` | 403 | Valid session, missing the route's permission code |
| `ops.not_found` | 404 | A single-record detail lookup matched nothing |
| `ops.invalid_chain` | 400 | Unparseable `chain` / `network` |
| `ops.invalid_status` | 400 | Unknown `status` filter value |
| `ops.invalid_wallet_type` | 400 | Unknown `walletType` filter value |
| `ops.invalid_withdrawal_kind` | 400 | `kind` was not `user`/`merchant` |
| `ops.invalid_kind` | 400 | Unknown kind on the energy-operations filter |
| `ops.invalid_callback_type` | 400 | Callback `type` was not `deposit`/`withdrawal` |
| `ops.invalid_asset` | 400 | Unknown coin, or no asset configured for the chain |
| `ops.invalid_amount` | 400 | Negative, or finer than the asset's precision |
| `ops.invalid_hex` | 400 | A signed-transaction blob was not valid hex |
| `ops.invalid_ip_address` | 400 | Every submitted IP/CIDR was malformed |
| `ops.network_required` | 400 | `coin` filter supplied without `network` |
| `ops.merchant_id_required` | 400 | `transactionId` filter supplied without `merchantId` |
| `ops.malformed_request` | 400 | The request never reached a handler: a required query parameter absent, or a body that is not valid JSON |
| `ops.too_many_addresses` | 400 | A batch screening lookup sent more than 200 addresses. Refused, never truncated — split the request |
| `ops.internal_error` | 500 | An unhandled server fault. The detail is logged, never returned |
| `compliance.*` | 400 | Screening-policy validation — see section 22b |

### HTTP status codes used

| Status | Meaning |
|---|---|
| 200 | Success |
| 400 | Bad request — validation failure, malformed input, unknown enum value in a query param |
| 401 | No/invalid/expired bearer token |
| 403 | Valid session, but missing the required permission code for this route |
| 404 | Entity not found |
| 409 | Conflict — invalid state transition (e.g. approving an already-approved withdrawal, resuming a wallet that isn't suspended) |
| 500 | Unexpected server error |

---

## 3. Authentication & session model

A session is a **server-side opaque token** (revocable, fixed-TTL). It can be presented **two ways** — pick one
per client:

- **Cookie mode (recommended for the browser SPA).** On login the server sets an **httpOnly** `cpe_ops_session`
  cookie; the browser sends it automatically. The session token is never exposed to JavaScript (no
  sessionStorage/XSS surface). Because a cookie is sent automatically, **every state-changing request
  (POST/PUT/PATCH/DELETE) must also send an `X-CSRF-Token` header** (see below). Send `credentials: 'include'`
  on every request.
- **Bearer mode (non-browser clients, or the interim SPA transport).** Send `Authorization: Bearer <token>`
  using the `token` from the login response. Bearer requests are **exempt from CSRF** (a header credential is
  not sent automatically). No cookie needed.

Both modes are served by the same login endpoint — it returns `token` **and** sets the cookie **and** returns
`csrfToken`; the client uses whichever it needs.

**CSRF (cookie mode only):** the login and `/auth/me` responses return a `csrfToken`. Hold it in memory and send
it as the `X-CSRF-Token` header on every POST/PUT/PATCH/DELETE. A missing/incorrect token on a cookie-authenticated
write → **403** `{"error":"Missing or invalid CSRF token…"}`. It is bound to the session (rotates on login, dies on
logout). After a page refresh (which clears the in-memory copy but keeps the httpOnly cookie), call `GET /auth/me`
to re-obtain it. The CSRF token grants nothing on its own — it is only meaningful alongside the httpOnly session
cookie, so keeping it in JS memory is safe.

**CORS (cross-origin SPA):** the API must list your UI origin in `Cors:AllowedOrigins` (exact origin, never `*`)
for the browser to send/receive the cookie; the dev server seeds the Vite origins. Cookie `SameSite` defaults to
`Lax` (correct for a UI + API on the same registrable domain); a cross-registrable-domain split needs `SameSite=None`
(server config `Auth:Cookie`).

### `POST /api/v1/ops/auth/login` — the only unauthenticated endpoint besides `/health`

Request:
```json
{ "username": "string, required, max 64", "password": "string, required, max 256" }
```

Response 200:
```json
{
  "isSuccess": true,
  "data": {
    "token": "opaque bearer token string (use in bearer mode; ignore in cookie mode)",
    "csrfToken": "opaque CSRF token (cookie mode: send as X-CSRF-Token on writes)",
    "expiresAt": "2026-08-19T18:00:00+00:00",
    "username": "admin",
    "role": "Admin",
    "permissions": ["*"]
  },
  "error": null
}
```
A successful login also sets the httpOnly `cpe_ops_session` cookie (`Set-Cookie`). Response 401 on bad
credentials: `{ "isSuccess": false, "error": "..." }`.

Session TTL defaults to **8 hours** (server-configured, `StaffAuthOptions.SessionTtlHours`). There is no
refresh-token flow — when the session expires, every request 401s and the frontend must send the user back
to login.

### `POST /api/v1/ops/auth/logout`

No body. Revokes the current session server-side (not just a client-side token discard) and clears the session
cookie. In cookie mode this is a state-changing POST, so it **requires the `X-CSRF-Token` header** like any other write.
Response 200: `{ "isSuccess": true, "data": { "loggedOut": true }, "error": null }`.

### `GET /api/v1/ops/auth/me` — any valid session, no specific permission needed

Call this on app load / after login to know what to render.
```json
{
  "isSuccess": true,
  "data": {
    "staffUserId": "guid",
    "username": "admin",
    "role": "Admin",
    "permissions": ["*"],
    "csrfToken": "opaque CSRF token — re-obtain here after a refresh (cookie mode)"
  },
  "error": null
}
```

### The permission model — read this carefully, it drives every screen's visibility

- Every route requires one specific **permission code** (e.g. `ops.wallets.manage`) except
  `/auth/login`, `/auth/logout`, `/auth/me`, and `/health`.
- A permission code is either held or not — there's no partial/read-vs-write split beyond what the code
  name itself encodes (e.g. `ops.merchants.view` vs `ops.merchants.manage` are two separate codes).
- The special code **`"*"`** (wildcard) grants everything — the seeded dev Admin role holds only `["*"]`.
- **Enforcement is server-side and absolute.** Missing the permission → `403 Forbidden` with
  `{"isSuccess": false, "error": "Missing permission 'ops.xxx.yyy'."}`, regardless of what the frontend
  shows or hides. **The frontend must never treat hiding a button as sufficient security** — always handle
  a 403 gracefully (e.g. toast "not authorized"), because a user can always hit the API directly.
- **Frontend nav visibility convention:** derive "should I show this module/button" from
  `permissions` in `/auth/me` — e.g. show the Wallets nav item if any `permissions` entry starts with
  `ops.wallets.` (or equals `"*"`). This is a UX nicety only, not the security boundary.

### Full permission code catalog — `GET /api/v1/ops/permissions` (needs `ops.roles.view`)

```json
{ "isSuccess": true, "data": { "permissions": ["ops.merchants.view", "ops.merchants.manage", "..."] }, "error": null }
```

The full, current list (also use this endpoint at runtime — don't hardcode, this can grow):

| Code | Grants |
|---|---|
| `ops.merchants.view` | Read merchant list/detail/allowed-IPs |
| `ops.merchants.manage` | Create merchant, activate/suspend, update allowed IPs |
| `ops.merchants.rotate-key` | Regenerate a merchant's API credential |
| `ops.fees.view` | Read a merchant's fee schedule |
| `ops.fees.manage` | Set a merchant's fee schedule |
| `ops.deposits.view` | Read the deposit transaction screen |
| `ops.deposits.manage` | Manually fail a stuck payment intent |
| `ops.withdrawals.view` | Read the withdrawal transaction screen |
| `ops.withdrawals.approve` | Approve/reject a `pending_approval` withdrawal |
| `ops.withdrawals.manage` | Release/cancel a funding-hold withdrawal |
| `ops.transactions.view` | Read the ledger-wide journal search |
| `ops.callbacks.manage` | Manually resend an abandoned callback |
| `ops.roles.view` | Read roles + the permission catalog |
| `ops.roles.manage` | Create/edit/delete roles, set a role's permissions |
| `ops.accounts.view` | Read staff accounts |
| `ops.accounts.manage` | Create accounts, change status/role, reset password |
| `ops.audit.view` | Search the audit log |
| `ops.wallets.view` | Search/browse wallets |
| `ops.wallets.manage` | Suspend/resume a wallet |
| `ops.balances.adjust` | Manually credit/debit a merchant's ledger balance — granted to the Admin role only by default (see §9a) |

---

## 4. Pagination — identical convention on every list/search endpoint

Query params: `?page=1&pageSize=50` — `page` is 1-based, `pageSize` clamped server-side to `[1, 200]`
(defaults to 50 if omitted or invalid).

Response shape:
```json
{
  "isSuccess": true,
  "data": { "page": 1, "pageSize": 50, "totalCount": 137, "items": [ /* ... */ ] },
  "error": null
}
```

---

## 5. Money / decimals

All monetary fields on the wire are **decimal display values** (e.g. `1.5` USDT), already converted from
base units server-side. Never send/parse money as strings elsewhere in this API — this is the one place it
crosses to display form.

### Percent vs basis points — standardized, read this before building any rate field

**Every percentage-shaped field on this API — request or response — is a plain percent, never basis points.**
`2` means 2%, `50` means 50%. The frontend should never construct or display a bps value; it only ever
sends/reads plain percent. (An earlier version of this doc said the opposite — write as bps, read as percent
— that was wrong and has been corrected; if you built against that, you were reading raw bps as if it were
percent, e.g. `120` displaying as "120%" instead of "1.2%" — that's the exact bug reported against the
merchant fee screen.)

- **At most 2 decimal places.** `1.65` is fine; `1.655` is **rejected with 400**, never silently rounded —
  same "never truncate money" rule the API applies to amounts (§14). If a user needs finer precision than
  2 decimal places on a rate, that's a product conversation, not something to work around client-side.
- **Range 0–100 inclusive** (`0`–`100`, not `0`–`1`). `100` = 100% is legal on some fields (e.g. a deposit fee
  that credits the merchant nothing) even though it's an unusual value to actually set.
- **Every field that carries this today:** `depositFeePercent`, `withdrawalFeePercent`, `topUpFeePercent`
  (§10, both the create-merchant `fees` block and `PUT/GET .../fees`), and `percent` on
  `PUT .../withdrawal-cap` (§19), read back as `merchantWithdrawalCapPercent`.
- Internally the backend converts to basis points (`percent × 100`) once, at the API boundary, and every
  domain calculation and DB column is in bps from then on — that's purely internal; nothing upstream of this
  API's request/response layer should ever see, send, or expect a bps number.

---

## 6. Roles

### `GET /api/v1/ops/roles` — `ops.roles.view`
Paginated. Row shape:
```json
{ "roleId": "guid", "name": "Admin", "description": "string|null", "permissionCodes": ["*"], "createdAt": "..." }
```

### `GET /api/v1/ops/roles/{id}` — `ops.roles.view`
Same row shape, single object in `data`. 404 if not found.

### `POST /api/v1/ops/roles` — `ops.roles.manage`
Request:
```json
{ "name": "string, required, max 64", "description": "string, optional, max 256", "permissionCodes": ["ops.merchants.view"] }
```
Response 200: the created role row. 409 if the name already exists.

### `PUT /api/v1/ops/roles/{id}` — `ops.roles.manage`
Request: `{ "name": "...", "description": "..." }` — updates name/description only.

### `PUT /api/v1/ops/roles/{id}/permissions` — `ops.roles.manage`
Request: `{ "permissionCodes": ["ops.merchants.view", "ops.deposits.view"] }` — **replaces the full set**,
not a diff/patch. Send every code the role should hold, every time.

### `DELETE /api/v1/ops/roles/{id}` — `ops.roles.manage`
No body. 409 with a specific error if any staff account still holds this role — reassign them first.

---

## 7. Accounts (staff users who can log into this Back Office)

Passwords are **never** sent by the client. Create and reset-password both generate a strong random
password server-side and return it **exactly once** — the frontend must show it prominently with a copy
button and an explicit "this will never be shown again" warning, same treatment as merchant API secrets.

### `GET /api/v1/ops/accounts` — `ops.accounts.view`
Paginated. Row:
```json
{ "staffUserId": "guid", "username": "admin", "roleId": "guid", "roleName": "Admin", "status": "Active", "createdAt": "..." }
```
`status` is `"Active"` or `"Disabled"` (PascalCase — note this differs from the lowercase-snake vocab used
on deposit/withdrawal status, see §14).

### `GET /api/v1/ops/accounts/{id}` — `ops.accounts.view`
Same row shape.

### `POST /api/v1/ops/accounts` — `ops.accounts.manage`
Request: `{ "username": "string, required, max 64", "roleId": "guid, required" }`
Response 200:
```json
{ "isSuccess": true, "data": { "staffUserId": "guid", "username": "...", "password": "one-time-shown", "warning": "Store this password securely — it will never be shown again." }, "error": null }
```

### `PATCH /api/v1/ops/accounts/{id}/status` — `ops.accounts.manage`
Request: `{ "active": true|false }`
409 if: disabling your own currently-logged-in account, or disabling the last remaining active account
(system refuses to let you lock everyone out).

### `PATCH /api/v1/ops/accounts/{id}/role` — `ops.accounts.manage`
Request: `{ "roleId": "guid" }`

### `POST /api/v1/ops/accounts/{id}/reset-password` — `ops.accounts.manage`
No body. Same one-time-password response shape as create.

---

## 8. Audit log (read-only — written internally, never via this API)

### `GET /api/v1/ops/audit` — `ops.audit.view`
Every action taken through any *mutating* endpoint in this whole API gets logged here automatically.

Query filters (all optional, AND-combined): `staffUserId`, `action`, `entityType`, `entityId`, `fromDate`,
`toDate`, plus standard `page`/`pageSize`.

Row:
```json
{
  "id": "guid",
  "staffUserId": "guid",
  "staffUsername": "admin",
  "action": "wallet.suspended",
  "entityType": "Wallet",
  "entityId": "guid-as-string",
  "reason": "string|null — carries context like the suspend reason or fee change details",
  "ipAddress": "string|null",
  "createdAt": "..."
}
```
`action` values you'll see (not an exhaustive enum, just whatever endpoints log — grep-able, roughly):
`merchant.created`, `merchant.status_changed`, `merchant.key_rotated`, `merchant.allowed_ips_updated`,
`merchant.fee_updated`, `payment_intent.failed`, `wallet.suspended`, `wallet.resumed`, `role.created`,
`role.updated`, `role.permissions_changed`, `role.deleted`, `account.created`, `account.status_changed`,
`account.role_changed`, `account.password_reset`, `withdrawal.approved`, `withdrawal.rejected`,
`withdrawal.released`, `withdrawal.cancelled`, `callback.resent`.

---

## 9. Merchants

### `GET /api/v1/ops/merchants` — `ops.merchants.view`
Paginated. Row (`MerchantAdminView`):
```json
{ "merchantId": "guid", "merchantCode": "ACME-1", "name": "Acme Inc", "status": "Active", "createdAt": "...", "hasActiveCredential": true, "allowedIps": ["1.2.3.4"] }
```
`status` ∈ `Active | Frozen | Closed` (PascalCase). No separate "Pending" review state — a merchant is
`Active` immediately on creation.

### `GET /api/v1/ops/merchants/{id}` — `ops.merchants.view`
`data` is the same row shape as the list above, **unchanged** — this is a deployed contract and stays flat.
A new **sibling field**, `balances`, rides alongside it in the same response with the merchant's ledger
balance for every active asset (zero balances are included, not omitted, so the array's length is stable):
```json
{
  "isSuccess": true,
  "data": { "merchantId": "guid", "merchantCode": "ACME-1", "...": "..." },
  "balances": [
    { "assetId": "guid", "network": "Tron", "coin": "USDT", "balance": 1250.5, "balanceBaseUnits": "1250500000" }
  ],
  "error": null
}
```
`balance` is the usual display decimal (§5); `balanceBaseUnits` is the exact integer in base units as a
**string** (same "watch this one" exception as §17's ledger search) — prefer it over `balance` for anything
that needs to round-trip exactly, since `balance` can lose precision once cast through a client-side float.

### `GET /api/v1/ops/merchants/{id}/allowed-ips` — `ops.merchants.view`
```json
{ "merchantId": "guid", "allowedIps": ["1.2.3.4"], "apiAccessBlocked": false }
```
**The allowlist is enforced.** Every signed merchant API call (`/api/v1/*` on the merchant gateway) must come from
an address on this list, or it is refused with **403 "IP address not whitelisted."**. **An empty list refuses
every call**, and `apiAccessBlocked: true` says so: show it prominently on the merchant screen, because that
merchant's integration is switched off. The pay page and the portal logins are not affected.

### `GET /api/v1/ops/merchants/next-code` — `ops.merchants.view`
Preview of the code `POST .../merchants` will most likely mint next (`ME00001`, `ME00002`, ... — one past the
highest existing `ME#####`-shaped code; a hand-picked legacy/test code that doesn't match that exact shape is
ignored and can never perturb it). For the create-merchant form to show before submission.

Response 200:
```json
{ "isSuccess": true, "data": { "nextMerchantCode": "ME00042" }, "error": null }
```
**Not a reservation.** If two staff open the create form at the same moment, both could see the same preview
— only one will actually get it; the real arbiter is the unique-code retry already built into creation, which
silently rolls to the next number on a collision. Re-fetch this right before showing the form each time,
don't cache it.

### `POST /api/v1/ops/merchants` — `ops.merchants.manage`
Creates **and auto-activates** the merchant (no separate activation step), and seeds **exactly one**
deposit wallet (not a pool — every wallet after the first is minted on-demand as the merchant needs one).

`merchantCode` is **server-generated** (`ME00001`, `ME00002`, ...) — never supplied by the caller. There is
no `callbackUrl` field either: deposit/withdrawal webhook delivery resolves its target entirely from the
`callbackUrl` on each individual `/deposit`/`/withdraw` request, never from anything stored on the merchant,
so nothing is lost by not collecting one here.

Request:
```json
{
  "name": "string, required, max 256",
  "contactEmail": "string, optional, max 256",
  "remark": "string, optional, max 1024",
  "settlementDays": 0,
  "settlementMode": "manual",
  "fees": {
    "depositFeeFixed": 0,
    "depositFeePercent": 2,
    "depositFeeMinimum": 0.5,
    "withdrawalFeeFixed": 1,
    "withdrawalFeePercent": 1,
    "withdrawalFeeMinimum": 2
  }
}
```
- `settlementDays`: T+N in whole days, 0-30 (0 = withdrawable immediately). The UI can offer 0/1/2 as presets;
  the backend accepts the full range.
- `settlementMode`: `"auto"` or `"manual"`, case-insensitive, defaults to `"manual"` if omitted. **Record only
  today** — no automated settlement logic runs yet regardless of this value; every merchant behaves as manual.
- `fees` is **entirely optional** — omit it to create the merchant unpriced (it falls back to the platform
  default fee, exactly as before this field existed). When present, all six sub-fields are required numbers
  (`0` is valid — e.g. `depositFeeMinimum: 0` means no minimum-fee floor). `*Percent` is a **plain percent**
  (`2` = 2%), at most 2 decimal places — a finer value (e.g. `2.505`) is rejected with 400, never rounded.
  `*Minimum` is the 最低手续费 floor: `max(fixed + amount × percent, minimum)`. Only TRX/USDT is priceable
  today; this always prices that asset. A failed/invalid `fees` block does **not** fail merchant creation —
  it's logged and the merchant is created unpriced; price it afterward via `PUT .../fees`.

Response 200:
```json
{
  "isSuccess": true,
  "data": {
    "merchantId": "guid",
    "merchantCode": "ME00001",
    "apiKey": "string",
    "apiSecret": "string — shown once, never retrievable again",
    "signingSecret": "string — shown once, never retrievable again",
    "wallet": { "chain": "Tron", "address": "T..." },
    "portalAccount": { "username": "me00001", "temporaryPassword": "string — shown once, never retrievable again" }
  },
  "error": null
}
```
**Critical UI requirement:** `apiSecret`, `signingSecret`, and `portalAccount.temporaryPassword` are each shown
**exactly once**, here, and can never be fetched again afterward (not even via `GET /merchants/{id}`) — show
them prominently with a copy button and an explicit "save this now" warning, same treatment for all three.

`wallet` can be `null` if seed provisioning failed server-side (logged); this does not indicate a problem
worth surfacing loudly — the merchant is still fully usable, the first deposit call just provisions a wallet
synchronously instead. **`portalAccount` can be `null` the same way** — if it is, the merchant has no way to
log into its own portal yet; retry via `POST /ops/merchants/{id}/portal-account` (§9a below).

400 on an invalid `settlementMode`, an invalid `fees` percent (negative, >100%, or finer than 2 decimals), or
a negative/over-precise fixed/minimum amount.

### `POST /api/v1/ops/merchants/{id}/portal-account` — `ops.merchants.manage`
Bootstraps a merchant's **first** portal login — the piece the portal's own self-service account creation
(`POST /api/v1/portal/accounts`, on `MerchantPortalApi`) cannot do, since that endpoint requires an
already-authenticated portal session, and a brand-new merchant has none. Called automatically as part of
`POST /ops/merchants` above; this endpoint exists for a merchant that doesn't have a portal account yet for
any reason (that call failed, or the merchant predates this feature).

No body. Response 200:
```json
{ "isSuccess": true, "data": { "merchantId": "guid", "username": "me00001", "temporaryPassword": "string — shown once" }, "error": null }
```
- **Username is always the merchant's own code, lowercased** (`ME00001` → `me00001`) — not configurable,
  guaranteed unique since merchant codes already are.
- The account is granted a **full-access "Admin" role**, auto-created for the tenant on first use (a
  brand-new merchant has zero roles too, same bootstrap problem) — the merchant's own admin can create
  narrower roles for teammates afterward, entirely self-service from there via `POST /portal/roles`.
- **409** (`merchant.portal_account_exists`) if the merchant already has a portal account — this never
  creates a second one; use the portal's own account management (or a password reset) instead. 404 if the
  merchant doesn't exist.

### `GET /api/v1/ops/merchants/{id}/portal-accounts` — `ops.merchants.manage`
Lists this merchant's portal accounts — a merchant can have more than one (its own admin may have added
teammates via the portal's self-service `POST /portal/accounts`), so staff need this before resetting one.

Response 200:
```json
{
  "isSuccess": true,
  "data": {
    "merchantId": "guid",
    "accounts": [
      { "merchantUserId": "guid", "username": "me00002", "displayName": "...", "roleId": "guid|null",
        "roleName": "Admin|null", "status": "Active|Disabled", "mustChangePassword": true, "createdAt": "..." }
    ]
  },
  "error": null
}
```

### `POST /api/v1/ops/merchants/{id}/portal-accounts/{accountId}/reset-password` — `ops.merchants.manage`
Staff-triggered password reset — for when a merchant's admin is locked out and has nobody else inside its own
portal to reset it for them. No body. **Invalidates the old password immediately.**

Response 200:
```json
{ "isSuccess": true, "data": { "merchantId": "guid", "merchantUserId": "guid", "username": "me00002", "temporaryPassword": "string — shown once" }, "error": null }
```
Same one-time-password treatment as everywhere else: shown exactly once, here, never recoverable afterward.
404 if the account doesn't belong to this merchant.

**What "temporary" actually means for this password** (same for the bootstrap `portal-account` create above):
it is a normal password, not a single-use code — it keeps working for every login until the merchant actually
changes it via the portal's own `POST /account/change-password`, and it does **not expire on its own**. The
merchant portal's login response carries `mustChangePassword: true` as a nudge, but nothing on the backend
enforces it — see `docs/merchant-portal-frontend-integration.md` §3 for the full behavior if the portal UI
ever needs to act on it.

### `PUT /api/v1/ops/merchants/{id}/profile` — `ops.merchants.manage`
Write-only — updates the staff-facing profile fields. Every field is optional; the caller sends only what
changed. An **omitted** field is left unchanged; an explicit **empty string** clears `contactEmail`/`remark`.
There is no read-back shape from this endpoint (read the current values back via `GET /merchants/{id}`).

Request (send only what changed):
```json
{ "contactEmail": "ops@merchant.com", "settlementMode": "auto", "remark": "VIP account" }
```
Response 200: `{ "merchantId": "guid" }`. 404 on an unknown `id`, 400 on an invalid `settlementMode`.

### `PATCH /api/v1/ops/merchants/{id}/status` — `ops.merchants.manage`
Request: `{ "active": true|false }` (`true` → activate, `false` → freeze).
Response: `{ "merchantId": "guid", "status": "Active" }` (or `"Frozen"`).

Status is **never terminal** — this also reopens a `Closed` merchant: `active: true` moves a Closed
merchant to `Active`, `active: false` moves it to `Frozen`. There is no invalid Active/Frozen/Closed
transition through this endpoint; the only failure is an unknown `id` (404).

### `POST /api/v1/ops/merchants/{id}/close` — `ops.merchants.manage`
No body. Closes the merchant from either `Active` or `Frozen`. Reversible — use `PATCH .../status` above
to reopen it (`active: true` → `Active`, `active: false` → `Frozen`). Closing does **not** touch any other
merchant data (credentials, policies, settlement wallet, etc. are untouched, just inert while Closed) and
does **not** stop already-confirmed on-chain deposits from crediting the ledger (§14) — it only blocks new
activity (deposit-address issuance, payouts, credential/config/policy changes — every one of those has its
own independent guard rejecting a Closed merchant, separate from the status field itself).

Response: `{ "merchantId": "guid", "status": "Closed" }`. 404 on an unknown `id`.

### `POST /api/v1/ops/merchants/{id}/regenerate-key` — `ops.merchants.rotate-key`
No body. Revokes the current credential immediately and issues a new one.
```json
{ "apiKey": "...", "apiSecret": "one-time", "signingSecret": "one-time", "warning": "..." }
```

### `PUT /api/v1/ops/merchants/{id}/allowed-ips` — `ops.merchants.manage`
Request: `{ "ipAddresses": ["1.2.3.4", "5.6.7.8"] }` — full replace, not additive.

- **Single full addresses only.** IPv4 as four decimal octets, or IPv6. CIDR ranges, ports, host names and
  shorthand such as `1.2` are refused. Addresses are stored normalised: IPv6 in canonical lowercase form, and
  `::ffff:1.2.3.4` as `1.2.3.4`, so `allowedIps` may not echo exactly what was typed.
- Refused entries are dropped and listed in `invalidIps`; the valid ones are saved. **Show `invalidIps` to the
  operator** — a dropped entry is a server that will get 403. If *every* entry is refused it is a 400
  `ops.invalid_ip_address` and nothing changes.
- **Sending `[]` clears the list, which blocks the merchant's API.** The response carries `apiAccessBlocked: true`;
  confirm with the operator before sending it.

Response:
```json
{ "merchantId": "guid", "allowedIps": ["1.2.3.4"], "apiAccessBlocked": false, "invalidIps": [], "cloudflare": { "added": 1, "removed": 0 } }
```

---

## 9a. Merchant balance — manual credit/debit, and balance history

A staff-initiated correction to a merchant's ledger balance — **not** backed by a real on-chain
deposit/withdrawal (e.g. a support-ticket goodwill credit, or clawing back an over-credit). Gated on
`ops.balances.adjust`, which is deliberately **not** granted by `ops.merchants.manage` or bundled into any
other code — by default only the seeded Admin role (`"*"`) can call these two endpoints; a custom role needs
this exact code added explicitly via §6. Posts immediately — there is no second-approver/threshold step
today, regardless of amount, so treat this as a sensitive, rarely-used action in the UI (e.g. a confirmation
dialog), not a routine one. Every call is fully audit-logged (§8: `merchant.balance_credited` /
`merchant.balance_debited`, with the amount/asset/reason in `reason`).

Both endpoints share the same request body:
```json
{
  "chain": "Tron",
  "coin": "USDT",
  "amount": 50.0,
  "reason": "string, required, max 512 — shown in the audit log, make it meaningful",
  "adjustmentId": "guid, optional"
}
```
`amount` is a **display-unit decimal** (§5), like everywhere else except §9a's own balance field and §17.
`adjustmentId` is optional — omit it for a normal one-off action; supply a stable value (e.g. a support-ticket
id) if the client might retry the same logical request, so a retried call replays safely instead of posting
twice. There is no idempotency without it beyond the usual "don't double-click" UI care.

### `POST /api/v1/ops/merchants/{id}/balance/credit` — `ops.balances.adjust`
Adds `amount` to the merchant's balance. Response:
```json
{ "isSuccess": true, "data": { "merchantId": "guid", "assetId": "guid", "coin": "USDT", "network": "Tron", "outcome": "Posted" }, "error": null }
```
`outcome` is `"Posted"` normally, or `"AlreadyPosted"` if `adjustmentId` matches a prior call (safe replay,
nothing double-credited).

### `POST /api/v1/ops/merchants/{id}/balance/debit` — `ops.balances.adjust`
Subtracts `amount` from the merchant's balance. Same response shape as credit.
**409** if the merchant's current balance is smaller than `amount` — the merchant's real, spendable ledger
balance is the only thing that gates a debit (never the platform's own running credit/debit history), so
this is a straightforward "not enough funds" the UI should surface plainly, same as an insufficient-balance
withdrawal.

Both endpoints: 404 if the merchant `id` doesn't exist; 400 for an unrecognized `chain`/`coin` or an `amount`
that's non-positive or finer than the asset's decimal precision.

### `GET /api/v1/ops/merchants/{id}/balance/history` — `ops.merchants.view`

The merchant's full "account statement" — every event that actually credited or debited their balance,
newest first: real deposits, deposit reversals, withdrawal reserves/releases, and manual credits/debits from
the two endpoints above. This is a **read**, gated the same as the rest of the merchant details payload —
not `ops.balances.adjust`, which is reserved for the two money-moving actions above.

Query params (all optional, standard pagination §4): `chain` + `coin` (must be supplied **together** — same
rule as elsewhere in this API, `chain` alone or `coin` alone is a 400), `fromDate`, `toDate`.

Response `data` — same level as `page`/`pageSize`/`items`:
```json
{
  "merchantId": "guid",
  "page": 1, "pageSize": 50, "totalCount": 6,
  "items": [
    {
      "journalId": "guid",
      "type": "manual_debit",
      "referenceType": "Adjustment",
      "referenceId": "guid",
      "direction": "Debit",
      "amount": 1.0,
      "amountBaseUnits": "1000000",
      "balanceAfter": 34.5,
      "balanceAfterBaseUnits": "34500000",
      "assetId": "guid",
      "coin": "USDT",
      "network": "Tron",
      "reason": "Manual debit: testing",
      "createdAt": "2026-08-28T08:04:07.9960755+00:00"
    }
  ]
}
```

**`type`** is the field to build UI around — a friendly category derived from the raw `referenceType` +
`direction`:

| `type` | Meaning | `direction` |
|---|---|---|
| `deposit` | Real on-chain deposit credited | Credit (always) |
| `deposit_reversal` | A confirmed deposit got orphaned by a reorg and reversed | Debit (always) |
| `withdrawal_reserve` | Funds locked when a payout (user or merchant cash-out) was requested | Debit (always) |
| `withdrawal_release` | Reserved funds returned — that withdrawal was rejected/failed/cancelled | Credit (always) |
| `manual_credit` | Staff manual credit (§9a above) | Credit (always) |
| `manual_debit` | Staff manual debit (§9a above) | Debit (always) |
| `other` | Anything not in the six types above (not expected in normal operation) | either |

`amount` is always positive (display decimal, §5); use `direction` (or `type`) to know whether it added or
removed funds, e.g. render `direction == "Credit" ? "+" : "-"` in front of `amount`. `amountBaseUnits` is the
exact integer string, same "watch this one" exception as §17 and this section's own balance field.

**`balanceAfter`** is the merchant's running balance for that row's `assetId`, immediately after this entry
posted — reading top to bottom (newest first) shows the balance stepping backward through time. It's correct
on every page, not just the first, and each asset's running total is independent — if `chain`/`coin` are
omitted and the merchant holds more than one asset, rows for different assets are interleaved and each keeps
its own separate `balanceAfter` sequence rather than sharing one running total. `balanceAfterBaseUnits` is the
exact integer string, same rounding caveat as `amountBaseUnits`.

**Two things that trip people up:**
- **A successful withdrawal shows only ONE row here, not two.** The money actually leaves the merchant's
  balance at `withdrawal_reserve` time — settlement (the withdrawal actually confirming on-chain) does
  **not** create a second row, because settlement only relocates funds between two *platform-internal*
  accounts, never touching the merchant's balance again. So `withdrawal_reserve` with no matching
  `withdrawal_release` means the payout succeeded; `withdrawal_reserve` **followed by** a
  `withdrawal_release` (usually moments later) means it failed/was rejected and the funds came straight
  back — net effect zero, but both rows stay visible since this is a full history, not a running total.
- **`referenceId` is the underlying Deposit/Withdrawal/Adjustment id**, not a `PaymentIntent`/invoice id —
  useful for deep-linking to §12/§13, but don't expect it to match `systemOrderNumber` on those screens for
  every row (an intent-less deposit, for instance, has no `PaymentIntent` at all).

**Known gaps, not built today:** no running balance-after-this-entry column (would need a historical balance
snapshot per posting, not just the current cache); no way to see which staff member made a `manual_credit`/
`manual_debit` row directly here — cross-reference `audit.AuditEntry` via §8 for that; no `type` filter query
param (client-side filter on the returned page for now).

---

## 10. Merchant fees (per-merchant deposit/withdrawal pricing)

### `GET /api/v1/ops/merchants/{id}/fees` — `ops.fees.view`
```json
{
  "isSuccess": true,
  "data": {
    "merchantId": "guid",
    "fees": [
      {
        "assetId": "guid", "network": "Tron", "coin": "USDT",
        "depositFeeFixed": 0.5, "depositFeePercent": 1.0, "depositFeeMinimum": 0.5,
        "withdrawalFeeFixed": 1.0, "withdrawalFeePercent": 0.5, "withdrawalFeeMinimum": 2.0,
        "topUpFeeFixed": 0, "topUpFeePercent": 0,
        "minimumDeposit": 1.0, "maximumDeposit": null,
        "minimumWithdrawal": null, "maximumWithdrawal": null,
        "merchantWithdrawalCapFlat": null, "merchantWithdrawalCapPercent": 25
      }
    ]
  },
  "error": null
}
```
An unpriced merchant simply has an empty `fees` array — which means **zero fee**, not an error.
`minimumDeposit`/`maximumDeposit`/`minimumWithdrawal`/`maximumWithdrawal`/`merchantWithdrawalCapFlat`/
`merchantWithdrawalCapPercent` are read-only here (set via `PUT .../deposit-limits`, `PUT .../withdrawal-limits`,
and `PUT .../withdrawal-cap` respectively) — included for a single-screen view.

### `PUT /api/v1/ops/merchants/{id}/fees` — `ops.fees.manage`
Request:
```json
{
  "chain": "Tron", "coin": "USDT",
  "depositFeeFixed": 0.5, "depositFeePercent": 1.0, "depositFeeMinimum": 0.5,
  "withdrawalFeeFixed": 1.0, "withdrawalFeePercent": 0.5, "withdrawalFeeMinimum": 2.0,
  "topUpFeeFixed": 0, "topUpFeePercent": 0
}
```
All `*FeeFixed`/`*FeeMinimum` values are **display-unit decimals** (not base units), and a `0` is valid (pure
percentage pricing, or no minimum-fee floor). `*FeePercent` is a **plain percent** (`2` = 2%, not basis
points), at most 2 decimal places — a finer value is rejected with 400, never rounded. 400 if the chain/coin
is unrecognized, a percent is negative/>100%/too-precise, or a fixed/minimum amount is negative or has more
decimal precision than the asset supports.

**`*FeeMinimum` is the 最低手续费 floor:** the actual fee charged is `max(fixed + amount×percent, minimum)`
— it never drops below `*FeeMinimum` no matter how small the transaction, but it also never exceeds what a
deposit actually received (a deposit fee is capped at the deposit amount itself, so a tiny deposit against a
high minimum fee is entirely consumed as fee revenue rather than crediting a negative balance).

Response 200 includes a `warnings` array (soft, non-blocking): populated when a `*FeeMinimum` is at least
half of this merchant's own configured minimum transaction amount (set via the limits endpoints below) — a
sign that small transactions could be taxed disproportionately. The save still succeeds; surface the warning
to staff but don't block on it.

**Omitted fields mean "unchanged", not "zero".** Every fee field is nullable: send one to set it, omit it to
leave the stored value alone. Posting only `depositFeePercent` changes the deposit rate and leaves the
withdrawal and top-up schedules — and every `*FeeMinimum` — exactly as they were.

An explicit `0` still genuinely means zero (pure-percentage pricing, or no fee) — the two intents are
distinguishable on the wire, which they previously were not.

> **This closes a destructive bug.** These fields used to be non-nullable, so an omitted schedule bound the
> default `0`: posting only `depositFee*` silently zeroed the withdrawal and top-up fees, returned 200, and
> echoed back only the asset, so nothing in the response revealed the loss. A fee form sending two schedules
> of three wiped merchants' top-up pricing on every edit.

**The response returns the resulting pricing**, not an echo of the asset — so a client can see exactly what
it left behind rather than having to re-GET and hope:

```json
{ "isSuccess": true, "errorCode": null,
  "data": { "merchantId": "...", "assetId": "...", "coin": "USDT", "network": "Tron",
            "fees": { "depositFeeFixed": 0, "depositFeePercent": 2.5, "depositFeeMinimum": 1.0,
                      "withdrawalFeeFixed": 0, "withdrawalFeePercent": 0.5, "withdrawalFeeMinimum": 0,
                      "topUpFeeFixed": 0, "topUpFeePercent": 3 } },
  "warnings": [] }
```

### `PUT /api/v1/ops/merchants/{id}/deposit-limits` — `ops.fees.manage`
Mirrors `PUT .../withdrawal-limits` (§ below) for the **payin** side — new, previously no such concept
existed for deposits.

Request:
```json
{ "chain": "Tron", "coin": "USDT", "minimum": 1.0, "maximum": null }
```
`minimum`/`maximum` in **display units**; `null` on a bound = unset (minimum falls back to the platform's
per-chain dust-floor config, maximum stays unbounded — there is no platform-wide default maximum today); a
set value (including `0`) fully overrides. Enforced when a merchant requests a deposit invoice
(`POST /api/v1/deposit`) — a request outside the range is rejected **before** any payment address is issued,
since a crypto deposit can't be rejected after it's already arrived on-chain.

Response 200: `{ "merchantId": "guid", "assetId": "guid", "coin": "USDT", "network": "Tron" }`.

#### Fees are deducted from what arrives — not added on top

A 100 USDT deposit at 100 bps means the payer sends **100**, the merchant is credited **99**, and the
platform earns **1**. The invoice always shows exactly the amount requested. (An earlier version grossed
deposits up so the merchant netted its target — that behaviour was removed; do not describe fees to staff
as "charged to the payer on top".)

Because the fee is deducted, `depositFeeBps` accepts the full `0…10000` range — 10000 (100%) is legal,
and simply means the merchant is credited nothing. It is absurd, not impossible, so the platform is
allowed to set it.

#### `topUpFee*` — merchant top-ups are priced separately

A **merchant top-up** is the merchant funding its own balance on-chain (portal §8.4), not a customer
payment. It has its own schedule which **defaults to zero and never inherits `Merchant:DefaultFee`** — so
an unpriced merchant tops up free even while its customer deposits are charged the platform default.
Setting `topUpFeeBps` non-zero charges the merchant for adding its own funds; that is a deliberate
commercial choice, not the norm.

Top-up balance is also **exempt from the merchant's settlement period (T+N)** — it is spendable as soon as
it confirms, because a settlement delay exists to cover *customer* chargeback and reorg risk. Staff should
know that raising a merchant's settlement period does **not** slow its top-ups.

---

## 11. Wallets

Suspend/resume is a **temporary, reversible hold** — not a decommission. It only stops *future* deposits
from being recorded on that address (deposit detection checks wallet status at the moment a transfer is
first seen); anything already in flight before the suspend is unaffected and completes normally.

### `GET /api/v1/ops/wallets` — `ops.wallets.view`
Query filters (all optional, AND-combined): `merchantId` (guid), `address` (exact match string), `chain`
(e.g. `Tron`), `status` (`Active`|`Disabled`|`Suspended`), `walletType`
(`Deposit`|`HotWithdrawal`|`Treasury`|`Cold`|`Energy`, case-insensitive), plus `page`/`pageSize`.

`walletType` is applied in SQL across the **whole** result set, and `totalCount` reflects it — so filtering
to `HotWithdrawal` really does mean "every hot-pool wallet", not "the hot-pool wallets on the page you
happened to load". An unrecognised value is a **400**, never a silently unfiltered list.
Row:
```json
{
  "walletId": "guid", "merchantId": "guid|null", "merchantName": "string|null", "chain": 1, "address": "T...",
  "walletType": "Deposit", "status": "Active", "statusReason": "string|null",
  "depositsReceivedCount": 3, "createdAt": "...", "updatedAt": "..."
}
```
**Note:** `chain` here serializes as a **numeric enum value** (`1` = Tron), not a string — unlike almost
everywhere else in this API where chain is a string like `"Tron"`. Map it client-side (`1` = Tron; today
this is the only chain live). `statusReason` is only non-null while `status == "Suspended"`. `merchantName`
is `null` whenever `merchantId` is — platform wallets (hot pool, staking, treasury, etc.) aren't assigned to
a merchant; only deposit wallets are.

### `GET /api/v1/ops/wallets/{id}` — `ops.wallets.view`
The single-record detail view. Returns **one row object** in `data`, in the **exact same shape** as an item
in the list above (it is built by the same projection, so the table and the record it opens can never
disagree about a field). Unknown id → **404** `ops.not_found`.

### `POST /api/v1/ops/wallets/{id}/suspend` — `ops.wallets.manage`
Request: `{ "reason": "string, optional, max 512" }`
Response: `{ "walletId": "guid", "status": "Suspended" }`. 409 if the wallet isn't currently `Active`
(already suspended or disabled). 404 if the wallet doesn't exist.

### `POST /api/v1/ops/wallets/{id}/resume` — `ops.wallets.manage`
No body. Response: `{ "walletId": "guid", "status": "Active" }`. 409 if the wallet isn't currently
`Suspended`.

---

## 12. Payment intents (deposit invoices) — manual actions

### `POST /api/v1/ops/payment-intents/{reference}/fail` — `ops.deposits.manage`
Cancels a still-unpaid (`Waiting`) invoice — e.g. a test transaction. `{reference}` is the
`systemOrderNumber` shown on the deposit transaction screen.
Request: `{ "reason": "string, required, max 512" }`
Response: `{ "reference": "guid", "status": "failed" }`. 409 if the invoice already matched/expired
(nothing to cancel). 404 if not found.

**Note on mismatched deposits:** there is deliberately **no** "review/confirm mismatch" workflow. A
confirmed deposit always matches whichever invoice is currently waiting on its address, exact amount or
not. The transaction-search row (below) carries both `expectedAmount` and `receivedAmount` so staff (and
the merchant, via their callback) can see and reconcile any under/overpayment themselves — the platform
never blocks or holds a mismatched deposit for manual review.

---

## 13. Transaction search — deposits and withdrawals are separate screens

Deliberately two different endpoints, not one shared "type" filter — deposits and withdrawals surface
different fields (payer-side data only exists for a deposit) and forcing a shared shape would mean one side
always shows nulls the other populates.

### `GET /api/v1/ops/transactions/deposits` — `ops.deposits.view`

Query filters (all optional): `merchantId`, `merchantName` (free-text, case-insensitive "contains" match
against the merchant's Name or MerchantCode — no matches returns an empty page, not an error), `systemOrderNumber`
(guid), `merchantOrderNumber` (string), `receivingAddress`, `network` (chain), `coin` (requires `network`
to also be set), `status` (`pending`|`confirmed`|`expired`|`failed`), `fromDate`, `toDate`, `page`, `pageSize`.

`status` filters on the **effective** status — the same value the rows report, including the time-derived
part: a lapsed-but-not-yet-swept invoice is still `Waiting` in the database but already reads (and filters
as) `expired`. An unrecognised value is a **400**, never an unfiltered page.

Response `data` — same level as `page`/`pageSize`/`items`:
```json
{
  "page": 1, "pageSize": 50, "totalCount": 1234,
  "totalTransactionRecords": 1234,
  "totalDepositAmount": 50000.00,
  "totalActualDepositAmount": 49500.00,
  "totalFee": 495.00,
  "distinctAssetCount": 1,
  "items": [ /* rows below */ ]
}
```
`totalTransactionRecords` is the same number as `totalCount` (both count every matching record across
**every page**, not just this one — it's duplicated under both names deliberately). `totalDepositAmount`
sums every matching invoice's requested amount; `totalActualDepositAmount`/`totalFee` sum only the
invoices that have actually matched a deposit (unmatched invoices contribute `0` to those two). **All four
sums are computed across the whole filtered result set**, not the current page. `distinctAssetCount` is how
many different coins appear in the filtered set — if it's `> 1`, the amount sums above were added together
across assets with different decimal precision and are not a real combined quantity (e.g. USDT + a
hypothetical 18-decimal asset); only trust them at face value when the search is filtered to one `coin`, or
when this is `1`.

Row:
```json
{
  "merchantId": "guid",
  "merchantName": "string|null — null only if the merchant record can't be resolved",
  "systemOrderNumber": "guid",
  "merchantOrderNumber": "the merchant's own tx reference",
  "userId": null,
  "payerAddress": null,
  "receivingAddress": "T...",
  "network": "Tron",
  "coin": "USDT",
  "expectedAmount": 100.0,
  "receivedAmount": 98.0,
  "txHash": "0x... | null — null until a deposit has matched this invoice",
  "fee": 1.0,
  "confirms": 20,
  "type": "deposit",
  "kind": "Customer",
  "createdAt": "...",
  "status": "pending",
  "callback": "Pending",
  "callbackFailedCount": 0,
  "callbackNextAttemptAt": "..."
}
```
`status` ∈ `pending | confirmed | expired | failed` (lowercase).
`receivedAmount`/`fee`/`txHash`/`confirms` are all `null` until a deposit has actually matched the invoice.

`kind` ∈ `Customer | MerchantTopUp`. `type` is always `"deposit"` and cannot tell them apart, so use `kind`
when investigating a merchant's balance: a **top-up** is the merchant funding its own float (portal §8.4) —
priced on a separate zero-default schedule and **exempt from that merchant's T+N settlement hold**, so it
explains a withdrawable balance that a settlement period would otherwise appear to contradict.

(`userId` and `payerAddress` were removed — they were hardcoded `null` on every row. If real payer
attribution is wanted, it will be added deliberately as a populated field.)

### `GET /api/v1/ops/transactions/withdrawals` — `ops.withdrawals.view`

Same query filters as deposits (minus `coin` needing `network` — same rule applies here too), plus `kind`
(`"user"` | `"merchant"` — filters to end-user payouts or merchant cash-outs; omit for both).

`status` here takes the withdrawal vocabulary: `pending` | `pending_merchant_approval` | `pending_approval` |
`insufficient_balance` | `awaiting_release` | `confirmed` | `failed`. It filters on the same collapsed value
the rows report, applied in SQL across the whole set with `totalCount` reflecting it — this is what makes a
settlement queue trustworthy, since previously an operator could only see the outstanding work that happened
to land on the page they loaded. Note `pending` deliberately collapses the entire pre-confirm pipeline
(Reserving/Approved/Signing/Broadcast) and `failed` covers both rejected and failed payouts. An unrecognised
value is a **400** — an operator must never be handed an empty queue because they typed a status that does
not exist. `kind` and `status` AND together.

Response `data` — same level as `page`/`pageSize`/`items`:
```json
{
  "page": 1, "pageSize": 50, "totalCount": 567,
  "totalTransactionRecords": 567,
  "totalWithdrawalAmount": 30000.00,
  "totalFee": 300.00,
  "distinctAssetCount": 1,
  "items": [ /* rows below */ ]
}
```
Same rules as the deposit totals above: computed across the **whole filtered set**, not the current page;
`distinctAssetCount > 1` means the sums span more than one asset and are only meaningful at face value when
filtered to one `coin`.

Row:
```json
{
  "merchantId": "guid",
  "merchantName": "string|null — null only if the merchant record can't be resolved",
  "systemOrderNumber": "guid",
  "merchantOrderNumber": "the merchant's own tx reference",
  "userId": null,
  "receivingAddress": "T...",
  "network": "Tron",
  "coin": "USDT",
  "expectedAmount": 50.0,
  "fee": 0.5,
  "confirms": 20,
  "txHash": "0x... | null — null until broadcast",
  "sourceWalletId": "guid|null — which hot-pool wallet is/was leased for this payout; null until Signing",
  "type": "withdrawal",
  "kind": "User | Merchant — end-user payout vs. the merchant's own earnings cash-out",
  "createdAt": "...",
  "status": "pending",
  "callback": "Pending",
  "callbackFailedCount": 0,
  "callbackNextAttemptAt": "..."
}
```
`status` ∈ `pending | pending_merchant_approval | pending_approval | insufficient_balance | awaiting_release | confirmed | failed`
(lowercase). The states that need a human action, not just waiting:
- **`pending_approval`** → build an Approve/Reject action (§14 below). Plain `pending` needs no action —
  it's already approved and self-processing.
- **`insufficient_balance`** → the hot wallet can't physically cover it yet. Funds stay reserved (not
  lost). **Auto-resumes** once the wallet holds enough — no endpoint needed to un-stick it. An admin funds
  the wallet from a company wallet and records it as a top-up (§20). Use `sourceWalletId` to jump to the Wallets screen and check that wallet's real
  on-chain balance if investigating.
- **`awaiting_release`** → funded but above the auto-send threshold, needs an operator Release action
  (§15 below).
- **`pending_merchant_approval`** → a portal-initiated payout awaiting the MERCHANT's own approver, in their
  own portal. Platform staff can see it but must not action it — there is no Ops endpoint for this state. It
  leaves only when the merchant approves (then it either sends, or becomes `pending_approval` if it is above
  the threshold) or the merchant rejects it.

`sourceWalletId` is the direct cross-reference into §11 (Wallets) — if a payout is stuck, this tells you
exactly which pool wallet to go inspect and top up.

### `GET /api/v1/ops/transactions/withdrawals/{systemOrderNumber}` — `ops.withdrawals.view`
### `GET /api/v1/ops/transactions/deposits/{systemOrderNumber}` — `ops.deposits.view`

The single-record detail views. Each returns **one row object** in `data`, in the **exact same shape** as an
item in the corresponding list — same field names, same precision, same conversions, because both are built
by the same projection. Unknown id → **404** `ops.not_found`.

These exist so a detail page can be deep-linked and refreshed without reconstructing the row from query
params. There is no extra data on them: if the list has it, the detail has it, and vice versa.

**Note:** `userId` and `payerAddress` have been **removed** from the deposit and withdrawal rows. They were
always hardcoded `null` (never populated by any flow), so they were noise on every row. If real user
attribution or payer-address capture is wanted later, it will be added deliberately — as a populated field,
not a null placeholder.

---

## 14. Withdrawal approval (the `pending_approval` gate — above-threshold payouts)

### `POST /api/v1/ops/withdrawals/{withdrawalId}/approve` — `ops.withdrawals.approve`
No body. Response: `{ "withdrawalId": "guid", "status": "Approved" }`.

### `POST /api/v1/ops/withdrawals/{withdrawalId}/reject` — `ops.withdrawals.approve`
Request: `{ "reason": "string, required, max 512" }`
Response: `{ "withdrawalId": "guid", "status": "Rejected" }`. Releases the merchant's reserved funds and
fires the merchant's withdrawal-failed callback, same as an automatic failure.

Both 409 if the withdrawal isn't currently in `pending_approval`, 404 if not found.

**Approving also releases the payout for sending.** An explicit staff approval IS the release, so an approved
payout goes straight to signing — you will NOT be asked to release it again on the §15 screen. (That was a real
double-gate: both gates read the same threshold, so a large payout used to need two staff actions on two screens.)
`awaiting_release` therefore now appears almost exclusively as the resume step after an `insufficient_balance`
hold, not after a normal approval.

---

## 15. Withdrawal funding holds (the `insufficient_balance` / `awaiting_release` states)

### `POST /api/v1/ops/withdrawals/{withdrawalId}/release` — `ops.withdrawals.manage`
No body. Releases an `awaiting_release` payout for sending — in practice a payout that was parked for
insufficient hot-wallet float and now needs an explicit go-ahead. A payout staff already approved (§14) is
released by that approval and never reaches this state.
Response: `{ "withdrawalId": "guid", "status": "Released" }`.

### `POST /api/v1/ops/withdrawals/{withdrawalId}/cancel` — `ops.withdrawals.manage`
Request: `{ "reason": "string, required, max 512" }`
Cancels a payout that can't be funded — the only hold → `Failed` path, releases the reserve.
Response: `{ "withdrawalId": "guid", "status": "Cancelled" }`.

**Reminder:** `insufficient_balance` needs **no** endpoint at all to resume normally — fund the hot
wallet (and record the top-up, §20) and the background worker resumes it automatically. Only use `cancel` if you're actually giving up
on the payout.

---

## 16. Callback delivery — manual resend

Automatic delivery retries on a fixed backoff (30s, 1m, 2m, 4m, 10m — 6 attempts total), then the row goes
`Abandoned`. This is the human escape hatch.

### `POST /api/v1/ops/callbacks/{type}/{referenceId}/resend` — `ops.callbacks.manage`
`{type}` is literally `"deposit"` or `"withdrawal"`. `{referenceId}` is the same guid shown as
`systemOrderNumber` on the corresponding transaction-search row.
No body. Response: `{ "type": "deposit", "referenceId": "guid" }`. Resends the **exact already-signed
payload** — never re-signs, never re-builds. 400 if `type` isn't one of the two literals. 409/404 via the
same envelope if the reference doesn't exist or has nothing to resend.

`callback` status you'll see embedded on transaction rows: `PendingNotification | Notified | Abandoned`
(**`PendingNotification`**, not `Pending` — the API deliberately renames the domain enum at the edge)
(PascalCase — again, differs from the lowercase transaction-status vocab).

---

## 16b. Dashboard aggregates — the landing page

### `GET /api/v1/ops/dashboard` — any valid staff session, **no extra permission**

Deliberately ungated: it exposes nothing a staff member cannot already read, and putting the landing page
behind a permission would give a freshly-created account a blank screen with no explanation.

```json
{
  "generatedAt": "2026-08-26T08:03:10Z",
  "operational": {
    "withdrawalsPendingMerchantApproval": 0,
    "withdrawalsPendingApproval": 3,
    "withdrawalsAwaitingFunds": 1,
    "withdrawalsAwaitingRelease": 2,
    "settlementsPendingAudit": 2,
    "settlementsPendingFinanceTransfer": 1,
    "callbacksAbandoned": 13,
    "callbacksPending": 4,
    "walletsSuspended": 0,
    "reconciliationDriftCount": 2,
    "reconciliationIncompleteCount": 0,
    "energyWalletsCritical": 1,
    "energyWalletsLow": 3
  },
  "custody": [ /* per (chain, asset) — see below */ ],
  "custodyAvailable": true,
  "energyHealthAvailable": true,
  "volume": []
}
```

**`operational` is a work queue, and every key maps 1:1 onto a filter you can link to.** The six
withdrawal counts use the exact `status` vocabulary from §13, so a tile links straight to
`/transactions/withdrawals?status=pending_approval` and the number will match that list's `totalCount`.
`settlementsPendingAudit` and `settlementsPendingFinanceTransfer` link to `status=pending_admin_audit` and
`status=pending_finance_transfer` respectively — the two merchant-settlement queues that need a human (§19b).
That is enforced by test, not by convention — a tile saying 3 that opens a list of 5 is the specific bug
being guarded against.

**`custody`** mirrors `GET /ops/reconciliation` (same snapshots, same fields, per (chain, asset)):
`ledgerTreasuryHolding`, `onChainTotal`, `drift` as display decimals, plus `driftBaseUnits` as the exact
integer string and `decimals`. If you already call `/ops/reconciliation`, you can ignore this block.

### Degraded responses — read `custodyAvailable` / `energyHealthAvailable`

The custody and energy-health numbers come from MongoDB, which holds **derived observability data, never
money truth**. If Mongo is unreachable, this endpoint still returns **200** with the SQL-backed
`operational` counts intact — it will not fail the whole landing page and hide the operator's actual to-do
list over an observability outage.

In that case the four Mongo-derived counts are **`null`, not `0`**, `custody` is `[]`, and the two
`*Available` flags are `false`. **Render null as "unavailable", never as zero** — a fake `0` on a drift
figure reads as "all balanced", which is the most dangerous thing you could show on a custody tile.

### `volume` is not implemented yet

It returns `[]` always. The per-asset deposit/withdrawal counts and sums over a time window need a
purpose-built grouped SQL aggregate: the existing totals path folds BigInteger money client-side by design
(there is no SQL `SUM` translation for this project's money type), so a naive 30-day per-asset breakdown
would load every row in the window — a table scan on the landing page, which is exactly what REQ-3's own
acceptance criteria rule out. Build the tiles on `operational` (as REQ-3 suggested); charts follow later.

---

## 17. Ledger-wide transaction search (distinct from the deposit/withdrawal screens)

### `GET /api/v1/ops/transactions` — `ops.transactions.view`
This reads the raw double-entry journal, not the deposit/withdrawal domain views — use it for a
merchant's full accounting trail (deposits, withdrawals, fees, gas costs, everything), not as a
replacement for §13's screens.

Query filters: `merchantId`, `transactionId` (a merchant's own tx reference — **requires** `merchantId` to
also be set, 400 otherwise, and today only resolves deposit-side references, not withdrawal), `fromDate`,
`toDate`, `page`, `pageSize`.

Row:
```json
{
  "journalId": "guid", "referenceType": "string", "referenceId": "guid",
  "assetId": "guid", "coin": "USDT|null", "decimals": 6,
  "description": "string", "direction": "Debit|Credit",
  "amount": "base-unit integer string — NOT display decimal, unlike everywhere else in this API",
  "createdAt": "..."
}
```
**Watch this one:** `amount` here is a raw base-unit integer string (e.g. `"1000000"` for 1 USDT), not the
display decimal convention used everywhere else in this doc (§5). That is deliberate — the ledger is the
authoritative accounting record and never rounds. Each row now carries its own `coin` and `decimals` so you
can format it without a second lookup against the asset catalog.

`coin` and `decimals` are **null** on a gas-denominated journal (`referenceType: "GasCost"`): the gas asset
is deliberately kept out of the deposit catalog, so it has no symbol or published precision. Render those
rows as raw base units — do **not** fall back to a guessed 6.

---

## 18. Status vocabulary cheat-sheet (casing is inconsistent across resources — this is real, not a typo)

| Resource | Field | Values | Casing |
|---|---|---|---|
| Deposit (payment intent) | `status` | `pending`, `confirmed`, `expired`, `failed` | lowercase |
| Withdrawal (user payout) | `status` | `pending`, `pending_merchant_approval`, `pending_screening`, `pending_approval`, `insufficient_balance`, `awaiting_release`, `confirmed`, `failed` | lowercase-snake |
| Withdrawal (merchant settlement) | `status` | additionally `pending_admin_audit`, `pending_finance_transfer`, `finance_settled` — see §19b | lowercase-snake |
| Callback | `status` | `PendingNotification`, `Notified`, `Abandoned` | PascalCase — note the first is **`PendingNotification`**, NOT `Pending` |
| Wallet | `status` | `Active`, `Disabled`, `Suspended` | PascalCase |
| Staff account | `status` | `Active`, `Disabled` | PascalCase |
| Merchant | `status` | `Active`, `Frozen`, `Closed` | PascalCase |
| Address screening | `decision` | `Allow`, `Review`, `Block`, `Unavailable` | PascalCase |
| Address screening | `purpose` | `PayoutDestination`, `SettlementWallet`, `DepositAddress`, `DepositSource` | PascalCase |

`pending_screening` is **its own bucket and not part of `pending`**: it is a payout waiting on a third
party, which calls for different handling from one waiting on us. It is a valid value for the `status`
filter, like every other value in that row.

`Unavailable` is not a clean result and must never be rendered as one. It means no verdict could be
obtained, and the payout flow holds such a payout for staff by default.

---

## 19. Merchant terms — settlement period, wallet, caps, limits, threshold

Five setters on top of §10's fees. All are **display decimals** on the way in, converted at the edge, and
**over-precision is refused rather than truncated** (§5).

| Endpoint | Permission | Body |
|---|---|---|
| `PUT /ops/merchants/{id}/settlement-period` | `ops.merchants.manage` | `{ "days": 1 }` (0–30; 0 = T+0) |
| `PUT /ops/merchants/{id}/settlement-wallet` | `ops.merchants.manage` | `{ "chain": "Tron", "address": "T...", "label"?, "activate"?: true }` |
| `POST /ops/merchants/{id}/settlement-wallets` | `ops.merchants.manage` | same body; `activate` defaults to **false** — puts an address on file without pointing earnings at it |
| `POST /ops/merchants/{id}/settlement-wallets/{walletId}/activate` | `ops.merchants.manage` | — |
| `POST /ops/merchants/{id}/settlement-wallets/{walletId}/retire` | `ops.merchants.manage` | — |
| `PUT /ops/merchants/{id}/withdrawal-cap` | `ops.fees.manage` | `{ "chain", "coin", "flatCap": 5000.0, "percent": 50 }` (percent, not bps — see §"Percent vs basis points") |
| `PUT /ops/merchants/{id}/withdrawal-limits` | `ops.fees.manage` | `{ "chain", "coin", "minimum": 10.0, "maximum": 5000.0 }` |
| `PUT /ops/merchants/{id}/deposit-limits` | `ops.fees.manage` | `{ "chain", "coin", "minimum": 1.0, "maximum": null }` |
| `PUT /ops/merchants/{id}/approval-threshold` | `ops.fees.manage` | `{ "chain", "coin", "threshold": 1000.0 }` |
| `PUT /ops/merchants/{id}/profile` | `ops.merchants.manage` | `{ "contactEmail", "settlementMode", "remark" }` — all optional, write-only |
| `PUT /ops/merchants/{id}/payout-approval` | `ops.merchants.manage` | `{ "required": true }` |

### The settlement-wallet actions — response shape

All four settlement-wallet endpoints (`PUT .../settlement-wallet`, `POST .../settlement-wallets`,
`.../activate`, `.../retire`) return the same shape:

```json
{ "merchantId": "...", "walletId": "...", "network": "Tron", "address": "T...", "label": "backup desk",
  "status": "Active", "isActive": true,
  "screeningDecision": "Allow", "screeningScore": 3, "screeningId": "..." }
```

Plus a top-level `warnings` array alongside `data` (see below) on the two writes that can produce one
(`PUT`/`POST .../settlement-wallets`) — activate/retire never carry warnings, since re-screening on
activation only ever raises a hard refusal (`merchant.settlement_wallet_blocked`), never a warning.

A merchant may keep **several** addresses on file per chain; `status` is `"Active"` (this is where the
chain's cash-outs are paid) or `"Retired"` (on file, not in use) — read the per-merchant
`GET /ops/merchants/{id}` response's `settlementWallets[]` array for the full set with all their
statuses, rather than inferring it from individual write responses.

### `payout-approval` — two-party payout approval, per merchant

Turns on the merchant's **own** approval stage. With it on, that merchant's user payouts stop at
`pending_merchant_approval` for their approver before the platform evaluates them — **on both the HMAC API
and the merchant portal**, because this is merchant policy, not a property of the caller. It previously
depended on which host received the request, which meant a merchant integrating server-to-server could never
use the feature at all.

**Defaults to `false`**, so no existing integration changed behaviour. This is a §16 confirmation flow —
enabling it changes where that merchant's money stops:

- Turning it **on** for a merchant with no approver in place leaves payouts waiting indefinitely.
- Turning it **off** releases nothing already waiting — those still need approving or rejecting.

It can never weaken the platform gate: the approval threshold is re-resolved server-side when the merchant
approves, so a merchant cannot approve its way past a payout that needs staff review.

Read it back on `GET /ops/merchants/{id}` as `requiresPayoutApproval`.

**`null` vs `0` is a real distinction on every optional amount, not a formality:**

- `null` = **unset** ⇒ the platform config value applies (`Withdrawal:Policies`).
- `0` = an explicit zero — "no minimum", "cap cash-out at zero", "everything needs approval".

Sending `0` where you meant "clear it" changes behaviour. A limits form must be able to submit `null`.

**Read them back** on `GET /ops/merchants/{id}/fees` and `GET /ops/merchants/{id}`. None of these are
write-only any more, so **seed every form from the server** rather than warning the operator that the current
value cannot be shown:

| Value | Read from |
|---|---|
| `merchantWithdrawalFlatCap`, `merchantWithdrawalPercentBps` | `GET .../fees` |
| `minimumWithdrawal`, `maximumWithdrawal` | `GET .../fees` |
| `approvalThreshold` | `GET .../fees` |
| `settlementDelayDays`, `settlementWallets`, `requiresPayoutApproval` | `GET /ops/merchants/{id}` |

The fees read preserves `null` as `null` — it never coalesces an unset policy to `0`, so an empty form field
and a configured zero stay distinguishable, which is the whole reason the distinction above matters.

**The settlement wallet is staff-only, permanently.** A merchant cannot set its own cash-out destination from
the portal — that is the control preventing a compromised merchant credential from redirecting earnings (§10).

---

## 19b. Merchant settlements — the audit → finance → record workflow

**A merchant cash-out is not paid by this system.** An admin audits the request, a finance admin pays the
merchant from a **company wallet outside platform custody**, and the transaction is recorded here. User
payouts are unaffected — those are still built, signed and broadcast automatically.

A merchant cash-out therefore has its own three statuses, which appear on the withdrawal screens and are
filterable like any other:

| `status` | Meaning | Action |
|---|---|---|
| `pending_admin_audit` | Waiting for an admin to review it | approve / reject |
| `pending_finance_transfer` | Cleared; finance must pay it externally | record the payment |
| `finance_settled` | Paid and the hash verified on-chain | terminal |

`finance_settled` is deliberately distinct from `confirmed`: `confirmed` means *the platform paid it itself*,
`finance_settled` means *a human paid it and we verified their hash*. Different origins of trust — show them
differently.

| Endpoint | Permission |
|---|---|
| `POST /ops/withdrawals/{id}/audit-approve` | `ops.withdrawals.approve` |
| `POST /ops/withdrawals/{id}/audit-reject` — `{ "reason": "..." }` | `ops.withdrawals.approve` |
| `POST /ops/withdrawals/{id}/record-settlement` — `{ "transactionHash", "sourceAddress?" }` | `ops.withdrawals.manage` |

Recording is gated on `manage`, not `approve`, so signing a settlement off and declaring it paid can be
separate people.

**Rejecting at either stage returns the merchant's money** — the ledger reserve is released. A decline never
strands funds.

### The hash is verified before anything is written

`record-settlement` checks the chain first: the transaction must exist, be **confirmed**, and carry the right
destination, asset and at least the right amount. **On failure nothing is written** — the settlement stays in
`pending_finance_transfer`, and the operator fixes the hash and retries. Branch on these:

| `errorCode` | What the operator should do |
|---|---|
| `verification.tx_not_found` | Check the hash; or wait if just broadcast |
| `verification.tx_not_confirmed` | Wait for confirmation, then retry |
| `verification.tx_failed` | The transaction reverted; no funds moved |
| `verification.destination_mismatch` | It paid a different address |
| `verification.amount_mismatch` | It paid less than owed |
| `withdrawal.duplicate_settlement_hash` | That hash is already recorded against another settlement |

Surface these verbatim rather than as one generic "failed" — "still confirming" and "wrong hash" call for
completely different actions.

### Extra fields on the withdrawal rows

`GET /ops/transactions/withdrawals` (and the detail endpoint) carry the settlement audit trail. **All null for
a user payout**, which the platform pays itself:

| Field | Meaning |
|---|---|
| `auditedBy` / `auditedAt` | Who cleared it, and when (also set on an audit rejection) |
| `settledBy` / `settledAt` | Who recorded the external payment, and when |
| `settlementSourceAddress` | The company wallet that paid it |

For an off-system settlement this is the **only** audit trail there is — the chain cannot tell you who acted,
because the source wallet is not ours.

---

## 20. Treasury — cold wallet, hot pool, and top-ups

All `ops.treasury.manage`. How money moves between the platform's wallets:

- **Deposit addresses → cold treasury.** Sweeps concentrate deposits into the **cold** treasury, a watch-only
  address whose key the system never holds.
- **Hot withdrawal pool → the merchant user's destination.** User payouts are built, signed and sent automatically.
- **Company wallet → hot pool.** When a pool wallet runs low, finance sends company funds into it **outside this
  system** and an admin records the transfer here as a top-up.

Cold treasury funds are **not** moved to the hot pool by this system. The in-system cold reload
(`POST /ops/treasury/reload` and `/reload/{id}/submit`) was removed on 2026-09-17; those routes now return 404.

| Endpoint | Purpose |
|---|---|
| `GET /ops/treasury/hot-pool?chain=Tron` | Pool wallets **with live on-chain balances**, to see which needs topping up. **Never returns a key reference.** |
| `GET /ops/treasury/cold-wallets` | Every registered cold collection wallet, plus each chain's two current destinations (below). |
| `POST /ops/treasury/cold-wallets` | Register a cold collection address (below). `POST /ops/treasury/cold-wallet`, singular, is the same handler and still works. |
| `POST /ops/treasury/cold-wallets/{walletId}/activate` | Point that chain's sweeps of this wallet's kind at it, retiring the one it replaces. |
| `POST /ops/treasury/cold-wallets/{walletId}/retire` | Take a wallet out of use. Refused for the active one — 409 `treasury.cold_wallet.cannot_retire_active`. |
| `POST /ops/treasury/cold-wallets/{walletId}/re-screen` | Screen the address again, ignoring the cache, and refresh the verdict on the row. **Spends provider quota**, so keep it a button, not a page load. |
| `POST /ops/treasury/top-up` | Record a top-up that has already happened (below). |

### Cold collection wallets — several per chain, Safe and Danger

A chain has **two** destinations, not one: sweeps from a deposit address that screened clean go to the
**Safe** collection wallet, and sweeps from a flagged one go to the **Danger** (quarantine) wallet, so tainted
inflow never mixes into clean treasury. `kind` is that routing class — it is *not* a verdict about the wallet.
The Danger wallet is expected to score badly over time, by design, and nothing in the platform refuses to use
it for that reason.

Several wallets may be registered per (chain, kind); exactly one is `Active`. Replacing a destination is an
**add-then-activate**, never an edit of an address: the retired address usually still holds funds and stays in
the custody audit, so rewriting it in place would show up as a shortfall in reconciliation.

`GET /ops/treasury/cold-wallets` returns:

```json
{ "chains": [ { "chain": "Tron",
                "safe":   { "walletId": "...", "address": "T...", "label": "Cold A" },
                "danger": null } ],
  "wallets": [ { "walletId": "...", "chain": "Tron", "kind": "Safe", "address": "T...", "label": "Cold A",
                 "status": "Active", "isActive": true,
                 "coin": "USDT", "balance": 12500.0, "balanceBaseUnits": "12500000000",
                 "screeningDecision": "Allow", "screeningScore": 3, "screeningId": "...",
                 "screenedAt": "...", "createdAt": "...", "updatedAt": "..." } ] }
```

- `chains[].safe = null` ⇒ that chain is **not sweeping at all**; `danger = null` ⇒ it cannot sweep anything
  flagged (those balances stay on the deposit address). Both are worth saying on the screen.
- `balance` is `null`, never `0`, when the node could not be read — "unknown" and "empty" call for opposite
  actions. `coin`/`balance` are null when no USDT asset is configured for the chain.
- Retired wallets are listed, with balances: they usually still hold funds.

`POST /ops/treasury/cold-wallets` takes
`{ "chain", "address", "kind"?: "Safe"|"Danger", "label"?, "activate"?: false, "reason"? }` and returns
`{ walletId, chain, kind, address, label, status, isActive, replacedAddress, screeningDecision,
screeningScore, screeningId, warnings, registered }`.

- **`activate` defaults to `false`.** Adding an address and pointing a chain's sweeps at it are separate
  decisions; a UI should make activation its own confirmed step.
- The address is **format-checked for the chain** — a bad one is 400 `treasury.cold_wallet.invalid_address`,
  before any row exists. An empty one is 400 `treasury.cold_wallet.address_required`. The same address under
  the other `kind` is 409 `treasury.cold_wallet.already_registered`; re-registering it under the same kind
  adopts the existing wallet rather than duplicating it.
- **Screening never refuses a registration.** A non-clean verdict is returned in `warnings` (empty in the
  normal case, so don't read a 200 as silence) and recorded on the row. A flagged **Danger** wallet produces
  no warning at all — that is the control working.
- Audited as `treasury.cold_wallet_registered` / `_replaced` / `_activated` / `_retired` (entity
  `TreasuryColdWallet`, id = the wallet id) with the reason and both addresses. `reason` max 200 chars.

### `POST /ops/treasury/top-up` — recording a hot-wallet top-up

```json
{ "chain": "Tron", "targetWalletId": "...", "amount": 5000.00,
  "transactionHash": "0x...", "sourceAddress": "T..." }
```

A top-up already happened: finance sent company funds into a hot wallet from a company wallet, and an admin is
telling us about it. So it is verified on-chain and recorded, never executed.

`GET /ops/treasury/hot-pool` returns each wallet's live `available` balance so the operator can see which one
is running dry. A balance that could not be read comes back **`null`, not `0`** — "unknown" and "empty" call
for opposite actions.

Same verification codes as §19b. Two things worth surfacing in the UI:

- **The booked amount is what the chain shows, not what was typed.** If more was sent than entered, the
  response's `amount` reflects what actually arrived — custody must match reality.
- A top-up **never touches merchant balances.** It raises platform custody and is recorded against a company
  contribution account, so it can never be mistaken for merchant earnings.

---

## 20b. Settlement activity — company funds across the custody boundary

`GET /api/v1/ops/settlement-activity?chain=&page=&pageSize=` — `ops.treasury.manage`

One chronological feed of every movement of **company** money: top-ups in, merchant settlements out. Row:
`type` (`top_up` | `merchant_settlement`), `direction` (`in` | `out`), `recordedAt`, `recordedBy`, `network`,
`amount(+amountBaseUnits)`, `txHash`, `sourceAddress`, `destinationAddress`, `merchantId`.

The `summary` block carries running totals **from the ledger, not the page**: `floatContributed` and
`settlementsPaidExternally`.

> **Do not add either figure to earnings.** Earnings are **fee revenue** only. `settlementsPaidExternally` is
> money going *out*; counting it as income would invert its sign. The two belong in separate blocks —
> "earned" and "company funds deployed".

---

## 21. Reconciliation — custody audit

`GET /api/v1/ops/reconciliation?chain=Tron` — any valid staff session, no extra permission (it moves nothing).

Latest snapshot per (chain, asset): `chain`, `assetId`, `coin`, `status`, `ledgerHolding`, `onChainTotal`,
`drift`, **`driftBaseUnits`** (exact signed integer string), `addressesScanned`, `addressesUnreadable`,
`observedAt`. **Non-`Balanced` rows are returned first.**

### Where the custody sits

Each row also breaks the on-chain total down by the role of the address holding it. These **sum to
`onChainTotal`** — they come from the same balance reads, so the parts cannot disagree with the whole:

| Field | Meaning |
|---|---|
| `coldTreasuryTotal` | Swept merchant deposits — the bulk of custody |
| `hotPoolTotal` | Operating float for user payouts |
| `depositAddressTotal` | Received but not yet swept |
| `toppedUpTotal` | Of the above, how much is **company float** rather than merchant money |

`toppedUpTotal` comes from the *ledger*, not the chain — it is an accounting fact about where funds came from,
which no address balance can show. Render it as a qualifier on custody, not as an extra amount to add.

**A positive drift is worth checking against `toppedUpTotal` first**: an admin who sent a top-up but has not
yet recorded it leaves the chain ahead of the ledger by that amount. The API deliberately does **not** label
that "explained" automatically — it cannot distinguish an unrecorded top-up from genuinely unexplained funds,
and a custody screen must not claim more certainty than it has.

Merchant settlements never appear here and never move the drift: they are paid from wallets outside custody,
so no watched address is debited.

| `status` | Meaning |
|---|---|
| `Balanced` | Drift within the configured tolerance |
| `Drift` | Ledger and chain disagree beyond tolerance — surface loudly |
| `Incomplete` | An address could not be read, so the total is partial — **not** a clean bill of health |

Render `Incomplete` distinctly from `Balanced`. Collapsing them tells an operator custody is fine when the
system does not actually know.

Unlike the dashboard's custody tile, **this endpoint fails loudly if its store is unavailable** — here, "the
audit is down" *is* the answer to the question being asked.

> In dev with the in-memory chain the balance reader returns zero, so every asset reports the full ledger
> holding as drift. Expected, not a custody problem.

---

## 22. Sweep and Energy — operational views, and the sweep dials

Energy is read-only. Sweep now has two write actions (`ops.sweep.manage`): re-tune a chain's dials, and ask
for an out-of-schedule pass. There is still no per-sweep action (no manual retry/cancel of one sweep).

### `GET /api/v1/ops/sweeps` — `ops.sweep.view`
Filters: `chain`, `status`, `walletId`, `page`, `pageSize`. Returns `{ page, pageSize, totalCount, summary, items }`.

Row: `sweepId`, `walletId`, `chain`, `assetId`, `coin`, `decimals`, `fromAddress`, `toAddress`,
`amount(+amountBaseUnits)`, `status`, **`destinationKind`** (`Safe`/`Danger`), **`screeningDecision`**,
**`screeningId`**, `txHash`, `confirmations`, `failureReason`, `createdAt`, `updatedAt`.

`destinationKind: "Danger"` means the source deposit address was flagged and its balance was sent to the
quarantine wallet instead of clean treasury; `screeningId` deep-links to
`GET /ops/compliance/screenings/{id}` for the indicators behind that. Both are null/`Safe` on sweeps made
before segregation existed, and whenever `Sweep:Screening` is off.

### `GET /api/v1/ops/sweeps/settings` — `ops.sweep.view`
Also served at the original path `GET /api/v1/ops/sweeps/policies` (same payload; kept for screens already
built against it).

One row per chain: `chain`, `configured`, `enabled`, `minSweepAmountBaseUnits`, `confirmations`,
`scanIntervalMinutes`, `source`, `scanRequestedAt`, `lastScanStartedAt`, `lastScanCompletedAt`,
`lastSweepsCreated`, `updatedBy`, `updatedAt`, and `assets[]` of `{ coin, decimals, minSweepAmount }` — the
single base-unit threshold converted for each active asset, because the scan applies the same integer to every
asset on the chain.

- `source` is `"Configuration"` while the values still track the deployed defaults, `"Stored"` once staff have
  saved them. Show the difference: "nobody has set this" and "someone set it to exactly the default" look
  identical otherwise.
- A chain with no configured policy is `configured: false` with null figures — never a default.
- `lastScanCompletedAt` + `lastSweepsCreated` are how a screen answers "did the sweep actually run".

### `PUT /api/v1/ops/sweeps/settings/{chain}` — `ops.sweep.manage`

```json
{ "enabled": true, "minSweepAmountBaseUnits": "10000000", "confirmations": 19, "scanIntervalMinutes": 2 }
```

Every field is required — a partial save would silently revert a dial the caller happened to hold a stale
value for. `minSweepAmountBaseUnits` is an **exact base-unit integer string** (§14), not a display decimal:
the threshold applies to every active asset on the chain, so there is no single asset to convert against.

Validation: `sweep.threshold_negative`, `sweep.confirmations_not_positive` (zero would treat an unconfirmed
transfer as final), `sweep.scan_interval_out_of_range` (1 minute – 7 days; pause the chain instead of
scheduling it further out), `ops.invalid_amount` for a non-integer threshold, `sweep.chain_not_configured`
for a chain with no `Sweep:Policies` section. Audited as `sweep.settings_updated`.

`enabled: false` pauses concentration for the chain: balances stay on deposit addresses, which the platform
also controls, so nothing is at risk while it is off.

### `POST /api/v1/ops/sweeps/scan/{chain}` — `ops.sweep.manage`

Asks for a pass outside the schedule. Returns `{ chain, requested: true, scanRequestedAt, scanIntervalMinutes }`.

**It is a request, not a scan.** The back-office host runs no sweep workers and holds no chain credentials, so
it stamps the chain's settings row and the money host claims it on its next tick (within ~30s), consuming the
request exactly once however many instances are running. A UI should say "requested" and let the operator
watch `lastScanCompletedAt` / the sweep list, rather than implying the pass has finished. Refused for a paused
chain with 409 `sweep.chain_paused`. Audited as `sweep.scan_requested`.

### `GET /api/v1/ops/energy/operations` — `ops.energy.view`
Filters: `chain`, `kind` (`Stake`/`Delegate`/`TopUp`), `status`, `stakingWalletId`, paging.

Row: `operationId`, `kind`, `chain`, `stakingWalletId`, `ownerAddress`, `targetAddress`,
`amountTrx(+amountSunBaseUnits)`, `status`, `txHash`, `confirmations`, `failureReason`, timestamps.

### `GET /api/v1/ops/energy/resources` — `ops.energy.view`
Per-wallet resource health, **worst-health first**: `walletId`, `chain`, `address`, `walletType`,
`health` (`Healthy`/`Low`/`Critical`), `energyAvailable`/`energyLimit`/`energyUsed`, `bandwidthAvailable`,
`delegatedEnergyOut`/`In`, `frozenTrxForEnergy`, `frozenTrxForBandwidth`, `availableTrxBalance` (each TRX
figure with a `...Sun` exact integer), `targetEnergy`, `minimumEnergy`, `observedAt`.

Energy counts are raw integers, not money — no decimals conversion applies to them. TRX amounts are money and
carry both forms.

---

## 22b. Address screening — the AML evidence trail

Read-only, plus one action. Everything here is an opinion about a public address; no money and no keys are
involved.

```
GET  /api/v1/ops/compliance/screenings                 ops.compliance.view
GET  /api/v1/ops/compliance/screenings/{screeningId}   ops.compliance.view
POST /api/v1/ops/compliance/screenings/re-screen       ops.compliance.manage
```

**List filters** (all optional, they AND together): `chain`, `decision`, `purpose`, `address`, `fromDate`,
`toDate`, `page`, `pageSize` (max 200). An unknown `decision`, `chain` or `purpose` is a 400 with
`ops.invalid_decision`, `ops.invalid_chain` or `ops.invalid_purpose`. The `address` filter is an exact
case-insensitive match, not a substring search, so paste a whole address.

`decision` is one of `Allow`, `Review`, `Block`, `Unavailable`. `purpose` is one of
`PayoutDestination`, `SettlementWallet`, `DepositSource`.

**This list is the history, not the current state.** `items` and `totalCount` are rows, including superseded
verdicts, and `decision` matches any row. An address blocked last month and cleared since still appears
under `decision=Block` here, through its old row. Its `summary` counts each address once at its latest
verdict but honours only `chain`, so it will not agree with a filtered table. Use this list for an
address's audit trail. **For anything that asks "what is flagged now", use the current-verdict list below.**

**A row can read `Allow` and still list `sanctioned_entity` in `reasons`.** That is correct and must not be
rendered as a contradiction. `reasons` includes indirect exposure inherited through counterparties, which is
evidence a reviewer wants to see; only a direct designation forces a Block. Show the reasons, do not infer a
decision from them.

**`rawResponse` is only on the detail read**, and only there. It is the provider's verbatim payload, returned
as a string. Render it as preformatted text; do not assume a shape.

`freshUntil` is when the cached verdict stops being reusable, and is `null` for an `Unavailable` row, which
is never reused. `failureReason` is populated only for `Unavailable`. `policy` is the thresholds that were
in force when the decision was taken, so an old row explains itself without reference to current settings.

**Re-screen** takes `{ "chain": "Tron", "address": "T...", "purpose": "PayoutDestination" }`; `purpose`
defaults to `PayoutDestination`. It ignores the cache and appends a new row. It spends provider quota, which
is why it is a separate permission — treat it as a deliberate action with a confirmation, not a refresh
button.

A provider outage comes back as **200** with `decision: "Unavailable"`, never a 5xx. Surface it as "could
not determine", not as a failed request.

### Current verdicts — build any work queue here

```
GET  /api/v1/ops/compliance/addresses             ops.compliance.view
POST /api/v1/ops/compliance/screenings/latest     ops.compliance.view
```

Both return rows in the same shape as the history list, without `rawResponse` (open the detail route for
that). Both read stored evidence only, so neither spends provider quota and a vendor outage cannot slow
either down.

**What "latest" means, everywhere:** newest `screenedAt`, then the row written last. The payout gate, the
counters, the current list and the batch lookup all use this one rule, so they cannot pick different verdicts
for the same address.

#### `GET /compliance/addresses` — one row per address, at its current verdict

Query parameters, all optional, AND-ed together: `chain`, `decision`, `purpose`, `stale`, `page`, `pageSize`
(max 200). Newest `screenedAt` first. Response: `page`, `pageSize`, `totalCount`, `summary`, `items`.

| Rule | What it means for the UI |
|---|---|
| `decision` matches the **latest** row only | A cleared address leaves the Block queue the moment it is re-screened clean. |
| `purpose` picks **which addresses** appear | An address is included if it was *ever* screened for that purpose. The verdict is still its latest row, so a row's own `purpose` can differ from the filter — a deposit address re-screened from the payout screen still shows in the deposit queue. Do not hide those rows. |
| `stale=true` | The verdict has expired, or the last screening failed (`freshUntil` null). These are due for a re-screen. `stale=false` is the opposite. |
| `totalCount` counts **addresses** | Page on it directly. |
| `summary` uses every filter **except** `decision` | The counters describe the same addresses the table is drawn from, so they stay put when the user switches decision tabs. All four keys are always present. |

An unparseable `stale` returns 400 `ops.malformed_request`. An unknown or numeric `decision`/`purpose`
returns 400 `ops.invalid_decision` / `ops.invalid_purpose`.

#### `POST /compliance/screenings/latest` — the verdict for a page of addresses

Use this for a verdict column on a wallet list: **one call per page**, not one request per row.

```json
// request
{ "chain": "Tron", "addresses": ["TEuL4m31…", "TWd4WrZ9…"] }

// response data
{ "items": [
  { "address": "TEuL4m31…", "screening": { "screeningId": "…", "decision": "Allow", "score": 3 } },
  { "address": "TWd4WrZ9…", "screening": null }
] }
```

- **One entry per address sent, in request order, echoed exactly as sent.** Key your lookup on the string you
  sent. Exact duplicates collapse to one entry; different casings do not, because TRON addresses are
  case-sensitive Base58.
- **`screening: null` means never screened, and nothing else.** `decision: "Unavailable"` is a screening that
  happened and could not reach the provider. A failed *request* is a third state — keep all three distinct in
  the cell: verdict, never screened, lookup failed.
- **At most 200 addresses.** 201 returns 400 `ops.too_many_addresses`; nothing is ever silently truncated.
- `[]` returns `items: []`. A missing `addresses` field returns 400 `ops.address_required`, so a misspelt field
  name cannot come back as a successful answer about nothing. A missing or unknown `chain` returns 400
  `ops.invalid_chain`. One chain per request.
- It is a **POST only because a page of addresses does not fit in a query string.** Nothing changes state, but
  under cookie auth it needs `X-CSRF-Token` like any other POST.

### Where screening surfaces elsewhere, and how to link it

Screening verdicts appear on two other screens. Both carry a `screeningId`, so **link it to the detail
route above** rather than repeating the indicators inline — one place renders the evidence, and the two can
never disagree.

| Screen | Fields | What a click should open |
|---|---|---|
| Withdrawal search and approval queue | `screeningDecision`, `screeningScore`, `screeningId` | `GET /ops/compliance/screenings/{screeningId}` |
| Settlement-wallet save / activate | `screeningDecision`, `screeningScore`, `screeningId`, `warnings` | the same detail route |
| Cold collection wallets (`GET /ops/treasury/cold-wallets`) | `screeningDecision`, `screeningScore`, `screeningId`, `screenedAt` | the same detail route |
| Sweep rows | `destinationKind`, `screeningDecision`, `screeningId` | the same detail route |

The withdrawal queue uses `pending_screening` as its own effective status, **not folded into `pending`**.
A payout waiting on a third party and one waiting on us need different handling, so give them different
treatment in the UI.

On the settlement-wallet save, `warnings` is empty in the normal case — a clean address, or screening
switched off. A non-empty list means the wallet **was saved** and screening had something to say. Do not
read a 200 as silence; render the warnings. `screeningDecision: null` means not screened at all, which is
deliberately different from `"Unavailable"`, meaning asked and no answer.

**Whitelisting is never refused; activation can be.** A merchant may keep several settlement addresses on
file per chain, and one of them is `Active` — the address that chain's cash-outs are actually paid to. An
address the provider **directly designates** (`Block`) is still saved, with its verdict, but cannot be made
active: `400 merchant.settlement_wallet_blocked`, and the wallet is on file as `Retired` so the decision is
recorded and re-screenable rather than lost. A `Review` or `Unavailable` verdict is accepted with a warning —
a staff member is exercising judgement here and may hold context the provider does not. Activation
re-screens (a still-fresh verdict costs no provider call), because that is the moment the address starts
receiving earnings. Retiring the **active** wallet is refused with `409 merchant.settlement_wallet_active`:
activate a replacement instead, which retires it as part of the same change and leaves no gap.

Cold **collection** wallets take the opposite stance on purpose, and §20 explains why: their verdict never
refuses anything, because the quarantine destination is expected to score badly.

### What was exercised on a booted host (2026-09-18)

The cold-collection, sweep-settings and settlement-wallet routes were driven over HTTP against a running Ops
host, not checked off against source. Registering a cold Danger wallet returned its screening verdict
(`Allow`, score 3) with `status: "Retired"` because `activate` defaults to false; a malformed address was
refused 400 `treasury.cold_wallet.invalid_address`; retiring the active Safe wallet was refused 409
`treasury.cold_wallet.cannot_retire_active`; re-screening appended a NEW `screeningId`, proving it bypassed
the cache. `PUT /ops/sweeps/settings/Tron` flipped `source` to `"Stored"` with `updatedBy: "admin"`, zero
confirmations and a one-year interval were refused with their own codes, and `POST /ops/sweeps/scan/Tron`
was **picked up by the money host on its next tick** (the request marker cleared, `lastScanStartedAt` set) —
the cross-host trigger working end to end. Requesting a scan on a paused chain was refused 409
`sweep.chain_paused`. Swapping a merchant's active settlement wallet six times in a row returned 200 every
time; before the ordering fix (retire-and-save, then activate, in one transaction) it returned 500 on some attempts and not others.

### Screening thresholds — the settings screen

```
GET /api/v1/ops/compliance/policy           ops.compliance.view
GET /api/v1/ops/compliance/policy/history   ops.compliance.view
PUT /api/v1/ops/compliance/policy           ops.compliance.manage
```

`GET` returns `current`, `configuredDefaults` and `notEditableHere`.

**Render `source` prominently.** `Configuration` means nobody has ever saved a policy and the deployed
defaults apply. `Stored` means someone saved one. Those look identical field-by-field when a saved policy
happens to match the defaults, and only one of them is a question worth asking.

**Show `configuredDefaults` beside `current`**, so an operator can see what they changed and what it would
revert to.

**`notEditableHere` is not an error — render it.** The master switches (whether screening runs at all,
whether payouts are gated) live in configuration on purpose and take a deployment to change. The payload
names the keys and the reason. Showing them as disabled with that explanation is far better than leaving an
operator hunting for a switch that is not on the screen.

**`PUT` is a full replace, not a patch.** Send every field. A partial update on a policy screen invites
changing one threshold while silently reverting another to whatever stale value the page was holding.

| Field | Notes |
|---|---|
| `blockScore`, `reviewScore` | 1-100. Review must not exceed block. |
| `cacheDays` | 1-365. |
| `indirectReviewMaxHops` | 0-10. **Zero disables the proximity rule** — `proximityRuleEnabled` says so, do not make the UI infer it from the number. |
| `indirectReviewMinPercent` | 0-100, a share of volume. |
| `addedIndicators` | Designations to add. **Add-only.** |
| `note` | Optional, but it is what makes the history readable. |

**Designations are add-only.** `alwaysBlockIndicators` is everything in force; `editableIndicators` is the
subset staff added and may remove. Anything in the first list but not the second ships with the platform and
cannot be removed here — render those as fixed, not as removable chips that fail on save.

**Do not send `updatedBy`.** It is taken from the session. A compliance change attributed to whatever the
caller typed is not an attribution.

Validation failures return 400 with a specific `errorCode`: `compliance.invalid_score`,
`compliance.review_above_block`, `compliance.invalid_cache_days`, `compliance.invalid_hops`,
`compliance.invalid_percent`. Branch on the code, never the message. A refused update saves nothing.

**Changes are not instant across the system.** The workers that screen payouts run in a different host and
pick up a new policy within about 30 seconds. Do not promise immediate effect in the UI copy.

`GET .../history` returns past versions newest first, with who changed what and when. Screening thresholds
are append-only, so nothing is ever edited away.

### Screening our own deposit addresses (the inbound check)

Not to be confused with §22's Sweep — this is Compliance screening a deposit address for its risk score,
unrelated to Sweep moving funds off it.

```
POST /api/v1/ops/compliance/deposit-addresses/screen    ops.compliance.manage
```

No body. Returns:

```json
{ "candidates": 120, "screened": 100, "flagged": 2, "deferred": 20 }
```

**What it is.** An inbound transfer cannot be screened in flight, and an arrived deposit is never refused —
it is credited, and a frozen merchant's deposits still credit the ledger. So the check runs on the other side
of the graph: our own receiving addresses, whose score rises when funds arrive from a bad counterparty.

**It records and flags. Nothing is reversed, withheld or disabled.** By the time an address looks bad the
money has reached a merchant's balance. Present a flag as something to investigate, never as a blocked or
held state, and do not offer an action that implies the deposit can be undone.

**`deferred` is not an error.** A pass is capped so it cannot exhaust the quota the payout screening depends
on. A non-zero `deferred` means the rest are picked up next pass. Show it as progress, not failure.

**Treat the button as spending money.** Each screened address is a provider call against a metered daily
allowance. Confirm before running, and do not poll it or wire it to a page load.

Results appear in the screening list under `purpose=DepositAddress`, which is distinct from `DepositSource`
(a counterparty, not built) and from the payout and settlement purposes.

### Settlement wallets — the array on the merchant detail read

`GET /ops/merchants/{id}` returns `settlementWallets` as an ARRAY (a merchant may have several per chain,
§19), each entry:

```json
{
  "chain": "Tron",
  "address": "TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH",
  "walletId": "01a08a50-1727-72ff-8be2-c9b13e48e7f7",
  "label": null,
  "status": "Active",
  "screeningDecision": "Allow",
  "screeningScore": 0,
  "screeningId": "01a08eb1-b302-72ea-8e02-6de0eea3b99d",
  "screenedAt": "2026-09-11T04:20:05.5044201+00:00"
}
```

`walletId` is what the three §19 actions (`activate`/`retire`/re-add) address — read it from here rather
than from a write response if you need to act on a wallet you did not just create. `status` is
`"Active"` (this is where the chain's cash-outs are paid — **exactly one per chain**) or `"Retired"`
(on file, not in use). `label` is the operator's own name for the address, for telling two whitelisted
addresses apart without comparing base58 strings; null unless staff set one.

The four screening fields (`screeningDecision`, `screeningScore`, `screeningId`, `screenedAt`) are all
null when the address has never been screened, which is **not** the same as a decision of `Unavailable`.
Render "not screened", never a tick.

**Show `screenedAt`, not just the decision.** A verdict is a snapshot. An Allow from eight months ago on the
address that receives all of a merchant's earnings is worth surfacing differently from one taken this week.
A background pass re-screens ACTIVE wallets periodically (retired ones are not re-screened — nothing is
paid to them), so the date is meaningful.

`screeningId` deep-links to `GET /ops/compliance/screenings/{id}` for the indicators and the provider
payload. The list endpoint for merchants does **not** carry `settlementWallets` at all — only the
single-merchant read does, because the list query does not load settlement wallets.

**A flagged wallet is never revoked automatically.** If you show a warning here, it is information for the
operator, not a state change: the wallet is still whitelisted and cash-outs still route to it. Every merchant
cash-out already stops at `pending_admin_audit` for a human, which is where the decision belongs.

### Capacity, so the UI does not design against the wrong limit

The plan allows 10,000 calls a day at one per second, and a completed verdict is cached for 30 days. A
re-screen is therefore cheap but not free, and a bulk re-screen button is not something to add casually.
Screen one address at a time from a record the operator is already looking at.

---


---

## 23. Local development data

An empty database makes every screen look broken in the same way, so there is a seeder that produces a
realistic portfolio: several merchants with different pricing, settlement periods and lifecycle states, deposit
invoices in varied states (including one unpaid and one underpaid), credited deposits with real fee splits, and
withdrawals sitting in **every** status this API exposes — `confirmed`, `pending_approval`,
`pending_merchant_approval`, `insufficient_balance`.

```powershell
./tools/dev/Setup-LocalEnv.ps1
$env:DevSampleData__Enabled = "true"
dotnet run --project src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway
```

Full runbook, including the demo merchants' terms and the portal logins for each: **`docs/dev-sample-data.md`**.

Two things worth knowing when reading the data it produces:

- It seeds *inputs* and lets the real workers produce every deposit, journal and callback, so it takes 30–60
  seconds to converge and the numbers are genuinely ledger-derived — not fabricated rows.
- In dev the on-chain balance reader returns zero, so `/ops/reconciliation` and the dashboard custody tile
  report the full ledger holding as drift. That is the in-memory chain, not a custody fault.

---

## 23b. Verified against a running host (2026-09-11)

Every route in this document was exercised over HTTP against a booted Ops host before this section was
written, rather than checked off against the source. What was tested and what it proved:

**All 75 route-and-method pairs are reachable and return the envelope.** Path parameters were filled with a
valid-shaped but nonexistent id and bodies were sent empty, so writes answered with validation or not-found
instead of mutating anything. Every one returned the standard envelope with a usable `errorCode`.

**Four defects were found this way and fixed**, all of which would have hit the front end:

| Route | Was | Now |
|---|---|---|
| `POST /ops/accounts` | 500, unhandled null username | 400 `staff_user.username_required` |
| `POST /ops/roles` | 500, unhandled null name | 400 `role.name_required` |
| `POST /ops/treasury/reload/{id}/submit` (route removed 2026-09-17) | 500, null `signedHex` | 400 `ops.invalid_hex` |
| `GET /ops/treasury/hot-pool` | 400 with **no envelope** | 400 `ops.network_required` |

The last one is why the host-wide handler exists: `chain` was a required parameter, so a request without it
failed binding before any code ran and produced a response the client could not interpret.

**Browser behaviour, end to end.** Credentialed CORS preflight from `http://localhost:5173` returns the
origin and `allow-credentials: true` and permits `X-CSRF-Token`; an unlisted origin gets no
`allow-origin` header back. Login sets an httpOnly `cpe_ops_session` cookie and returns a `csrfToken`. The
cookie alone authenticates a read. A cookie-authenticated write **without** `X-CSRF-Token` is refused 403
`ops.csrf_invalid`; the same write with it reaches the handler. Logout revokes the session, and the next
request with that cookie is 401.

**Permission gating, with a genuinely restricted user.** An admin's wildcard passes every gate, so a role
holding only `ops.merchants.view` was created and logged in. It read `/ops/merchants` at 200 and was refused
`/ops/sweeps`, `/ops/compliance/policy` and `/ops/accounts` at 403 `ops.permission_denied` — and the denied
**write** was refused too, not merely hidden. Hide a control in the UI for a permission the user lacks, but
do not rely on hiding it: the server refuses it either way.

**Added after that sweep:** `GET /compliance/addresses` and `POST /compliance/screenings/latest` (REQ-26) were
exercised separately on a booted host — 21 checks covering one row per address, the summary ignoring
`decision`, stale filtering, request-order echo, `null` for never screened, the 200 cap refusing 201, the
missing-field and missing-chain refusals, and 403 for a user without `ops.compliance.view`.

### What this does not cover

Writes were probed against nonexistent ids, which proves routing, binding and error mapping but not the
success path of each mutation. Those are covered by the service-level test suite rather than over HTTP, which
is this project's convention for Ops endpoints — they are thin wrappers over already-tested services.

---

## 24. Known gaps — don't build UI that assumes these work today

- **2FA** — not implemented anywhere in the backend.
- **`volume` on the dashboard** returns `[]` — see §16b for why it was deliberately not built.
- **No mismatch-review workflow** for deposits (§12) — by design, not a missing feature.
- **No ops *action* on Sweep or Energy** (§22) — no manual retry, cancel, or stake trigger. Read-only.
- **No `EnergyPolicy` (threshold) read endpoint** — the resource snapshot already carries target/minimum energy.
- **No reconciliation history / time series** (§21) — only the current snapshot per (chain, asset).
- **No paged history endpoint** across transaction records generally — deferred to be applied uniformly.
- **Screening is OFF by default** (section 22b) - three independent flags, all defaulting off. Where none
  is enabled every verdict reads `Unavailable` and no payout is ever held for screening. The screens work,
  they are simply empty. Do not render that as broken.
- **No bulk re-screen and no scheduled re-screening** (section 22b) - a verdict is cached for 30 days and
  nothing re-checks a stored settlement wallet. Staff force one address at a time, from a record they are
  already looking at.
- **`userId` / `payerAddress` were removed**, not left null: both were hardcoded placeholders on every
  deposit/withdrawal row and carried no information. If real user attribution is wanted, it will be added
  deliberately as a populated field.
