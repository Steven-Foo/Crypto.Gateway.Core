using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

public static class MerchantRoleErrors
{
    public static readonly Error MerchantRequired =
        Error.Validation("merchant_role.merchant_required", "A merchant is required.");

    public static readonly Error NameRequired =
        Error.Validation("merchant_role.name_required", "A role name is required.");

    public static readonly Error NotFound =
        Error.NotFound("merchant_role.not_found", "Role not found.");

    public static readonly Error NameAlreadyExists =
        Error.Conflict("merchant_role.name_already_exists", "A role with this name already exists.");

    public static readonly Error InUse =
        Error.Conflict("merchant_role.in_use", "This role is still assigned to one or more accounts.");
}
