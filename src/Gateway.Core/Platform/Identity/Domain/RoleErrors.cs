using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

public static class RoleErrors
{
    public static readonly Error NameRequired =
        Error.Validation("role.name_required", "A role name is required.");

    public static readonly Error NameAlreadyExists =
        Error.Conflict("role.name_already_exists", "A role with this name already exists.");

    public static readonly Error NotFound =
        Error.NotFound("role.not_found", "Role not found.");

    public static readonly Error InUse =
        Error.Conflict("role.in_use", "This role is still assigned to one or more staff accounts and cannot be deleted.");
}
