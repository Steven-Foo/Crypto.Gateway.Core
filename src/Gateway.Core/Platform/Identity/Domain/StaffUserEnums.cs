namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// Flat, two-level access for the first pass — no per-module permission matrix (that's APIGateway's
/// Bo/Role/Module/Permission complexity, a deliberate later addition if it's ever needed). Admin can do
/// every mutating Ops action; Viewer is read-only (list/query, never create/edit/fail).
/// </summary>
public enum StaffRole
{
    Admin = 1,
    Viewer = 2,
}
