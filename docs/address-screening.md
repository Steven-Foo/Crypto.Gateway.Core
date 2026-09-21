# Address screening (Platform/Compliance)

Screens a blockchain address against a third-party AML provider and records what was decided and why.

**Built:** the module, the MistTrack adapter and the evidence trail (§1–§7); the user-payout gate (§8),
which screens a destination before any money moves; and settlement-wallet screening (§9), which checks a
merchant's cash-out destination before staff can whitelist it.

**Off by default,** via three independent switches — `Compliance:Enabled`,
`Withdrawal:Screening:Enabled` and `Merchant:Screening:ScreenSettlementWallets`. Nothing screens and no
payout waits until an operator turns them on deliberately.

---

## 1. Why it is its own module

A risk score is a third party's opinion about an address. That is neither chain state nor a fact, so it
does not belong in `Blockchain`, whose whole job is to prove a transaction occurred and which must contain
no business logic (§8). It also is not owned by Withdrawal, because settlement wallets and, later, deposit
senders need the same answer — homing it in one consumer would force the others through a module boundary
they are not allowed to cross (§4.5).

So it is `Gateway.Core/Platform/Compliance`, schema `compliance`, with the layers it actually uses:
Contracts, Domain, Application, Infrastructure, Tests. No `Api/` project — endpoints live in hosts (§4.3).

**Ledger impact: none.** Screening reads a public address and stores an opinion. It moves no money, posts
no journal, and holds no key (§10).

---

## 2. Two ports, on purpose

```
Withdrawal / Merchant / (later) Deposit
            │  §4.5 — Contracts only
            ▼
   IAddressScreeningService          ← what callers consume; returns a verdict
            │
   AddressScreeningService           ← our policy + the evidence trail
            │
     IAddressRiskProvider            ← the vendor seam
       ├── MistTrackAddressRiskProvider   (real; sandbox or live by BaseUrl)
       └── InMemoryAddressRiskProvider    (dev/test; contacts nothing)
```

Callers never learn which vendor answered. That is what keeps the vendor swappable — moving to Crystal,
TRM or Elliptic replaces one class.

The split also puts **policy on our side of the line**. The provider returns a score and its own band
name; the thresholds that turn that into Allow, Review or Block are ours. A vendor swap therefore cannot
silently move our risk appetite, which it would if we consumed their band names directly.

---

## 3. The decision model

| Decision | Meaning |
|---|---|
| `Allow` | Below every threshold. Proceed. |
| `Review` | Hold for a human. Never an automatic refusal. |
| `Block` | Refuse under the policy in force. |
| `Unavailable` | **No verdict could be obtained.** Not a clean result. |

`Unavailable` is a distinct outcome rather than a default, and that is the most important choice in the
module. Collapsing it into `Allow` silently removes the control the moment the vendor has an outage.
Collapsing it into `Block` hands a third party the power to halt every payout. Keeping it separate forces
each caller to decide what an unknown means for its own flow.

**Sanctions designations block regardless of score.** `AlwaysBlockIndicators` is checked first and
independently of the numbers, because a sanctions designation is a legal fact and not a gradient. Without
that rule, a threshold tweak could start letting one through on a low score.

Defaults follow the provider's own bands: block at 91 (Severe), review at 71 (High).

### Direct designation versus indirect exposure

The always-block rule matches only what the provider says about **this address**, never risk inherited
through a chain of counterparties. That distinction is the difference between screening being usable and
screening refusing most of your legitimate destinations.

MistTrack reuses the same `risk_type` codes for both, tagging each entry with an `exposure_type` and a hop
count. A live call on 2026-09-11 against a widely used TRX address returned:

```
score 3, risk_level "Low"
risk_detail[0]: risk_type "sanctioned_entity", exposure_type "indirect", hop_num 3,
                entity "htx", percent 2.735
```

That is an ordinary address three hops from an exchange that once served a sanctioned customer. Any address
with exchange history looks like this. Matching the always-block rule against the full indicator list would
have refused it outright, despite the vendor grading it 3 out of 100.

So the adapter returns two lists. `Indicators` is everything, kept as evidence and shown on the ops screen,
because a reviewer needs to see what the vendor actually reported. `Designations` is the subset at
`exposure_type` direct, and only that list can force a Block. An entry with **no** stated exposure type
counts as direct, so an unfamiliar response shape over-refers rather than under-detects.

Indirect exposure is not discarded and not ignored. It is a gradient, the vendor already prices it into the
score, and the thresholds above are what judge it. A row may therefore legitimately read `Allow` while
listing `sanctioned_entity` among its reasons.

That is the whole of it by default. An optional second rule can raise close, heavy indirect exposure to
`Review` — never to `Block` — but it ships disabled and its thresholds are meant to be measured rather than
guessed. See section 13.

**Configuration adds to the built-in list, it cannot remove from it.** That is how .NET binds a string array
onto a property that already holds one, and it is left that way on purpose: a sanctions override is exactly
the rule that should not be deletable by editing a settings file. Duplicates are collapsed before the policy
is stamped onto an evidence row.

---

## 4. The evidence trail

`compliance.AddressScreening` is **append-only**. Re-screening an address inserts a new row rather than
updating the old one, the same discipline the ledger uses and for the same reason: a compliance decision
has to be explainable months later, and an overwritten score destroys the only proof of why a payout was
allowed or refused on the day.

Each row keeps three things that answer different questions:

- **the provider's score and raw JSON** — what the vendor actually said
- **our decision** — what we did about it
- **`PolicyDescription`** — the thresholds in force at the time

Storing only the score would let a later threshold change rewrite what we appear to have decided. Storing
only the decision would leave us unable to show what it rested on.

---

## 5. Caching, quota and the rate limit

A completed screening is reusable until `FreshUntil` (`CacheDays`, default 30). A **failed** screening is
never reusable — otherwise one outage would pin an address to "unknown" for the whole window, long after
the provider recovered.

