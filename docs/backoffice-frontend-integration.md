# Back Office (OperationsApi) — Frontend Integration Guide

This is the complete, current contract for the Back Office API (`Api/OperationsApi`). It supersedes
`docs/backoffice-api.md`, which predates the roles/permissions rework and is missing several screens — do
not use that file as a reference; it describes a binary Admin/Viewer model that no longer exists.

Everything below was verified directly against the current endpoint source code, not written from memory.

---

## 1. Base URL

Dev default: `http://localhost:54001` (HTTPS on `54000`, self-signed dev cert — not trusted by default).
Production base URL is whatever the deployment target configures; ask ops/DevOps for the real value, it is
not baked into this doc.

All routes are prefixed `/api/v1/ops/...`. There is no versioning beyond `v1` today.

---

## 2. Response envelope — every single endpoint, success or failure

```json
{ "isSuccess": true,  "data": { /* endpoint-specific */ }, "error": null }
{ "isSuccess": false, "data": null, "error": "human-readable message" }
```

Always check `isSuccess`, not just the HTTP status — but the HTTP status is also meaningful (see below).
There is no machine-readable error *code* field, only `error` (a string message) — do not try to
pattern-match on it beyond display.

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
`status` ∈ `Pending | Active | Suspended | Closed` (PascalCase).

### `GET /api/v1/ops/merchants/{id}` — `ops.merchants.view`
Same row shape, single object.

### `GET /api/v1/ops/merchants/{id}/allowed-ips` — `ops.merchants.view`
```json
{ "merchantId": "guid", "allowedIps": ["1.2.3.4"] }
```

### `POST /api/v1/ops/merchants` — `ops.merchants.manage`
Creates **and auto-activates** the merchant (no separate activation step), and seeds **exactly one**
deposit wallet (not a pool — every wallet after the first is minted on-demand as the merchant needs one).

Request:
```json
{ "merchantCode": "string, required, max 64", "name": "string, required, max 256", "callbackUrl": "string, optional, valid absolute URL" }
```
Response 200:
```json
{
  "isSuccess": true,
  "data": {
    "merchantId": "guid",
    "merchantCode": "ACME-1",
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

400 on duplicate `merchantCode` or invalid callback URL. 500 (rare) if registration succeeded but
auto-activation failed — tell the user to check with engineering, the merchant may be half-set-up.

### `PATCH /api/v1/ops/merchants/{id}/status` — `ops.merchants.manage`
Request: `{ "active": true|false }` (`true` → activate, `false` → suspend).
Response: `{ "merchantId": "guid", "status": "Active" }`. 400 on an invalid transition (e.g. activating a
`Closed` merchant).

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
        "depositFeeFixed": 0.5, "depositFeeBps": 100, "depositFeePercent": 1.0,
        "withdrawalFeeFixed": 1.0, "withdrawalFeeBps": 50, "withdrawalFeePercent": 0.5
      }
    ]
  },
  "error": null
}
```
An unpriced merchant simply has an empty `fees` array — which means **zero fee**, not an error.

### `PUT /api/v1/ops/merchants/{id}/fees` — `ops.fees.manage`
Request:
```json
{
  "chain": "Tron", "coin": "USDT",
  "depositFeeFixed": 0.5, "depositFeeBps": 100,
  "withdrawalFeeFixed": 1.0, "withdrawalFeeBps": 50
}
```
`depositFeeFixed`/`withdrawalFeeFixed` are **display-unit decimals** (not base units), a `0` is valid (pure
percentage pricing). 400 if the chain/coin is unrecognized, or a fixed amount is negative or has more
decimal precision than the asset supports.

---

## 11. Wallets

Suspend/resume is a **temporary, reversible hold** — not a decommission. It only stops *future* deposits
from being recorded on that address (deposit detection checks wallet status at the moment a transfer is
first seen); anything already in flight before the suspend is unaffected and completes normally.

### `GET /api/v1/ops/wallets` — `ops.wallets.view`
Query filters (all optional, AND-combined): `merchantId` (guid), `address` (exact match string), `chain`
(e.g. `Tron`), `status` (`Active`|`Disabled`|`Suspended`), plus `page`/`pageSize`.
Row:
```json
{
  "walletId": "guid", "merchantId": "guid|null", "chain": 1, "address": "T...",
  "walletType": "Deposit", "status": "Active", "statusReason": "string|null",
  "depositsReceivedCount": 3, "createdAt": "...", "updatedAt": "..."
}
```
**Note:** `chain` here serializes as a **numeric enum value** (`1` = Tron), not a string — unlike almost
everywhere else in this API where chain is a string like `"Tron"`. Map it client-side (`1` = Tron; today
this is the only chain live). `statusReason` is only non-null while `status == "Suspended"`.

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

