using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

public sealed class MerchantUserMap : IEntityTypeConfiguration<MerchantUser>
{
    public void Configure(EntityTypeBuilder<MerchantUser> builder)
    {
        builder.ToTable("MerchantUser");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        // MerchantId is a cross-module reference (the Merchant lives in another module/schema) — an opaque Guid,
        // no FK, so the modules stay independently extractable (§4.5).
        builder.Property(u => u.MerchantId).IsRequired();
        builder.Property(u => u.Username).HasMaxLength(64).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(u => u.RoleId);
        builder.Property(u => u.MustChangePassword).IsRequired();
        builder.Property(u => u.IsPrimary).IsRequired();
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Ignore(u => u.DomainEvents);

        // Global-unique username (login needs no merchant selector); index for the per-tenant account list.
        builder.HasIndex(u => u.Username).IsUnique();
        // Named explicitly — a second index on the exact same column (below) would otherwise silently
        // replace this one rather than coexist with it (the same EF filtered-index gotcha noted elsewhere in
        // this codebase: db/README.md, the Energy top-up index).
        builder.HasIndex(u => u.MerchantId, "IX_MerchantUser_MerchantId");
        builder.HasIndex(u => u.RoleId);

        // At most one primary (super-admin) account per merchant — enforced here, not just by application
        // logic, because MerchantAccountService.CreateAsync's "is this the first account" check is a
        // read-then-write with no lock of its own; this index is the real race-safety arbiter (mirrors the
        // settlement-wallet/cold-wallet "one active per X" pattern elsewhere in the codebase).
        builder.HasIndex(u => u.MerchantId, "UX_MerchantUser_MerchantId_Primary")
            .IsUnique()
            .HasFilter("[IsPrimary] = 1");

        // Intra-module FK (allowed within a module, §4.5) — Restrict so a role cannot be dropped out from
        // under an account even by a direct DB action; the app-level precheck lives in MerchantRoleService.
        builder.HasOne<MerchantRole>().WithMany().HasForeignKey(u => u.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}
