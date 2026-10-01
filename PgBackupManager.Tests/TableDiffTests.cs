using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Services;

namespace PgBackupManager.Tests;

public class TableDiffTests
{
    private static string? N(object? v, CanonicalType t, bool trim = true) => TableDiffRunner.Norm(v, t, trim);

    [Fact]
    public void Decimal_ScaleDoesNotMatter() => Assert.Equal(N(1.5m, CanonicalType.Decimal), N(1.5000m, CanonicalType.Decimal));

    [Fact]
    public void Decimal_RealDifferenceKept() => Assert.NotEqual(N(10.5m, CanonicalType.Decimal), N(10.5001m, CanonicalType.Decimal));

    [Fact]
    public void Bool_FromIntOrString() => Assert.Equal(N(true, CanonicalType.Bool), N(1, CanonicalType.Bool));

    [Fact]
    public void Int_FromDecimalAndLong() => Assert.Equal(N(42L, CanonicalType.Int32), N(42m, CanonicalType.Int32));

    [Fact]
    public void Date_MidnightTimestampEqualsDate() =>
        Assert.Equal(N(new DateTime(2024, 1, 2), CanonicalType.Date), N("2024-01-02", CanonicalType.Date));

    [Fact]
    public void Text_TrailingSpacesIgnoredByDefault()
    {
        Assert.Equal(N("C1    ", CanonicalType.String), N("C1", CanonicalType.String));
        Assert.NotEqual(N("C1  ", CanonicalType.String, trim: false), N("C1", CanonicalType.String, trim: false));
    }

    [Fact]
    public void Null_IsNotEmptyString()
    {
        Assert.Null(N(null, CanonicalType.Text));
        Assert.Null(N(DBNull.Value, CanonicalType.Text));
        Assert.Equal("", N("", CanonicalType.Text));
    }

    [Fact]
    public void Guid_CaseInsensitive() =>
        Assert.Equal(N(Guid.Parse("A0B1C2D3-0000-0000-0000-000000000001"), CanonicalType.Guid), N("a0b1c2d3-0000-0000-0000-000000000001", CanonicalType.Guid));

    [Fact]
    public void Double_WholeNumberMatchesDecimal() => Assert.Equal(N(3.0, CanonicalType.Float64), N(3m, CanonicalType.Float64));
}