Purpose deliberately does not narrow a cache hit. Risk is a property of the address, not of why you are
asking, so a settlement wallet screened yesterday answers a payout question today.

**The binding constraint is the rate limit, not the money.** On a flat subscription the marginal cost of a
call is zero until the daily quota runs out; what actually bites is one call per second.

**Pacing lives in a singleton, and that is load-bearing.** It used to be a field on the provider — which is
registered as a typed `HttpClient` and is therefore **transient**, so every dependency-injection scope got a
fresh gate starting from the beginning of time. Pacing held inside one worker pass and imposed no constraint
at all between passes, between a worker and an HTTP request, or between two workers in the same process.
Each caller paced itself perfectly while the process breached the limit by the number of concurrent callers.
Found while adding the second screening caller, which would have doubled the real rate. A breach is not
cosmetic: the provider answers 429, a 429 is a screening with no verdict, and the payout gate holds a
no-verdict payout for staff, so an unpaced burst converts directly into a queue of held payouts.

It is still only correct **within one process**. Two instances hold two gates and would breach the limit
together, which is why every screening path is drained under a single-flight distributed lock. Remove that
lock and the limiter has to become a Redis token bucket — the lock is what makes an in-process limiter
sufficient, and dropping it silently drops the pacing guarantee with it.

### Capacity, in numbers

The plan allows **10,000 calls/day at one call per second**. One call per second is 86,400 a day, so the
daily quota binds first as long as a screening costs one unit.

**How much one screening costs is measured, not documented.** The vendor's price list charges ten times more
for `risk_score` than for `address_labels` on pay-as-you-go, which is reason enough to suspect the
subscription may weight them. Verified 2026-09-11: there is **no way to read this from the API** — the
response carries no rate-limit or quota headers, and there is no usage endpoint (`v1/quota`, `v1/usage`,
`v1/user_info`, `v1/account`, `v1/api_quota`, `v1/remaining_quota` and `v1/balance` all answer
`PageNotFound`). The dashboard is the only source, so measuring it is a deliberate act:

```powershell
./tools/dev/Test-AddressScreening.ps1 -Mode Quota
```

It makes exactly one metered call with a clear before-and-after, and turns the drop into a capacity figure.

| One screening costs | Screenings per day | Headroom at ~170/day |
|---|---|---|
| 1 unit | 10,000 | 1.7% used, 59x |
| 5 units | 2,000 | 8.5% used, 12x |
| 10 units | 1,000 | 17% used, 6x |

**MEASURED VALUE: not yet recorded.** Fill this in once the measurement is run.

The ~170/day figure is 5,000 payouts a month, and it is a deliberate over-estimate because it ignores the
cache. Only a first sighting of an address costs anything; a repeat destination inside 30 days is free. It
also ignores that settlement wallets are screened once per merchant per chain.

**The plan covers the workload even on the worst assumption.** At ten units a call there is still six times
the headroom needed. So the weight is worth knowing for planning, not because it changes whether to enable
screening.

**What the rate limit means in practice.** A day's payouts drain at one per second, so 170 of them occupy
under three minutes of a worker's time. The limit only bites on a burst, and a burst is exactly what the
queue exists to absorb — which is why screening was never put in the request path.

---

## 6. Configuration

```jsonc
"Compliance": {
  "Enabled": true,          // false ⇒ every call returns Unavailable, contacting nobody
  "BlockScore": 91,
  "ReviewScore": 71,
  "CacheDays": 30,
  "AlwaysBlockIndicators": [ "sanctioned_entity", "Sanctioned Entity" ],
  "MistTrack": {
    "BaseUrl": "https://sandbox-api.misttrack.io",   // or https://openapi.misttrack.io
    "ApiKey": "",           // appsettings.Local.json only — never committed (§10)
    "RequestsPerSecond": 1, // Standard plan allows 1/sec, 10,000/day
    "TimeoutSeconds": 15
  }
}
```

`Enabled` defaults to **false** in code. Screening must be switched on deliberately; a forgotten config
key should never be able to switch it off silently.

**Which provider gets registered** mirrors how the chain adapter is chosen. Production always takes the
real adapter, because an in-memory provider there would fabricate clean scores indistinguishable from real
verdicts — the same reason a fake signer is never registered in production (§10). Development and Staging
take the in-memory provider unless a MistTrack key is present, which lets a developer point at the sandbox
and exercise the real adapter without a live plan.

`BaseUrl` alone decides sandbox versus live, so the **same** adapter code runs in both. A sandbox-only
adapter would prove nothing about the one that runs in production.

---

## 7. Testing it

**Unit tests (17, no network).** Policy and caching are tested against the real service with the in-memory
provider. Response mapping is tested against payloads copied verbatim from MistTrack's sandbox
documentation — fixtures rather than live calls, because a live test would spend daily quota and fail
whenever the vendor is down, which is how a suite gets ignored.

**The script.** `tools/dev/Test-AddressScreening.ps1` has three modes. It finds the key in
`$env:MISTTRACK_API_KEY`, then in the git-ignored `appsettings.Local.json`, and never writes or echoes it.

| Mode | Cost | What it is for |
|---|---|---|
| `-Mode Entitlement` (default) | 2 calls | Confirms `risk_score` is on the plan. Repeat after any plan change. |
| `-Mode Quota` | 1 call | Measures what one screening costs against the daily allowance. |
| `-Mode Sandbox` | 0 calls | Diagnoses the sandbox, which does not work with a production key. |

```powershell
./tools/dev/Test-AddressScreening.ps1 -Mode Quota
```

**The script is ASCII-only and carries a byte-order mark, and both matter.** Windows PowerShell 5.1 reads a
BOM-less file as ANSI, which turns a UTF-8 em dash into a character it accepts as a **string delimiter** —
the script then fails to parse with a misleading "the string is missing the terminator". The same
silent-encoding trap as the `db/sql` header rule. Keep it ASCII so it survives a stripped BOM too.

