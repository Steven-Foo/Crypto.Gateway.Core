/*───────────────────────────────────────────────────────────────────────────────
  Crypto Payment Engine — MongoDB bootstrap
  Idempotent: safe to re-run. Creates collections, JSON-Schema validators, indexes.

  Usage:
    mongosh "mongodb://localhost:27017" --file db/mongo/00-bootstrap.js
    mongosh "<atlas-uri>" --file db/mongo/00-bootstrap.js

  ── Two rules this script enforces structurally ──────────────────────────────
  1. MongoDB is NEVER a source of truth for money. It stores external/blockchain
     state only. Balances are derived from the SQL ledger, never from here.
  2. Amounts are stored as STRING base units, never as `double`. BSON doubles are
     IEEE-754 binary floats and cannot represent 1 wei exactly. The validators below
     reject numeric amount fields outright (CLAUDE.md §14).

  No mnemonic, seed, private key or secret is ever written to MongoDB.
───────────────────────────────────────────────────────────────────────────────*/

const DB_NAME = "cryptopaymentengine";
const RPC_LOG_TTL_DAYS = 30;
const WEBHOOK_LOG_TTL_DAYS = 90;
const RESOURCE_HISTORY_TTL_DAYS = 180;

const database = db.getSiblingDB(DB_NAME);

/** Create the collection if absent, otherwise apply the validator to the existing one. */
function ensureCollection(name, validator) {
  const exists = database.getCollectionNames().includes(name);
  if (!exists) {
    database.createCollection(name, validator ? { validator } : {});
    print(`  created collection ${name}`);
  } else if (validator) {
    database.runCommand({ collMod: name, validator, validationLevel: "moderate" });
    print(`  updated validator on ${name}`);
  }
}

function ensureIndexes(name, indexes) {
  indexes.forEach((ix) => database.getCollection(name).createIndex(ix.keys, ix.options || {}));
}

/** A base-unit amount: digits only, up to 78 chars (uint256 max). Never a number. */
const baseUnitString = {
  bsonType: "string",
  pattern: "^[0-9]{1,78}$",
  description: "base-unit amount as a decimal string; numeric types are forbidden",
};

const chainEnum = { enum: ["Tron", "Ethereum", "Solana"] };

print(`Bootstrapping MongoDB database '${DB_NAME}'...`);

/*── Wallet snapshots: latest on-chain balance. Derived, never authoritative. ──*/
ensureCollection("WalletSnapshot", {
  $jsonSchema: {
    bsonType: "object",
    required: ["walletId", "address", "chain", "updatedAt"],
    properties: {
      walletId: { bsonType: "string" },
      address: { bsonType: "string" },
      chain: chainEnum,
      nativeBalance: baseUnitString,
      tokenBalances: {
        bsonType: "array",
        items: {
          bsonType: "object",
          required: ["assetId", "balance"],
          properties: { assetId: { bsonType: "string" }, balance: baseUnitString },
        },
      },
      updatedAt: { bsonType: "date" },
    },
  },
});
ensureIndexes("WalletSnapshot", [
  { keys: { walletId: 1 }, options: { unique: true, name: "ux_walletId" } },
  { keys: { chain: 1, address: 1 }, options: { unique: true, name: "ux_chain_address" } },
  { keys: { updatedAt: -1 }, options: { name: "ix_updatedAt" } },
]);

/*── Raw transactions. The dedup key mirrors the SQL deposit UNIQUE constraint. ─*/
ensureCollection("BlockchainTransaction", {
  $jsonSchema: {
    bsonType: "object",
    required: ["chain", "txHash", "blockNumber"],
    properties: {
      chain: chainEnum,
      txHash: { bsonType: "string" },
      blockNumber: { bsonType: "long" },
      amount: baseUnitString,
      contractAddress: { bsonType: ["string", "null"] },
      status: { bsonType: "string" },
      confirmations: { bsonType: "int" },
      raw: { bsonType: "object" },
    },
  },
});
ensureIndexes("BlockchainTransaction", [
  { keys: { chain: 1, txHash: 1 }, options: { unique: true, name: "ux_chain_txHash" } },
  { keys: { blockNumber: -1 }, options: { name: "ix_blockNumber" } },
]);

