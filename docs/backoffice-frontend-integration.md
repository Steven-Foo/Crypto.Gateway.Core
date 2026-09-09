# Back Office (OperationsApi) — Frontend Integration Guide

This is the complete, current contract for the Back Office API (`Api/OperationsApi`). It supersedes
`docs/backoffice-api.md`, which predates the roles/permissions rework and is missing several screens — do
not use that file as a reference; it describes a binary Admin/Viewer model that no longer exists.

Everything below was verified directly against the current endpoint source code, not written from memory.

**Related documents**
- `docs/merchant-portal-frontend-integration.md` — the **merchant-facing portal** API (`Api/MerchantPortalApi`,
  the backend for `apps/merchant`). A different host, different cookie, different permission vocabulary. Do not
  mix the two.
- `docs/dev-sample-data.md` — how to populate a local database with realistic merchants, deposits and
  withdrawals so these screens have something to render.

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
crosses to display form. Fee percentages are given as basis points on write (`depositFeeBps: 100` = 1%) and
also echoed back pre-divided as `depositFeePercent` on read.

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
{ "merchantId": "guid", "allowedIps": ["1.2.3.4"] }
```

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
    "wallet": { "chain": "Tron", "address": "T..." }
  },
  "error": null
}
```
**Critical UI requirement:** `apiSecret` and `signingSecret` are shown exactly once, here, and can never be
fetched again (not even via `GET /merchants/{id}`) — show them prominently with a copy button and an
explicit "save this now" warning. `wallet` can be `null` if seed provisioning failed server-side (logged);
this does not indicate a problem worth surfacing loudly — the merchant is still fully usable, the first
deposit call just provisions a wallet synchronously instead.

400 on an invalid `settlementMode`, an invalid `fees` percent (negative, >100%, or finer than 2 decimals), or
a negative/over-precise fixed/minimum amount.

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
Request: `{ "ipAddresses": ["1.2.3.4", "5.6.7.8"] }` — full replace, not additive. Invalid IP formats are
silently dropped and reported back in `invalidIps` (not a hard failure) unless *every* submitted IP was
invalid, in which case it's a 400 and nothing changes.
Response:
```json
{ "merchantId": "guid", "allowedIps": ["1.2.3.4"], "invalidIps": [], "cloudflare": { "added": 1, "removed": 0 } }
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
        "minimumWithdrawal": null, "maximumWithdrawal": null
      }
    ]
  },
  "error": null
}
```
An unpriced merchant simply has an empty `fees` array — which means **zero fee**, not an error.
`minimumDeposit`/`maximumDeposit`/`minimumWithdrawal`/`maximumWithdrawal` are read-only here (set via
`PUT .../deposit-limits` and `PUT .../withdrawal-limits` respectively) — included for a single-screen view.

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