### What the live run answered (2026-09-11)

**Question 1 is settled: `/v3/risk_score` IS included in the Standard plan.** A live call returned HTTP 200
with a fully scored response including `risk_detail`, `detail_list` and a report URL. Nothing about the
integration plan needs revisiting on entitlement grounds.

**Question 2 is still open, and cannot be answered from the API.** There are no rate-limit or quota headers
on the response, and no usage endpoint exists — `v1/quota`, `v1/usage`, `v1/user_info`, `v1/account`,
`v1/api_quota`, `v1/remaining_quota` and `v1/balance` all return `PageNotFound`. The dashboard is the only
source. Run `-Mode Quota` and record the number in section 5.

**The sandbox is not usable with this key.** `https://sandbox-api.misttrack.io` returns HTTP 400 with an
empty body for every request, while the same key against `https://openapi.misttrack.io` succeeds. Either the
sandbox needs its own separately issued key or it is not part of this plan — worth asking the vendor.

The consequence to watch for locally: **a dev host holding a key but pointing at the committed sandbox base
URL returns `Unavailable` for everything.** That is the fail-safe working, not a bug, but it makes local
screening useless. Either leave the key out entirely and use the in-memory provider, or point the base URL
at the live API and accept that dev calls spend real quota.

---

## 8. Phase 2 — gating user payouts

Built. A user payout's destination is screened before any money moves.

### Where it sits in the payout flow

```
request → ledger reserve
            │
            ├─ portal payout → PendingMerchantApproval → merchant approves ─┐
            └─ HMAC-API payout ──────────────────────────────────────────────┤
                                                                            ▼
                                                                   PendingScreening
                                                                            │
                            ┌───────────────────────────────┬───────────────┴──────────────┐
                          Allow                      Review / Unavailable                Block
                            │                               │                              │
                 threshold re-resolved            PendingApproval                      Rejected
                  ↓                ↓              (staff decide)                (reserve RELEASED)
             Approved      PendingApproval
```

**After the merchant's sign-off, before the platform's.** A payout the merchant will decline never spends a
provider call, and staff reviewing a payout always have its verdict in front of them. A blocked payout never
reaches staff at all.

**User payouts only.** A merchant cash-out pays to the staff-whitelisted settlement wallet and already stops
at `PendingAdminAudit` for a human, so screening belongs where that wallet is whitelisted, not on every
cash-out.

### What each verdict does

| Verdict | Outcome | Reserve |
|---|---|---|
| Allow | Normal routing resumes; the approval threshold is re-resolved at this moment | Held |
| Review | `PendingApproval` — staff decide, with the reason on the row | Held |
| Unavailable | `PendingApproval` by default (`OnUnavailable`) | Held |
| Block | `Rejected` | **Released** |

A Block releases the reserve through the same event path a staff rejection uses. A refusal must never strand
the merchant's money in clearing.

Review and Unavailable both land in the existing platform approval queue rather than a second review state,
so staff have one place to act and the existing approve/reject actions are the override. The row records
which of the two it was, because "risky" and "we could not tell" call for different judgement.

### Why a worker and not the request path

The provider allows roughly one call per second. Screening a burst inline would serialise into the API
request and time out, and a timed-out screening is a payout with no verdict — the one outcome worth avoiding.
The queue is drained by `WithdrawalScreeningWorker` instead, which turns that into latency before sending,
which the payout pipeline already has.

**The worker's single-flight lock is load-bearing**, not just an efficiency measure. The provider's rate
limit is enforced in-process by the adapter, which is only correct while one instance screens at a time. Two
instances would each pace themselves correctly and still breach the limit together. If that lock is ever
removed, the adapter's limiter must become a Redis token bucket.

### Configuration

```jsonc
"Withdrawal": {
  "Screening": {
    "Enabled": false,              // payouts only enter PendingScreening when this is on
    "OnUnavailable": "Hold"        // or "Allow"
  }
}
```

Deliberately separate from the `Compliance` section: Compliance answers "how risky is this address", the
payout flow answers "what do I do about it". A second consumer can answer the second question differently
without renegotiating a shared policy.

`Enabled` defaults to **false**. `OnUnavailable: Allow` keeps money moving during a vendor outage at the
price of an unscreened payout — the exposure screening exists to prevent — so it is never the default.

### Ops surface

`WithdrawalAdminRow` carries `screeningDecision`, `screeningScore` and `screeningId`, and the effective-status
vocabulary gains `pending_screening` (its own bucket, not folded into `pending`, so an operator can tell a
queue waiting on a third party from one waiting on us). Staff approving a flagged payout see why on the same
screen rather than in a lookup they might skip.

### Schema

Migration `AddWithdrawalScreening`: three nullable columns on `withdrawal.Withdrawal` — `ScreeningId`,
`ScreeningDecision`, `ScreeningScore`. `ScreeningId` is an opaque cross-module reference into
`compliance.AddressScreening`, deliberately **not** a foreign key (§4.5). The new status is string-stored and
fits the existing column. **No ledger impact** — screening decides whether a payout proceeds, never what is
posted.

The verdict is snapshotted onto the payout rather than re-read from Compliance, because the evidence is
append-only and a later re-screen of the same address must never appear to change what this payout was judged
on.

### Verified

Seven pipeline tests on real SQL Server, plus end-to-end on a booted host over the signed HMAC API: a clean
destination cleared and reached `Broadcast`; an unconfigured provider returned Unavailable and **held for
review rather than allowing** (the fail-safe, observed by accident before the host had `Compliance` config);
and with screening off a payout routed straight to `Approved` having never called the provider.

---

## 9. Settlement-wallet screening