ensureCollection("TransactionReceipt", {
  $jsonSchema: {
    bsonType: "object",
    required: ["chain", "txHash"],
    properties: {
      chain: chainEnum,
      txHash: { bsonType: "string" },
      receipt: { bsonType: "object" },
      logs: { bsonType: "array" },
      energyUsage: { bsonType: ["long", "null"] },
      bandwidthUsage: { bsonType: ["long", "null"] },
    },
  },
});
ensureIndexes("TransactionReceipt", [
  { keys: { chain: 1, txHash: 1 }, options: { unique: true, name: "ux_chain_txHash" } },
]);

/*── Contract events: (txHash, logIndex) is the on-chain dedup identity. ───────*/
ensureCollection("ContractEvent", {
  $jsonSchema: {
    bsonType: "object",
    required: ["chain", "transactionHash", "logIndex"],
    properties: {
      chain: chainEnum,
      contract: { bsonType: "string" },
      event: { bsonType: "string" },
      block: { bsonType: "long" },
      transactionHash: { bsonType: "string" },
      logIndex: { bsonType: "int" },
      data: { bsonType: "object" },
    },
  },
});
ensureIndexes("ContractEvent", [
  { keys: { chain: 1, transactionHash: 1, logIndex: 1 }, options: { unique: true, name: "ux_chain_tx_logIndex" } },
  { keys: { contract: 1, event: 1, block: -1 }, options: { name: "ix_contract_event_block" } },
]);

/*── Blocks: parentHash is what makes reorg detection possible. ────────────────*/
ensureCollection("Block", {
  $jsonSchema: {
    bsonType: "object",
    required: ["chain", "blockNumber", "hash"],
    properties: {
      chain: chainEnum,
      blockNumber: { bsonType: "long" },
      hash: { bsonType: "string" },
      parentHash: { bsonType: "string" },
      timestamp: { bsonType: "date" },
      transactions: { bsonType: "array" },
    },
  },
});
ensureIndexes("Block", [
  { keys: { chain: 1, blockNumber: -1 }, options: { unique: true, name: "ux_chain_blockNumber" } },
  { keys: { chain: 1, hash: 1 }, options: { unique: true, name: "ux_chain_hash" } },
]);

/*── Address metadata. Public data only — no key material, ever. ───────────────*/
ensureCollection("AddressMetadata", {
  $jsonSchema: {
    bsonType: "object",
    required: ["chain", "address"],
    properties: {
      walletId: { bsonType: "string" },
      chain: chainEnum,
      address: { bsonType: "string" },
      permissions: { bsonType: "object" },
      activated: { bsonType: "bool" },
      accountType: { bsonType: "string" },
      raw: { bsonType: "object" },
    },
  },
});
ensureIndexes("AddressMetadata", [
  { keys: { chain: 1, address: 1 }, options: { unique: true, name: "ux_chain_address" } },
  { keys: { walletId: 1 }, options: { name: "ix_walletId" } },
]);

/*── Operational logs, TTL-expired. RpcLog must never contain auth headers. ────*/
ensureCollection("RpcLog", {
  $jsonSchema: {
    bsonType: "object",
    required: ["provider", "method", "createdAt"],
    properties: {
      provider: { bsonType: "string" },
      method: { bsonType: "string" },
      request: { bsonType: "object" },
      response: { bsonType: "object" },
      durationMs: { bsonType: "int" },
      success: { bsonType: "bool" },
      createdAt: { bsonType: "date" },
    },
  },
});
ensureIndexes("RpcLog", [
  { keys: { createdAt: 1 }, options: { name: "ttl_createdAt", expireAfterSeconds: RPC_LOG_TTL_DAYS * 86400 } },
  { keys: { provider: 1, method: 1, createdAt: -1 }, options: { name: "ix_provider_method" } },
  { keys: { success: 1, createdAt: -1 }, options: { name: "ix_failures", partialFilterExpression: { success: false } } },
]);

