using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Preferences;
using KaiZent.Platform.Data;

namespace KaiZent.Platform.Tests;

public sealed class ThemeCatalogTests
{
    [Fact]
    public void Contains_all_devexpress_standard_schemes()
    {
        // 11 Fluent-Akzentfarben hell und dunkel, dazu 4 klassische Themes
        Assert.Equal(26, ThemeCatalog.All.Count);
        Assert.Equal(ThemeCatalog.All.Count, ThemeCatalog.All.Select(t => t.Key).Distinct().Count());
        Assert.True(ThemeCatalog.IsKnown(ThemeCatalog.Default));
        Assert.Contains(ThemeCatalog.All, t => t.Key == "fluent-dark-mint" && t.IsDark && t.Accent == "Mint");
        Assert.Contains(ThemeCatalog.All, t => t.Key == "classic-office-white" && t.Family == ThemeFamily.Classic);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fluent-light-neon")]
    [InlineData("<script>")]
    public void Unknown_keys_resolve_to_default(string? key) => Assert.Equal(ThemeCatalog.Default, ThemeCatalog.Resolve(key).Key);
}

public sealed class ValidationTests
{
    [Theory]
    [InlineData("kurz1", false)]
    [InlineData("nurbuchstabenlang", false)]
    [InlineData("123456789012", false)]
    [InlineData("Hofladen2026x", true)]
    public void Password_rule(string password, bool ok) => Assert.Equal(ok, Validation.IsStrongPassword(password));

    [Theory]
    [InlineData("a@b.de", true)]
    [InlineData(" a@b.de", false)]
    [InlineData("kein-at.de", false)]
    [InlineData("a@localhost", false)]
    public void Email_rule(string email, bool ok) => Assert.Equal(ok, Validation.IsEmail(email));
}

public sealed class TenantDatabaseTests
{
    [Fact]
    public void Names_are_checked_strictly()
    {
        var dbs = new TenantDatabases("lms_farm_");
        var id = Guid.NewGuid();
        Assert.Equal("lms_farm_" + id.ToString("N"), dbs.NameFor(id));
        Assert.True(dbs.IsValidName(dbs.NameFor(id)));
        Assert.False(dbs.IsValidName("lms_farm_x\"; drop database postgres; --"));
        Assert.False(dbs.IsValidName("vms_practice_" + id.ToString("N")));
    }

    [Theory]
    [InlineData("LMS_")]
    [InlineData("lms")]
    [InlineData("1lms_")]
    [InlineData("lms-farm_")]
    public void Invalid_prefixes_are_refused(string prefix) => Assert.Throws<ArgumentException>(() => new TenantDatabases(prefix));
}

public sealed class SecretBoxTests
{
    [Fact]
    public void Roundtrip_and_tamper_detection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kzp_keys_" + Guid.NewGuid().ToString("N"));
        try
        {
            var box = SecretBox.LoadOrCreate(dir);
            var secret = box.Protect("geheim");
            Assert.StartsWith("v1:", secret);
            Assert.Equal("geheim", box.Unprotect(secret));
            Assert.Equal(box.Fingerprint, SecretBox.LoadOrCreate(dir).Fingerprint);
            var bytes = Convert.FromBase64String(secret[3..]);
            bytes[^1] ^= 1;
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => box.Unprotect("v1:" + Convert.ToBase64String(bytes)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

public sealed class MigrationTests
{
    [Fact]
    public void Platform_migrations_load()
    {
        Assert.NotEmpty(MigrationSet.PlatformCatalog.Load());
        Assert.NotEmpty(MigrationSet.PlatformTenant.Load());
        // Mandanten-Migrationen dürfen nie den Katalog ansprechen
        Assert.DoesNotContain(MigrationSet.PlatformTenant.Load(), m => m.Sql.Contains("catalog.", StringComparison.OrdinalIgnoreCase));
    }
}
