using GridWise.Api.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GridWise.Api.Data;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.ToTable("organizations", t =>
            t.HasCheckConstraint("ck_organizations_slug", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'"));
        b.HasKey(o => o.Id).HasName("pk_organizations");
        b.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(o => o.Name).HasColumnName("name").HasMaxLength(Organization.NameMaxLength).IsRequired();
        b.Property(o => o.Slug).HasColumnName("slug").HasMaxLength(Organization.SlugMaxLength).IsRequired();
        b.Property(o => o.CreatedAt).HasColumnName("created_at");
        b.Property(o => o.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(o => o.Slug).IsUnique().HasDatabaseName("ux_organizations_slug");
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.ToTable("users", t =>
            t.HasCheckConstraint("ck_users_failed_login_count", "failed_login_count >= 0"));
        b.HasKey(u => u.Id).HasName("pk_users");
        b.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(u => u.Email).HasColumnName("email").HasMaxLength(AppUser.EmailMaxLength).IsRequired();
        b.Property(u => u.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(AppUser.EmailMaxLength).IsRequired();
        b.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(AppUser.DisplayNameMaxLength).IsRequired();
        b.Property(u => u.PasswordHash).HasColumnName("password_hash").IsRequired();
        b.Property(u => u.FailedLoginCount).HasColumnName("failed_login_count");
        b.Property(u => u.LockoutEndsAt).HasColumnName("lockout_ends_at");
        b.Property(u => u.CreatedAt).HasColumnName("created_at");
        b.Property(u => u.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.ToTable("memberships", t =>
            t.HasCheckConstraint("ck_memberships_role", "role IN ('operator', 'analyst', 'manager', 'admin')"));
        b.HasKey(m => m.Id).HasName("pk_memberships");
        b.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(m => m.OrganizationId).HasColumnName("organization_id");
        b.Property(m => m.UserId).HasColumnName("user_id");
        b.Property(m => m.Role)
            .HasColumnName("role")
            .HasMaxLength(16)
            .HasConversion(role => role.ToName(), name => OrganizationRoles.FromName(name));
        b.Property(m => m.CreatedAt).HasColumnName("created_at");
        b.Property(m => m.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(m => new { m.OrganizationId, m.UserId }).IsUnique().HasDatabaseName("ux_memberships_organization_user");
        b.HasIndex(m => m.UserId).HasDatabaseName("ix_memberships_user_id");

        b.HasOne(m => m.Organization)
            .WithMany(o => o.Memberships)
            .HasForeignKey(m => m.OrganizationId)
            .HasConstraintName("fk_memberships_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.User)
            .WithMany(u => u.Memberships)
            .HasForeignKey(m => m.UserId)
            .HasConstraintName("fk_memberships_users_user_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
