using ApexERP.Application.Services;
using Xunit;

namespace ApexERP.Tests;

public sealed class ZimbabweTaxpayerTinValidatorTests
{
    [Theory]
    [InlineData("1234567890")]
    [InlineData("TIN-123456")]
    [InlineData("  ZW/123456  ")]
    [InlineData("AB12-CD/34")]
    public void Accepts_non_empty_alphanumeric_tin_with_supported_separators(string tin)
    {
        Assert.True(ZimbabweTaxpayerTinValidator.IsValid(tin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("///")]
    [InlineData("TIN 123")]
    [InlineData("TIN_123")]
    [InlineData("TIN.123")]
    public void Rejects_blank_or_unsupported_tin_values(string? tin)
    {
        Assert.False(ZimbabweTaxpayerTinValidator.IsValid(tin));
    }

    [Fact]
    public void Rejects_tin_longer_than_forty_characters()
    {
        Assert.False(ZimbabweTaxpayerTinValidator.IsValid(new string('A', 41)));
    }

    [Fact]
    public void Accepts_tin_at_maximum_length()
    {
        Assert.True(ZimbabweTaxpayerTinValidator.IsValid(new string('A', 40)));
    }
}