Built 2026-09-11, **revised 2026-09-18** when settlement wallets became a set (several per chain, one
active — see §16). A merchant's settlement wallet is screened when staff whitelist it. This is the highest
value per provider call in the whole integration: one call per merchant per chain, protecting the destination
every one of that merchant's earnings is paid to.

### It behaves differently from the payout gate, on purpose

| | Payout destination | Settlement wallet |
|---|---|---|
| When | Worker, off a queue | Synchronously, in the staff request |
| Block | Rejected | **Saved, but cannot be made the active (paid) destination** |
| Review | Held for staff | **Accepted and activated (if asked)**, warning returned |
| Unavailable | Held for staff | **Accepted and activated (if asked)**, warning returned |

Two differences, both deliberate.

**Synchronous rather than queued**, because whitelisting is a rare deliberate act — one call, no burst to
pace — so there is no reason to make an operator wait on a queue. Payouts arrive in bursts against a one-call
-per-second limit, which is the only reason that path needs a worker.

**Whitelisting is never refused; only ACTIVATION can be.** A payout runs unattended, so anything short of a
clean verdict parks it for a human. Adding a settlement wallet runs *with* a human already exercising
judgement, who may hold context the provider does not — so a flagged or unobtainable verdict is surfaced to
them rather than overriding them, and the address is saved either way. **Making it the ACTIVE destination is
the one act this control still gates**: a direct sanctions designation (`Block`) refuses activation, because
every one of that merchant's earnings would then be paid to it, and a sanctions hit is a legal fact rather
than a risk appetite. The refusal is configurable
(`Merchant:Screening:BlockActivationOnScreeningBlock`, default **true**) — for a business that wants the
judgement to rest entirely with the operator, setting it `false` still records and returns the verdict, it
just never blocks the activate call.

A refused *activation* leaves the existing active wallet untouched. Losing a good destination to a failed
replacement would halt that merchant's cash-outs for a reason unrelated to the wallet already active.

### Configuration

```jsonc
"Merchant": {
  "Screening": {
    "ScreenSettlementWallets": false,
    "BlockActivationOnScreeningBlock": true
  }
}
```

A third section, alongside `Compliance` and `Withdrawal:Screening`, following the same split: Compliance says
how risky an address is, each consumer says what to do about it. They must be switchable independently
precisely because they answer differently.

### The provider is an optional dependency

`MerchantRegistrar` resolves `IAddressScreeningService` **softly**, so a host that never whitelists a
settlement wallet — the merchant portal — is not forced to compose Compliance just to boot (§15.10).

With screening enabled but no provider composed, the call **fails loudly** rather than silently skipping the
check. A wallet that looks screened but is not is worse than an error.

### Response

```jsonc
{
  "isSuccess": true,
  "data": { "merchantId": "...", "walletId": "...", "network": "Tron", "address": "T...",
            "label": "primary", "status": "Active", "isActive": true,
            "screeningDecision": "Review", "screeningScore": 78, "screeningId": "..." },
  "warnings": ["Address screening flagged this wallet: High (score 78) ..."]
}
```

`warnings` is empty for a clean or unscreened address, so the normal case stays silent. A non-empty list
means the wallet **was** saved (and activated, if that was requested) but screening had something to say —
a UI must not read a 200 as silence. `screeningDecision` is `null` when screening is off, which is distinct
from `"Unavailable"` (we asked and could not get an answer). A Block on ACTIVATION is a `400` with error code
`merchant.settlement_wallet_blocked`, not a 200 with a warning — that is the one outcome this control still
enforces.

**The wallet is still saved on a 400.** The standard error envelope has no room for the created
`walletId` alongside `errorCode`, so a caller that POSTed with `activate:true` and got refused has to
list the merchant's `settlementWallets` (`GET /ops/merchants/{id}`, backoffice-frontend-integration.md §19) to find the row it just
created — it is there, `status:"Retired"`, ready to activate later or investigate.

### Two pre-existing defects fixed along the way

`MerchantRepository.GetByIdAsync` never included `SettlementWallets`, so replacing a merchant's settlement
wallet died on a unique-index violation — a 500. **`GetByCodeAsync` had the identical omission**, found
separately when the dev seeder (which resolves by code) failed the same way on every boot of an already-
seeded database. Both fixed, with regression tests at each exact call shape.

A third, related defect surfaced when settlement wallets became a set (§16): switching the active wallet is
two UPDATE statements (retire the old, activate the new) that the filtered unique index will not tolerate
out of order, and EF chooses that order itself — so doing both in one `SaveChanges` failed intermittently.
Fixed by retiring-and-saving before activating, inside one transaction.

---

## 10. The ops screen

Built. Three routes on `Api/OperationsApi`, over the evidence trail the money host writes.

| Route | Permission | What it does |
|---|---|---|
| `GET /api/v1/ops/compliance/screenings` | `ops.compliance.view` | Paged list, newest first, filterable by chain, decision, purpose, address and date |
| `GET /api/v1/ops/compliance/screenings/{id}` | `ops.compliance.view` | One record, plus the provider's verbatim payload |
| `POST /api/v1/ops/compliance/screenings/re-screen` | `ops.compliance.manage` | Screens again, ignoring the cache, and appends a new row |

**No migration.** The table already carried an index on decision and screened-at, added when the module was
built for exactly this read.

**Re-screening carries its own permission** because it spends provider quota, and the plan meters calls per
day. A read-only analyst must not be able to exhaust the budget the payout queue depends on.

**It appends, it never edits.** The trail's value is that a payout's verdict stays explainable months later,
so there is no route here that alters a past decision. The bypass is also deliberately absent from
`ScreenAsync`, the method the money paths call: a per-payout cache bypass is precisely what the rate limit
cannot absorb, so forcing a fresh call stays a human act.

