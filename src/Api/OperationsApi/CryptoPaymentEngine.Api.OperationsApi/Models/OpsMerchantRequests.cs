using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class CreateMerchantRequest
{
    [Required, MaxLength(64)] public string MerchantCode { get; init; } = null!;
    [Required, MaxLength(256)] public string Name { get; init; } = null!;
    [Url] public string? CallbackUrl { get; init; }
}

public sealed class SetMerchantStatusRequest
{
    [Required] public bool Active { get; init; }
}

public sealed class UpdateAllowedIpsRequest
{
    [Required] public List<string> IpAddresses { get; init; } = [];
}
