# Dev sample data — a demo portfolio for UI development

Populates a local database with realistic merchants, deposits, and withdrawals so back-office and
merchant-portal UI work has something to render. **Development/Staging only — never production.**

---

## 1. How it works, and why it works that way

The seeder **never writes money rows**. It creates only *inputs*:

- merchants, their pricing and their terms (through the same Application services the staff Ops endpoints call);
- deposit invoices (through the real `IPaymentIntentService`);
- **blocks on the in-memory chain**, carrying transfers to the addresses those invoices were issued;
- withdrawal requests (through the same services the merchant API and portal call).

Everything after that is done by the **real pipeline**: the real scanner detects the transfers, the real
confirmation worker credits them, the real ledger posts the double-entry and fee split, the real matcher
closes the invoice, and the real notifier fires the signed callback.

**Why not just INSERT the rows?** Because a hand-written ledger is a fiction that drifts. Fabricating journal
rows would bypass every invariant the ledger exists to enforce (§14, §15), and it would silently stop
reflecting reality the first time either the schema or the posting logic changed — leaving the UI built
against data the system can no longer produce. The only thing this seeder stands in for is the **node**, at
the same DI seam the in-memory chain source already occupies (§8).

Two consequences follow directly from that choice, and neither is a defect:

- **It needs the in-memory chain** (`Chains:Tron:Live=false`). Under a live node the only way to make a
  deposit appear is to actually send USDT. The seeder detects this and skips with a message.
- **It is not instant.** It waits for the same workers a real deposit waits for — typically 30–60 seconds.

It is **idempotent**: if `DEMOACME` already exists it stops immediately. To re-seed, drop the database.

---

## 2. Running it

```powershell
# 1. Infrastructure: SQL Server + Redis (Docker Compose), Mongo natively (see db/README.md §2)
docker compose up -d sqlserver redis
net start MongoDB          # elevated shell

# 2. Schema — applies all 15 module contexts, and points all three hosts at this database
./tools/dev/Setup-LocalEnv.ps1

# 3. Run the money host with seeding on
$env:DevSampleData__Enabled = "true"
dotnet run --project src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway
```

Or set it permanently in the git-ignored `appsettings.Local.json`:

```json
{ "DevSampleData": { "Enabled": true } }
```

Watch the log — it narrates each stage and prints each merchant's API key.

> ### All three hosts must share one database
>
> The committed `appsettings.Development.json` defaults to **LocalDB**, while `Setup-LocalEnv.ps1` targets the
> **Docker SQL Server on `localhost,1433`**. A host without an override therefore runs against a different,
> near-empty database — and the symptom is not an error, it is a back office that shows **none** of the data
> you just seeded.
>
> The setup script now closes this: it writes `appsettings.Local.json` with the right `Db:ConnectionString` for
> any host that lacks one, and **warns rather than overwrites** if a host already has one pointing elsewhere
> (that file may hold your TronGrid key). If a screen is unexpectedly empty, check this first.

To re-seed from scratch:

```powershell
sqlcmd -S localhost,1433 -U sa -P "Cpe_Dev_Passw0rd!" -C -Q "ALTER DATABASE CryptoPaymentEngine SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE CryptoPaymentEngine;"
./tools/dev/Setup-LocalEnv.ps1
```

---

## 3. What you get

### Merchants

| Code | Terms | Why it exists |
|---|---|---|
| `DEVMERCHANT` | T+0, active | The pre-existing fixed-credential merchant for signed API round-trips |
| `DEMOACME` | T+0, active, 1.00% deposit / 0.50% withdrawal | The healthy baseline |
| `DEMOGLOBE` | **T+1**, active, 1.50% / 0.75%, 50% cash-out cap | Available ≠ settled balance; a capped cash-out |
| `DEMOFROST` | T+2, **Frozen** | A non-Active tenant, *with* history |

**The frozen and T+N merchants exist on purpose.** A UI that has only ever rendered one healthy tenant tends
to get the unhealthy ones wrong — a frozen merchant whose buttons all fail with no explanation, or a
settlement period the balance screen ignores.

### Per merchant

- **6 deposit invoices**: four paid in full, one left unpaid (a genuine `pending` row), one **underpaid** so
  `amountMatched` is false — the case a naive UI renders as a clean success.