/*── Inbound provider callbacks. Verified + deduped before any side effect. ────*/
ensureCollection("WebhookLog", {
  $jsonSchema: {
    bsonType: "object",
    required: ["provider", "createdAt"],
    properties: {
      provider: { bsonType: "string" },
      event: { bsonType: "string" },
      payload: { bsonType: "object" },
      signatureVerified: { bsonType: "bool" },
      dedupKey: { bsonType: "string" },
      processed: { bsonType: "bool" },
      createdAt: { bsonType: "date" },
    },
  },
});
ensureIndexes("WebhookLog", [
  { keys: { createdAt: 1 }, options: { name: "ttl_createdAt", expireAfterSeconds: WEBHOOK_LOG_TTL_DAYS * 86400 } },
  // Replay protection: a provider may deliver the same event many times.
  { keys: { provider: 1, dedupKey: 1 }, options: { unique: true, name: "ux_provider_dedupKey", partialFilterExpression: { dedupKey: { $type: "string" } } } },
  { keys: { processed: 1, createdAt: -1 }, options: { name: "ix_unprocessed", partialFilterExpression: { processed: false } } },
]);

/*── TRON resource tracking (Blockchain/Resources; future Energy module). ──────*/
ensureCollection("WalletResource", {
  $jsonSchema: {
    bsonType: "object",
    required: ["walletId", "updatedAt"],
    properties: {
      walletId: { bsonType: "string" },
      energy: { bsonType: "long" },
      bandwidth: { bsonType: "long" },
      frozenTrx: baseUnitString,
      delegatedEnergy: { bsonType: "long" },
      delegatedBandwidth: { bsonType: "long" },
      updatedAt: { bsonType: "date" },
    },
  },
});
ensureIndexes("WalletResource", [{ keys: { walletId: 1 }, options: { unique: true, name: "ux_walletId" } }]);

ensureCollection("WalletResourceHistory", {
  $jsonSchema: {
    bsonType: "object",
    required: ["walletId", "timestamp"],
    properties: {
      walletId: { bsonType: "string" },
      energy: { bsonType: "long" },
      bandwidth: { bsonType: "long" },
      frozenTrx: baseUnitString,
      delegatedEnergy: { bsonType: "long" },
      timestamp: { bsonType: "date" },
    },
  },
});
ensureIndexes("WalletResourceHistory", [
  { keys: { walletId: 1, timestamp: -1 }, options: { name: "ix_walletId_timestamp" } },
  { keys: { timestamp: 1 }, options: { name: "ttl_timestamp", expireAfterSeconds: RESOURCE_HISTORY_TTL_DAYS * 86400 } },
]);

ensureCollection("EnergyDelegation", {
  $jsonSchema: {
    bsonType: "object",
    required: ["fromWalletId", "toWalletId", "createdAt"],
    properties: {
      fromWalletId: { bsonType: "string" },
      toWalletId: { bsonType: "string" },
      energy: { bsonType: "long" },
      frozenTrx: baseUnitString,
      status: { bsonType: "string" },
      createdAt: { bsonType: "date" },
    },
  },
});
ensureIndexes("EnergyDelegation", [
  { keys: { fromWalletId: 1, toWalletId: 1, createdAt: -1 }, options: { name: "ix_from_to_created" } },
  { keys: { status: 1 }, options: { name: "ix_status" } },
]);

print("");
print(`MongoDB bootstrap complete. Collections: ${database.getCollectionNames().sort().join(", ")}`);
print("Reminder: Mongo holds blockchain state only. Money truth lives in the SQL ledger.");