**The counters count addresses, not rows.** An address screened weekly for a year would otherwise dominate a
"blocked" count fifty times over. The summary resolves each address to its most recent verdict, so a number
that grows only when work actually accumulates. Verified live: an address holding an old `Unavailable` row
and a newer `Allow` row appears in the list when filtering for `Unavailable`, and counts as `Allow`.

**The raw payload appears only on the detail read.** A list of fifty rows would otherwise drag fifty JSON
blobs across the wire to render a table that shows none of them.

An unavailable provider returns 200 with a verdict of `Unavailable`, never a 500. The request worked; the
answer is "we could not tell", and reporting that as a server error would send an operator chasing the wrong
thing.

### Verified over HTTP

On a booted Ops host: the list returned real evidence rows with working chain, decision, purpose and address
filters; unknown filter values returned 400 with `ops.invalid_decision`, `ops.invalid_chain` and
`ops.invalid_purpose`; an unknown id returned 404 `ops.not_found`; unauthenticated returned 401; both codes
appear in the permission catalog. A re-screen against an address holding a fresh cached verdict still called
the provider, which is the cache bypass proven rather than asserted. A live re-screen returned `Allow` at
score 3 with `sanctioned_entity` among its reasons, which is the designation split working end to end on
real vendor data.

---

## 11. Keeping a settlement wallet's verdict current

Built. Whitelisting screens an address **once**. A verdict is a snapshot, not a standing fact: an address
clean on the day staff approved it can be designated months later, and without this nothing would ever look
again — while every one of that merchant's earnings continues to be paid to it.

A worker re-screens every whitelisted settlement wallet on an interval, and the merchant admin read now
carries the standing verdict so staff can see it where they manage the account.

**Active wallets only, since §16 made settlement wallets a set.** A retired one is paid nothing, so
re-screening it would spend quota to learn nothing actionable — `IMerchantSettlementDirectory.ListAllAsync`
filters to `Status == Active` before handing the pass its candidates.

### It flags. It never revokes.

This is the design decision, not an omission. A worsened verdict records evidence and logs a warning. It
does not remove the wallet.

- **Revoking halts a merchant's cash-outs entirely.** Doing that automatically, with nobody in the loop,
  hands a third party's opinion — or its outage — the power to stop a merchant being paid. That is the
  exact failure this module refuses elsewhere by keeping `Unavailable` a separate outcome.
- **There is already a human in front of the money.** Every merchant cash-out stops at
  `PendingAdminAudit`, so a flagged wallet is caught before funds move, by someone who can weigh context
  the provider does not have.
- **A revocation is destructive and needs re-approval to undo.** A flag costs nothing to ignore and
  nothing to act on.

### What counts as worsening

Only a move up the scale Allow, Review, Block. `Unavailable` is deliberately **not on that scale** and never
counts as a downgrade — a provider outage is not news about the address, and alarming on it would fill the
log every time the vendor had a bad afternoon, which is how a real alert gets ignored. A first-ever verdict
is flagged only if it is itself bad; a clean first result is the ordinary case.

### It costs almost nothing to run often

A pass calls the same `ScreenAsync` the money paths use, so any still-fresh verdict is served from cache
without touching the provider. **`CacheDays` drives quota consumption, not the interval.** A short interval
only shortens the lag between a verdict expiring and being refreshed. The pass reports how many wallets
actually cost a call, which is the number to watch.

### Configuration

```jsonc
"Merchant": {
  "Screening": {
    "ScreenSettlementWallets":   false,  // check a wallet as it is whitelisted
    "RescreenSettlementWallets": false,  // periodically re-check wallets already on file
    "RescreenIntervalHours":     12
  }
}
```

The two switches are separate because they spend quota very differently. Whitelisting is a handful of calls
a month; re-screening is one call per merchant per chain per cycle, forever. Turning on the first should not
silently commit you to the second.

### Where the verdict shows up

`GET /ops/merchants/{id}` returns each settlement wallet with `screeningDecision`, `screeningScore`,
`screeningId` and `screenedAt`. It is read from stored evidence only, so opening a merchant spends no quota
and a vendor outage cannot slow it down or break it. Every field is null when the address has never been
screened, which is deliberately different from a verdict of `Unavailable`.

`screenedAt` matters as much as the decision. An old verdict on a high-value destination is itself worth
seeing.

---

## 12. Inbound: screening our own deposit addresses

Built. This is Phase 3, in the only form the problem actually allows.

### Why sender screening is not the design

An inbound transfer **cannot be screened while it is in flight**. There is nothing to screen until it lands,
and by then it is a fact. It cannot be refused either: an arrived deposit is credited, and a frozen
merchant's deposits still credit the ledger (§14), because freezing stops issuing and withdrawing but never
recording. A control that can only say "no" after the money is already booked is not a control.

What is available is the other side of the same graph. A provider scores an address from its transaction
history, so funds arriving from a bad counterparty **raise the score of our own receiving address**. Watching
the addresses we issue is therefore the honest inbound check, and it works after the fact by design rather
than by accident.

### It records and flags. Nothing else.

No deposit is reversed, no credit withheld, no wallet disabled. The restraint is firmer here than for
settlement wallets: by the time an address looks bad, the money has already reached a merchant's balance, so
an automatic reaction would mean clawing funds back on a vendor's say-so.

A flag says which address received something worth investigating. What follows is a human decision.

### Scheduled and manual, deliberately separate

| | Trigger | Honours `Enabled` | Honours the cap |
|---|---|---|---|
| Scheduled | Worker, `IntervalHours` | Yes | Yes |
| Manual | `POST /ops/compliance/deposit-addresses/screen` | **No** | Yes |

The manual pass bypasses the enabled switch so staff can check on demand without committing to a standing
spend. It does **not** bypass the cap: a manual run costs exactly what a scheduled one costs, and an operator
clicking a button is no reason to let it consume a day's budget.

### The per-pass cap is the load-bearing control