Query filters (all optional): `merchantId`, `systemOrderNumber` (guid), `merchantOrderNumber` (string),
`receivingAddress`, `network` (chain), `coin` (requires `network` to also be set), `fromDate`, `toDate`,
`page`, `pageSize`.

Row:
```json
{
  "merchantId": "guid",
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

Same query filters as deposits (minus `coin` needing `network` — same rule applies here too).

Row:
```json
{
  "merchantId": "guid",
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
  "createdAt": "...",
  "status": "pending",
  "callback": "Pending",
  "callbackFailedCount": 0,
  "callbackNextAttemptAt": "..."
}
```
`status` ∈ `pending | pending_approval | insufficient_balance | awaiting_release | confirmed | failed`
(lowercase). The states that need a human action, not just waiting:
- **`pending_approval`** → build an Approve/Reject action (§14 below). Plain `pending` needs no action —
  it's already approved and self-processing.
- **`insufficient_balance`** → the hot wallet can't physically cover it yet. Funds stay reserved (not
  lost). **Auto-resumes** once the wallet is reloaded — no endpoint needed to un-stick it, just wait or
  reload the wallet. Use `sourceWalletId` to jump to the Wallets screen and check that wallet's real
  on-chain balance if investigating.
- **`awaiting_release`** → funded but above the auto-send threshold, needs an operator Release action
  (§15 below).

`sourceWalletId` is the direct cross-reference into §11 (Wallets) — if a payout is stuck, this tells you
exactly which pool wallet to go inspect/reload.

---

## 14. Withdrawal approval (the `pending_approval` gate — above-threshold payouts)

### `POST /api/v1/ops/withdrawals/{withdrawalId}/approve` — `ops.withdrawals.approve`
No body. Response: `{ "withdrawalId": "guid", "status": "Approved" }`.

### `POST /api/v1/ops/withdrawals/{withdrawalId}/reject` — `ops.withdrawals.approve`
Request: `{ "reason": "string, required, max 512" }`
Response: `{ "withdrawalId": "guid", "status": "Rejected" }`. Releases the merchant's reserved funds and
fires the merchant's withdrawal-failed callback, same as an automatic failure.

Both 409 if the withdrawal isn't currently in `pending_approval`, 404 if not found.

---

## 15. Withdrawal funding holds (the `insufficient_balance` / `awaiting_release` states)

### `POST /api/v1/ops/withdrawals/{withdrawalId}/release` — `ops.withdrawals.manage`
No body. Releases an `awaiting_release` payout for sending.
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
  "assetId": "guid", "description": "string", "direction": "Debit|Credit",
  "amount": "base-unit integer string — NOT display decimal, unlike everywhere else in this API",
  "createdAt": "..."
}
```
**Watch this one:** `amount` here is a raw base-unit integer string (e.g. `"1000000"` for 1 USDT), not the
display decimal convention used everywhere else in this doc (§5). Convert client-side using the asset's
known decimals if you need to display it.

---

## 18. Status vocabulary cheat-sheet (casing is inconsistent across resources — this is real, not a typo)

| Resource | Field | Values | Casing |
|---|---|---|---|
| Deposit (payment intent) | `status` | `pending`, `confirmed`, `expired`, `failed` | lowercase |
| Withdrawal | `status` | `pending`, `pending_approval`, `insufficient_balance`, `awaiting_release`, `confirmed`, `failed` | lowercase-snake |
| Callback | `status` | `Pending`, `Notified`, `Abandoned` | PascalCase |
| Wallet | `status` | `Active`, `Disabled`, `Suspended` | PascalCase |
| Staff account | `status` | `Active`, `Disabled` | PascalCase |
| Merchant | `status` | `Pending`, `Active`, `Suspended`, `Closed` | PascalCase |

---

## 19. Known gaps — don't build UI that assumes these work today

- **`userId` and `payerAddress`** on the deposit/withdrawal rows are hardcoded `null` — not implemented.
- **No mismatch-review workflow** for deposits (§12) — by design, not a missing feature.
- **No treasury/hot-wallet-reload/cold-wallet screens** in this API at all — that flow only exists as
  dev-only endpoints in the *other* host (MerchantGateway's `/dev/treasury/*`), never ported here.
- **No Energy/Sweep/Reconciliation ops screens** — those modules run as background workers with zero Ops
  visibility today.
- **Per-merchant withdrawal/deposit limits** (min/max) are recorded by the fee-schedule domain but nothing
  reads them yet — config-based limits (`WithdrawalPolicy`) are what's actually enforced.
- **2FA** — not implemented, explicitly deferred.
- **No single-record "detail" endpoints** for deposits/withdrawals/wallets — only the paginated
  search/list. Build detail views (if needed) by keeping the row data already returned by search.