- **5 credited deposits**, each with a real ledger journal, the merchant's fee split, and a fired callback.
- **4 withdrawals** spanning the statuses an operator actually has to act on:

| Amount | Lands in | Screen it drives |
|---|---|---|
| 50 USDT | `Approved` → sends → `Confirmed` | The happy path |
| 2,500 USDT | `PendingApproval` | The Ops approval queue (above the 1,000 USDT threshold) |
| 120 USDT | `PendingMerchantApproval` | The **portal** approval queue (two-party rule) |
| 200 USDT cash-out | `Approved` | Merchant earnings withdrawal (`kind=merchant`) |

### Mongo-backed screens

The Energy monitor is given reduced energy readings on two of the monitored platform wallets, so the
resource-health screen shows a spread of numbers instead of N identical rows. Reconciliation snapshots are
written by the real worker every 5 minutes.

> **`health` will still read `Healthy` on every row.** The monitor classifies Healthy/Low/Critical against an
> `EnergyPolicy`, and no policy exists for `HotWithdrawal` wallets — a pre-existing, documented gap (Energy 5a
> only alerts where a policy is configured). The seeder deliberately does **not** invent one: an operational
> threshold the platform has not chosen is worse than a visibly empty one. To exercise the worst-health-first
> ordering, configure a real policy.

> In dev the in-memory balance reader reports **zero** on-chain, so reconciliation reports the full ledger
> holding as drift. That is expected without a live chain adapter — it is not a custody problem.

---

## 4. Ordering note — why terms are applied last

The settlement period and the freeze are applied **after** the withdrawals are seeded, not when the merchant
is created.

Both are gates on *new* activity: a T+N merchant has no settled balance on the day its deposits land, and a
frozen merchant is refused every money-out. Applying them first is perfectly correct — and leaves two of the
three demo merchants with an **empty payout list**, which is a poor demo. Applying them last gives each
merchant a full history *and* its term, which is also the more realistic shape: terms change on established
merchants.

**Nothing is faked by this.** Every gate is still live: a payout submitted against `DEMOGLOBE` from the portal
right now is still correctly refused as `withdrawal.exceeds_settled_balance`, and `DEMOFROST` still refuses
everything. Only their past is populated.

---

## 5. Logging in

**Back office** (`Api/OperationsApi`) — the dev staff account from `Identity:DevSeed`.

**Merchant portal** (`Api/MerchantPortalApi`) — one login per demo tenant:

| Username | Merchant | Password |
|---|---|---|
| `merchant001` | `DEVMERCHANT` | `Merchant@2026` |
| `acme001` | `DEMOACME` | `Merchant@2026` |
| `globe001` | `DEMOGLOBE` | `Merchant@2026` |
| `frost001` | `DEMOFROST` | `Merchant@2026` |

**Sign in as more than one.** A single-tenant dev environment cannot show you a cross-tenant leak, and tenant
isolation is the single most important property of the portal API.

These are documented test credentials, never real ones (§10). The demo merchants' **API signing secrets are
deliberately not logged and not written anywhere** — they are generated randomly by the real registrar. If you
need to sign HMAC requests as a demo merchant, rotate its credential from the back office, or use
`DEVMERCHANT`, whose fixed credentials are in `appsettings.Development.json` for exactly this purpose.

---

## 6. Configuration

| Key | Default | Purpose |
|---|---|---|
| `DevSampleData:Enabled` | `false` | Opt-in. Off by default so an existing dev run is unchanged. |
| `DevSampleData:CallbackUrl` | `http://localhost:51078/dev/callbacks` | Where demo callbacks post — the in-host sink, readable at `GET /dev/callbacks`. |
| `DevSampleData:LedgerWaitSeconds` | `180` | How long to wait for deposits to credit before skipping the withdrawals. |

If the wait expires, the deposits still land — only the withdrawals are skipped, and re-running the host adds
them (the merchants already exist, so nothing is duplicated).

---

## 7. Related

- `docs/backoffice-frontend-integration.md` — Back Office API contract
- `docs/merchant-portal-frontend-integration.md` — Merchant Portal API contract
- `docs/dev-mainnet-deposit.md` — driving a **real** mainnet deposit instead
- `db/README.md` — schema/bootstrap conventions and their drift traps