Deposit addresses are the one candidate set that grows without bound — one per merchant, more as volume
grows — and this shares a quota with the payout gate, which is the control that actually holds money. An
uncapped pass could spend a day's budget and leave payouts unable to be screened. **This pass must never be
the reason a payout cannot be screened.**

Whatever is not reached is picked up next pass, because the candidate query returns whatever still lacks a
fresh verdict. The response reports `candidates` against `screened`; the two diverging means the cap is
biting and a backlog is building, which the worker also logs.

**Only funded addresses are candidates.** A provisioned address that has never received anything has no
transaction graph, so screening it spends a call to be told nothing.

**Candidates are found with one query, not one per address.** `FindAddressesNeedingScreeningAsync` returns
the subset with no fresh verdict, capped at the budget, so a pass over thousands of addresses is a single
indexed read rather than thousands of round trips.

### Configuration

```jsonc
"Wallet": {
  "Screening": {
    "Enabled":             false,   // the SCHEDULED pass; the manual one runs regardless
    "MaxAddressesPerPass": 100,     // hard ceiling on provider calls per pass
    "IntervalHours":       24,
    "Chains":              [ "Tron" ]
  }
}
```

### A configuration trap this found, the hard way

**.NET binds a configuration array by ADDING to whatever the property already holds.** A settings file
listing `["Tron"]` against a code default of `[Tron]` produces `[Tron, Tron]`. Observed live: the pass
looped twice over the same chain and screened every address twice, doubling quota consumption for no extra
information.

The same behaviour was found earlier on `Compliance:AlwaysBlockIndicators`, where it was merely noisy. Here
it doubled the bill. Both are now de-duplicated at the point of use, and an audit confirms they are the only
two array-typed options in the codebase with a non-empty default. **Any array option added later behaves the
same way.**

---

## 13. The proximity rule for indirect exposure

Built, and **off by default**. It is the knob for the question the decision model otherwise flattens.

### What it addresses

All indirect exposure is otherwise treated identically: recorded as evidence, left entirely to the vendor's
score. That is the safe default and it is what makes screening usable at all. But it treats these as the
same fact:

| Exposure | Without the rule |
|---|---|
| 60% of volume, one hop from a sanctioned entity | Evidence only |
| 0.1% of volume, five hops away | Evidence only |

The first is close to a direct relationship. The second is background noise that almost every address with
exchange history carries.

### The rule

An **indirect** finding whose risk type is in `AlwaysBlockIndicators`, **within** `IndirectReviewMaxHops`
**and at or above** `IndirectReviewMinPercent`, is raised to `Review`.

**Never to `Block`.** Block stays reserved for a direct designation, which is a legal fact about the address
rather than a matter of degree. Proximity is a gradient, so the most it justifies is putting a person on it.

**Both conditions must hold.** Distance alone would flag nearly every address with exchange history, which is
the failure the rule exists to avoid rather than to cause. Weight alone would flag an address whose entire
history traces to something bad fifteen removes away, which says more about the shape of the network than
about the address.

**It can only raise a clean result.** The score thresholds are evaluated first, so a direct designation still
blocks and a high score still blocks; enabling proximity can never soften either into a Review.

It reuses `AlwaysBlockIndicators` rather than taking a second list, so the two cannot drift apart. The rule
reads as: the findings that would block outright if they described this address get a human look when they
are merely close.

### Configuration

```jsonc
"Compliance": {
  "IndirectReviewMaxHops":    0,   // 0 disables the rule entirely
  "IndirectReviewMinPercent": 0    // share of volume, 0-100
}
```

The thresholds in force are stamped onto every evidence row
(`indirect_review<=3hops>=1pct`), so a decision taken under one setting stays explainable after the setting
changes.

### Do not guess the numbers

Any values chosen before there is real data would be invented, and both ways of being wrong are bad. Set them
loosely and every payout queues for staff, which trains people to approve without looking and makes the
control worse than nothing. Set them tightly and they never fire, which is the state before the rule existed.

Every screening stores the full provider payload, so the evidence needed to choose them is already being
collected. Once real payout volume has run through with screening enabled, measure the hop and percentage
distribution of actual destinations and set the thresholds where they separate a workable review queue from
the background.

**One reference point from live data:** an ordinary TRX address, which the vendor scores 3 out of 100, reads
`sanctioned_entity` at **2.735% of volume, three hops** out through an exchange. Any threshold that catches
that will catch most of your legitimate destinations.

### Verified against the live API

With the rule off, that address screens `Allow`. With it set to 3 hops and 1%, the same address on the same
unchanged score of 3 screens `Review` — so the rule, and not the score, is what moved it. The evidence row
carries the thresholds that produced the decision.

---

## 14. Setting the thresholds from the back office

Built. The tuning knobs are editable through the API; the master switches are not, and that split is the
design.

### What is editable, and what is not

| | Where it lives | Why |
|---|---|---|
| Block and review scores, cache days, hop limit, volume floor, extra designations | **API** | Calibration. These are meant to be adjusted from measurement, often. |
| `Compliance:Enabled`, `Withdrawal:Screening:Enabled`, `Merchant:Screening:*`, `Wallet:Screening:Enabled`, `Sweep:Screening:Enabled`, `Treasury:Screening:ScreenCollectionWallets` | **Configuration** | Existence. |

The thresholds decide how a control is calibrated. The switches decide whether it runs at all. A stolen
admin session must not be able to silently switch off the gate that holds money, and keeping that in
configuration means it takes infrastructure access rather than a browser tab. The read endpoint names those
keys explicitly, so a settings screen can say why the switch is not there instead of leaving an operator
hunting for it.

### Endpoints

```
GET /api/v1/ops/compliance/policy           ops.compliance.view
GET /api/v1/ops/compliance/policy/history   ops.compliance.view
PUT /api/v1/ops/compliance/policy           ops.compliance.manage
```