**This is a full replacement, not a patch.** Every fee field is written on each call, and an omitted field
deserialises to `0` rather than being left alone — so posting only `depositFee*` silently sets the
withdrawal and top-up fees (and their minimums) to zero. **A UI that edits one fee must GET the record first
and post all of them back.** There is no partial-update endpoint.

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
Request: `{ "reason": "string, required, max 512" }`
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
  "createdAt": "...",
  "status": "pending",
  "callback": "Pending",
  "callbackFailedCount": 0,
  "callbackNextAttemptAt": "..."
}
```
`status` ∈ `pending | confirmed | expired | failed` (lowercase). `userId` and `payerAddress` are **always
null today** — not implemented yet, don't build UI that assumes real values will ever show up in the
current build. `receivedAmount`/`fee`/`txHash`/`confirms` are all `null` until a deposit has actually
matched the invoice.

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
  lost). **Auto-resumes** once the wallet is reloaded — no endpoint needed to un-stick it, just wait or
  reload the wallet. Use `sourceWalletId` to jump to the Wallets screen and check that wallet's real
  on-chain balance if investigating.
- **`awaiting_release`** → funded but above the auto-send threshold, needs an operator Release action
  (§15 below).
- **`pending_merchant_approval`** → a portal-initiated payout awaiting the MERCHANT's own approver, in their
  own portal. Platform staff can see it but must not action it — there is no Ops endpoint for this state. It
  leaves only when the merchant approves (then it either sends, or becomes `pending_approval` if it is above
  the threshold) or the merchant rejects it.

`sourceWalletId` is the direct cross-reference into §11 (Wallets) — if a payout is stuck, this tells you
exactly which pool wallet to go inspect/reload.

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

**Reminder:** `insufficient_balance` needs **no** endpoint at all to resume normally — reload the hot
wallet and the background worker resumes it automatically. Only use `cancel` if you're actually giving up
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

`callback` status you'll see embedded on transaction rows: `Pending | Notified | Abandoned`
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
| Withdrawal (user payout) | `status` | `pending`, `pending_merchant_approval`, `pending_approval`, `insufficient_balance`, `awaiting_release`, `confirmed`, `failed` | lowercase-snake |
| Withdrawal (merchant settlement) | `status` | additionally `pending_admin_audit`, `pending_finance_transfer`, `finance_settled` — see §19b | lowercase-snake |
| Callback | `status` | `Pending`, `Notified`, `Abandoned` | PascalCase |
| Wallet | `status` | `Active`, `Disabled`, `Suspended` | PascalCase |
| Staff account | `status` | `Active`, `Disabled` | PascalCase |
| Merchant | `status` | `Active`, `Frozen`, `Closed` | PascalCase |

---

## 19. Merchant terms — settlement period, wallet, caps, limits, threshold

Five setters on top of §10's fees. All are **display decimals** on the way in, converted at the edge, and
**over-precision is refused rather than truncated** (§5).

| Endpoint | Permission | Body |
|---|---|---|
| `PUT /ops/merchants/{id}/settlement-period` | `ops.merchants.manage` | `{ "days": 1 }` (0–30; 0 = T+0) |
| `PUT /ops/merchants/{id}/settlement-wallet` | `ops.merchants.manage` | `{ "chain": "Tron", "address": "T..." }` |
| `PUT /ops/merchants/{id}/withdrawal-cap` | `ops.fees.manage` | `{ "chain", "coin", "flatCap": 5000.0, "percentBps": 5000 }` |
| `PUT /ops/merchants/{id}/withdrawal-limits` | `ops.fees.manage` | `{ "chain", "coin", "minimum": 10.0, "maximum": 5000.0 }` |
| `PUT /ops/merchants/{id}/deposit-limits` | `ops.fees.manage` | `{ "chain", "coin", "minimum": 1.0, "maximum": null }` |
| `PUT /ops/merchants/{id}/approval-threshold` | `ops.fees.manage` | `{ "chain", "coin", "threshold": 1000.0 }` |
| `PUT /ops/merchants/{id}/profile` | `ops.merchants.manage` | `{ "contactEmail", "settlementMode", "remark" }` — all optional, write-only |

**`null` vs `0` is a real distinction on every optional amount, not a formality:**

- `null` = **unset** ⇒ the platform config value applies (`Withdrawal:Policies`).
- `0` = an explicit zero — "no minimum", "cap cash-out at zero", "everything needs approval".

Sending `0` where you meant "clear it" changes behaviour. A limits form must be able to submit `null`.

**Read them back** on `GET /ops/merchants/{id}/fees` (limits, cap, threshold) and
`GET /ops/merchants/{id}` (`settlementDelayDays`, `settlementWallets`).

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

## 20. Treasury — cold wallet, hot-pool reload, and top-ups

All `ops.treasury.manage`. This is the human-in-the-loop custody flow: funds accumulate in a **cold** treasury
whose key the system never holds, and an operator periodically reloads the **hot pool** that pays withdrawals.

| Endpoint | Purpose |
|---|---|
| `GET /ops/treasury/hot-pool?chain=Tron` | Pool wallets **with live on-chain balances**, to pick which needs topping up. **Never returns a key reference.** |
| `POST /ops/treasury/cold-wallet` | `{ "chain", "address" }` — register the watch-only cold address |
| `POST /ops/treasury/reload` | `{ "chain", "targetWalletId", "amount" }` → `{ reloadId, unsignedTransactionHex }` |
| `POST /ops/treasury/reload/{reloadId}/submit` | `{ "signedHex" }` |

**The UI must sign client-side.** The backend builds an *unsigned* transaction and accepts a *signed* blob; the
cold private key must never be sent to any backend, including this one. A reload screen that collects the key
and posts it has defeated the entire point of the cold tier. Broadcast and confirmation are done by a worker in
the money host, so `submit` returning 200 means *accepted*, not *sent*.

No ledger entry is written — treasury→hot is custody-internal, so total custody is unchanged, only its
location. Once it confirms, parked `insufficient_balance` withdrawals resume automatically.

### `POST /ops/treasury/top-up` — recording a hot-wallet top-up

```json
{ "chain": "Tron", "targetWalletId": "...", "amount": 5000.00,
  "transactionHash": "0x...", "sourceAddress": "T..." }
```

**Different from the cold reload above.** A reload is *built and broadcast by this system*. A top-up already
happened: an admin sent company funds into a hot wallet from their own business wallet, and is telling us
about it. So it is verified and recorded, never executed.

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

## 22. Sweep and Energy — read-only operational views

Read-only. There is deliberately **no ops action** (no manual retry/cancel/stake) on these yet.

### `GET /api/v1/ops/sweeps` — `ops.sweep.view`
Filters: `chain`, `status`, `walletId`, `page`, `pageSize`. Returns `{ page, pageSize, totalCount, summary, items }`.

Row: `sweepId`, `walletId`, `chain`, `assetId`, `fromAddress`, `toAddress`, `amount(+amountBaseUnits)`,
`status`, `txHash`, `confirmations`, `failureReason`, `createdAt`, `updatedAt`.

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

## 24. Known gaps — don't build UI that assumes these work today

- **2FA** — not implemented anywhere in the backend.
- **`volume` on the dashboard** returns `[]` — see §16b for why it was deliberately not built.
- **No mismatch-review workflow** for deposits (§12) — by design, not a missing feature.
- **No ops *action* on Sweep or Energy** (§22) — no manual retry, cancel, or stake trigger. Read-only.
- **No `EnergyPolicy` (threshold) read endpoint** — the resource snapshot already carries target/minimum energy.
- **No reconciliation history / time series** (§21) — only the current snapshot per (chain, asset).
- **No paged history endpoint** across transaction records generally — deferred to be applied uniformly.
- **No client-side signing UI** for the treasury reload (§20) — the API is ready, the signing component is not.
- **`userId` / `payerAddress` were removed**, not left null: both were hardcoded placeholders on every
  deposit/withdrawal row and carried no information. If real user attribution is wanted, it will be added
  deliberately as a populated field.
