using System.Text.Json.Serialization;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// Response of <c>/wallet/getaccountresource</c>. TRON <b>omits zero-valued fields</b>, so every property
/// defaults to 0 when absent — never assume a field is present. Energy/bandwidth are their own units;
/// bandwidth is split into a free daily allotment (<c>freeNet*</c>) and a staked portion (<c>Net*</c>).
/// </summary>
public sealed record TronAccountResourceDto
{
    [JsonPropertyName("EnergyLimit")] public long EnergyLimit { get; init; }
    [JsonPropertyName("EnergyUsed")] public long EnergyUsed { get; init; }

    [JsonPropertyName("freeNetLimit")] public long FreeNetLimit { get; init; }
    [JsonPropertyName("freeNetUsed")] public long FreeNetUsed { get; init; }

    [JsonPropertyName("NetLimit")] public long NetLimit { get; init; }
    [JsonPropertyName("NetUsed")] public long NetUsed { get; init; }
}

/// <summary>
/// Response of <c>/wallet/getaccount</c> (only the fields we need). <c>balance</c> is the spendable TRX in
/// sun (absent ⇒ 0); <c>frozenV2</c> lists staked positions by resource type (a missing <c>type</c> means
/// BANDWIDTH). A brand-new account returns <c>{}</c>, which maps to an all-zero snapshot.
/// </summary>
public sealed record TronAccountDto
{
    [JsonPropertyName("balance")] public long Balance { get; init; }
    [JsonPropertyName("frozenV2")] public List<TronFrozenV2Dto> FrozenV2 { get; init; } = [];
}

/// <summary>One <c>frozenV2</c> entry: <c>amount</c> in sun; <c>type</c> "ENERGY" or absent (BANDWIDTH).</summary>
public sealed record TronFrozenV2Dto
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("amount")] public long Amount { get; init; }
}

/// <summary>
/// Request body for <c>/wallet/freezebalancev2</c>. Freezes <see cref="FrozenBalance"/> sun of the owner's
/// own TRX to acquire <see cref="Resource"/> (ENERGY). Frozen TRX is not spent — it's recoverable via
/// unstake. Addresses are the 21-byte <c>41…</c> hex form (<c>visible=false</c>).
/// </summary>
public sealed record FreezeBalanceV2Request
{
    [JsonPropertyName("owner_address")] public required string OwnerAddress { get; init; }
    [JsonPropertyName("frozen_balance")] public required long FrozenBalance { get; init; }
    [JsonPropertyName("resource")] public string Resource { get; init; } = "ENERGY";
    [JsonPropertyName("visible")] public bool Visible { get; init; }
}

/// <summary>
/// Request body for <c>/wallet/delegateresource</c>. Delegates <see cref="Balance"/> sun of the owner's
/// staked balance's <see cref="Resource"/> (ENERGY) to <see cref="ReceiverAddress"/>. Reclaimable via
/// undelegate — not spent. <see cref="Lock"/> false so it can be reclaimed immediately.
/// </summary>
public sealed record DelegateResourceRequest
{
    [JsonPropertyName("owner_address")] public required string OwnerAddress { get; init; }
    [JsonPropertyName("receiver_address")] public required string ReceiverAddress { get; init; }
    [JsonPropertyName("balance")] public required long Balance { get; init; }
    [JsonPropertyName("resource")] public string Resource { get; init; } = "ENERGY";
    [JsonPropertyName("lock")] public bool Lock { get; init; }
    [JsonPropertyName("visible")] public bool Visible { get; init; }
}