Reading is a View right; changing is not. A threshold change decides whether money moves, so it sits behind
the same code as spending provider quota.

### Configuration is the floor, a saved version is the override

Configuration alone would mean every tuning change is a deployment, which is untenable for numbers meant to
be set from measurement. The database alone would mean a fresh environment starts with no policy at all, and
"no policy" on a control that decides whether money moves is the worst possible default.

So the configured values always apply until staff deliberately save a version. `source` on the read-back says
which is in force — `Configuration` or `Stored`. That distinction matters: "nobody has set this" and "someone
set it to exactly the default" are otherwise indistinguishable, and only one of them is a question worth
asking. The configured defaults stay visible beside the current values, so staff can see what they changed.

### The designation list is add-only

Staff may add designations. They cannot remove what the platform ships with, no matter what they save. A
sanctions override is exactly the rule that should not come off in a web form. `editableIndicators` is the
subset that can be taken away again; `alwaysBlockIndicators` is everything in force.

### Append-only, and attributed

Every change inserts a version. Nothing is updated in place, for the same reason the evidence trail is
append-only: a payout allowed last month has to stay explainable against the thresholds that were actually in
force, which a mutable settings row destroys. The evidence row already stamps the policy text at decision
time; this table says who chose it and when.

`updatedBy` comes from the validated session and never from the request body — a compliance change that
records an attribution the caller supplied is not an attribution. A change also writes a warning-level log
line, so it is not discoverable only by knowing which table to look in.

### How a change reaches the workers

The ops host serves the API; the money host runs the screening workers. A resolved policy is cached for **30
seconds**, so a change saved on one host reaches the other within that window without a restart. The host
that made the change drops its own cache immediately. The window is deliberately short: a stale threshold
matters only for the seconds after a deliberate change, and the alternative is a database read per screened
address, which buys nothing.

### Validation, and why each bound exists

| Rule | Code | Reason |
|---|---|---|
| Scores 1-100 | `compliance.invalid_score` | |
| Review at or below block | `compliance.review_above_block` | A higher review floor means nothing ever reaches review — everything qualifying was already refused. Silently useless is worse than refused. |
| Cache 1-365 days | `compliance.invalid_cache_days` | Zero re-screens every address on sight and exhausts the daily quota. |
| Hops 0-10 | `compliance.invalid_hops` | Zero disables the proximity rule. Beyond ten, a finding describes the network rather than the address, so a larger limit only looks cautious. |
| Percent 0-100 | `compliance.invalid_percent` | |
| Attribution required | `compliance.unattributed_change` | A threshold change is a compliance act. |

A refused update persists nothing.

### Verified over HTTP

On a booted Ops host: the policy read `Configuration` before anything was saved; a PUT setting the hop limit
to 2 at 5% with an added designation came back `Stored`, attributed to the session user, with the shipped
designations intact and only the added one editable; all six validation rules returned their codes at 400;
the history showed the version with its note; and a subsequent screening stamped
`indirect_review<=2hops>=5.00pct` onto its evidence row — the saved policy reaching the decision path, not
just the settings screen.

---

## 15. Current verdicts and the batch lookup

Built for the admin portal's REQ-26. Two reads over the evidence trail that answer "what is true now", beside
the history list that answers "what was recorded".

### The defect they fix

`GET /ops/compliance/screenings?decision=X` filters **rows**. An address blocked and later re-screened clean
still matches `decision=Block` through its old row, forever, and `totalCount` counts an address once per
screening. That is correct for an audit trail and wrong for a work queue, which would never empty.

### The two reads

| Route | Returns |
|---|---|
| `GET /api/v1/ops/compliance/addresses` | One row per chain and address, at its latest verdict, filterable by decision, purpose, chain and staleness |
| `POST /api/v1/ops/compliance/screenings/latest` | The latest verdict for up to 200 given addresses on one chain, `null` for never screened |

Both need `ops.compliance.view`, read stored evidence only, and spend no quota.

### One definition of "latest"

Newest `ScreenedAt`, then highest `Seq`. The current list, the batch lookup, the counters and the cache probe
the payout gate uses all apply it, so none of them can pick a different verdict for the same address.

**`Seq`, not `Id`.** The request asked for `Id` as the tie-breaker. SQL Server orders a `uniqueidentifier` by
its last six bytes first, so ordering by `Id` is deterministic but unrelated to the order rows were written —
even for version-7 GUIDs, whose time component sits in the first bytes. `Seq` is the clustered identity, which
is insertion order and therefore what "latest" actually means. The cache probe previously ordered by
`ScreenedAt` alone; it now uses the same rule.

### The rules

- **Reduce first, filter second.** Every filter applies to an address's latest row.
- **`purpose` selects addresses, not the verdict row.** An address is included if it was ever screened for
  that purpose; its verdict is still its latest row. Otherwise re-screening a deposit address from the payout
  screen would drop it from the deposit queue.
- **`stale`** means `FreshUntil` is null or past. A null is a failed screening, never reused, so always due.
- **`totalCount` counts addresses.**
- **`summary` uses every filter except `decision`.** Counters beside a narrowed list describe the addresses it
  is drawn from; honouring `decision` would collapse them to one bucket.
- **Batch lookup:** one entry per distinct address in request order, echoed as sent. Exact duplicates
  collapse, case variants do not (TRON Base58 is case-sensitive; matching still goes through the column's
  case-insensitive collation). `null` means never screened only. More than 200 is refused with
  `ops.too_many_addresses`, never truncated. A missing `addresses` field is refused rather than read as empty.

### How it is queried, and what it costs

The reduction is a NOT EXISTS — no row for the same chain and address that is newer — which stays a plain row
set, so filtering, counting and paging compose on it in SQL. It seeks on
`IX_AddressScreening_Chain_Address_ScreenedAt`. The counters were already running an equivalent aggregate on
every list request, so the database was paying this cost before.

