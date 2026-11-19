using GridWise.Api.Identity;

namespace GridWise.Api.Tests;

public sealed class IdentityDomainTests
{
    [Theory]
    [InlineData(OrganizationRole.Admin, OrganizationRole.Operator, true)]
    [InlineData(OrganizationRole.Manager, OrganizationRole.Manager, true)]
    [InlineData(OrganizationRole.Analyst, OrganizationRole.Manager, false)]
    [InlineData(OrganizationRole.Operator, OrganizationRole.Analyst, false)]
    [InlineData((OrganizationRole)99, OrganizationRole.Operator, false)]
    [InlineData((OrganizationRole)0, OrganizationRole.Operator, false)]
    public void Roles_are_ordered_and_unknown_values_grant_nothing(OrganizationRole held, OrganizationRole required, bool expected) =>
        Assert.Equal(expected, held.Satisfies(required));

    [Fact]
    public void Role_names_round_trip_and_match_the_database_constraint()
    {
        foreach (var role in Enum.GetValues<OrganizationRole>())
        {
            Assert.Equal(role, OrganizationRoles.FromName(role.ToName()));
        }
        Assert.Equal(new[] { "operator", "analyst", "manager", "admin" }, OrganizationRoles.Names);
        Assert.Throws<ArgumentOutOfRangeException>(() => OrganizationRoles.FromName("Admin"));
    }

    [Theory]
    [InlineData("harbor-demo", true)]
    [InlineData("site42", true)]
    [InlineData("Harbor", false)]
    [InlineData("-leading", false)]
    [InlineData("double--dash", false)]
    [InlineData("trailing-", false)]
    [InlineData("new\nline", false)]
    [InlineData("ends-with-newline\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Slug_rule_matches_lowercase_kebab_case(string? slug, bool expected) =>
        Assert.Equal(expected, OrganizationSlug.IsValid(slug));

    [Fact]
    public void Slug_longer_than_63_characters_is_rejected()
    {
        Assert.True(OrganizationSlug.IsValid(new string('a', 63)));
        Assert.False(OrganizationSlug.IsValid(new string('a', 64)));
    }

    [Theory]
    [InlineData("user@gridwise.test", "secret", 0)]
    [InlineData(null, null, 2)]
    [InlineData("  ", "", 2)]
    [InlineData("not-an-email", "secret", 1)]
    [InlineData("Name <user@gridwise.test>", "secret", 1)]
    public void Login_request_validation(string? email, string? password, int expectedErrors) =>
        Assert.Equal(expectedErrors, IdentityEndpoints.Validate(new LoginRequest(email, password)).Count);

    [Fact]
    public void Overlong_password_is_rejected_before_hashing()
    {
        var errors = IdentityEndpoints.Validate(new LoginRequest("user@gridwise.test", new string('x', IdentityEndpoints.PasswordMaxLength + 1)));

        Assert.True(errors.ContainsKey("password"));
    }
}