The table is append-only and grows with every re-screen, so this becomes a larger scan as history accumulates.
If that is ever measured as a problem, a maintained latest-verdict-per-address table written in the same
transaction as each evidence row would serve both reads and the counters. Not built until a need is measured.

### Verified

Ten SQL Server tests: a cleared address leaves the current Block list while the history list still returns it;
addresses counted, not rows; purpose including an address whose latest row carries another purpose; stale
under two clocks; the summary ignoring decision but honouring chain; a timestamp tie resolving to the row
written last in the current list, the counters, the batch lookup and the cache probe alike; request-order
echo with `null` for never screened and a case variant resolving; exact-duplicate collapse; empty and blank
input; chain scoping.

Twenty-one checks over HTTP on a booted Ops host, including the 200 cap refusing 201 and accepting 200, the
missing-field and missing-chain refusals, an unparseable `stale` returning the enveloped 400, a numeric
`decision` refused, and 403 on both routes for a user without `ops.compliance.view`. The dev database held no
address whose old `Unavailable` row had been superseded, so that specific case rests on the SQL test.

---

## 16. Taint segregation — routing a sweep by the deposit address's verdict

Built 2026-09-18. The first place a screening verdict decides **where money goes** rather than whether it moves.

### The problem it solves

§12 screens our own deposit addresses and flags the ones that received something worth investigating. That was
the end of it: the balance was then swept into the same cold treasury as everything else. Once two balances
share an address they cannot be separated again — everything that later leaves that address carries the taint —
so a single flagged deposit contaminated the whole clean treasury, permanently, and no later analysis could
undo it.

### How it works

A chain now has **two** cold collection wallets: a `Safe` one and a `Danger` one (`ColdWalletKind`). Before a
sweep is created, `SweepScanService` resolves the source deposit address's verdict and routes on it:

| Verdict | Destination |
|---|---|
| `Allow` | Safe collection wallet |
| `Review` or `Block` | Danger (quarantine) collection wallet |
| No fresh verdict, or `Unavailable` | `Sweep:Screening:OnUnavailable` — `Hold` (default), `Safe`, or `Danger` |

`Review` quarantines alongside `Block` deliberately. Segregating a clean balance is reversible — a human moves
it out — while mixing a tainted one into clean treasury is not reversible at all, and that asymmetry decides it.

**A flagged balance is never swept to the Safe wallet as a fallback.** With no Danger wallet registered the
sweep is simply not created; the balance stays on the deposit address, which the platform also controls. The
same holds when screening is enabled but no provider is composed: it logs an error and holds, rather than
quietly sweeping everything into clean treasury on the strength of a missing dependency.

### Cost control

The scan runs over every funded deposit address, and it shares its daily quota with the payout gate — the
control that actually holds money. Two things bound it:

- Only addresses **already over the sweep threshold** are screened. Everything else is skipped before any
  screening question is asked.
- `MaxScreeningsPerPass` (default 25) caps live calls per pass. The pass asks
  `FindAddressesNeedingScreeningAsync` — one indexed query — which of its candidates lack a fresh verdict,
  screens at most that many, and serves the rest from stored evidence. Addresses over the budget follow
  `OnUnavailable` and are picked up next pass.

With §12's scheduled deposit-address sweep running, most addresses already carry a fresh verdict by the time
they are worth sweeping, so a pass usually spends nothing.

### What is recorded

Each sweep row carries `DestinationKind`, `ScreeningId` and `ScreeningDecision`, snapshotted at creation. A
later re-screen of the same address must never appear to change what a past sweep was judged on — the same rule
the payout gate follows. The ops sweep list surfaces all three, and `screeningId` deep-links to the evidence.

### The collection wallets themselves

They are screened on registration under a new purpose, `ScreeningPurpose.ColdCollectionWallet`, and the verdict
is stored on the wallet row so a staff screen shows it without spending quota. **It never refuses anything.**
The Danger wallet is *expected* to score badly — it collects tainted funds by design — so treating a bad verdict
as a disqualification would disable the segregation precisely when it is working. A flagged Safe wallet returns
a warning for a human to look at; a flagged Danger wallet returns nothing at all, because an alarm nobody should
act on is how real alarms get ignored.

Note the deliberate contrast with §9: a merchant settlement wallet the provider **directly designates** cannot
be made the active cash-out destination. There the address is a payout destination a human chose; here it is a
quarantine bucket the platform chose on purpose.

### Configuration

```jsonc
"Sweep": {
  "Screening": {
    "Enabled": false,              // default: every sweep goes to the Safe wallet, exactly as before
    "OnUnavailable": "Hold",       // Hold | Safe | Danger
    "MaxScreeningsPerPass": 25
  }
}
```

Separate from `Compliance:*` and from the other consumers' switches, for the reason every one of them is
separate: Compliance answers "how risky is this address", each consumer answers "what do I do about it". Here
the answer is a routing decision, which is a different question from the payout gate's.

---

## 17. Still not built

**Per-merchant policy.** Thresholds are global config. Per-merchant risk appetite waits for real hit-rate
numbers.

**Moving funds out of quarantine.** The Danger collection wallet accumulates; nothing in this system spends
from it, and nothing tracks which deposit a quarantined balance came from beyond the sweep rows themselves.
Both are deliberate for now — the key is not in the system, so any movement is a human act — but a report that
totals quarantined funds per source would be the natural next step.

**Re-routing after the fact.** A deposit address that screens clean today and badly tomorrow has already had
its earlier balances swept to the Safe wallet. That is inherent: the decision can only be made with the
information available at sweep time.

---

## 18. Related

- `db/README.md` — the migration and `db/sql` drift rules; `150-compliance.sql` is this module's script
- `docs/backoffice-frontend-integration.md` — the Ops API surface
