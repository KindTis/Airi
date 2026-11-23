using System;
using Airi.Domain;
using Airi.Web;
using Xunit;

namespace Airi.Tests;

public sealed class MetadataProductCodeTests
{
    [Theory]
    [InlineData("[group] abp_088 ABP088.mp4", "JUR-459", "ABP-088", false)]
    [InlineData("GsVFgYVFMQgsBLAe.mp4", "review of JUR 459", "JUR-459", false)]
    [InlineData("example heyzo-1234.mp4", "JUR-459", "HEYZO-1234", true)]
    [InlineData("HEYZO_1234 HEYZO-1234.mp4", "", "HEYZO-1234", true)]
    [InlineData("HEYZO-1234 HEYZO_1234.mp4", "", "HEYZO-1234", true)]
    [InlineData("FC2-PPV-1234567.mp4", "", "FC2-PPV-1234567", true)]
    [InlineData("010203_004.mp4", "", "010203-004", true)]
    [InlineData("K12345.mp4", "", "K12345", true)]
    [InlineData("n1234.mp4", "", "N1234", true)]
    [InlineData("AB-12.mp4", "", "AB-12", false)]
    public void FromEntry_PrefersAndNormalizesTheUniqueSupportedCode(string fileName, string title, string value, bool uncensored)
    {
        Assert.Equal(new MetadataProductCode(value, uncensored), MetadataProductCode.FromEntry(Entry(fileName, title)));
    }

    [Theory]
    [InlineData("ABP-088 & JUR-459.mp4", "ABP-088")]
    [InlineData("ABP-088A.mp4", "ABP-088")]
    [InlineData("ABP-088 ABP-088A.mp4", "ABP-088")]
    [InlineData("GsVFgYVFMQgsBLAe.mp4", "ABP-088 or JUR-459")]
    [InlineData("GsVFgYVFMQgsBLAe.mp4", "untitled")]
    [InlineData("prefixABP088suffix.mp4", "No product code")]
    [InlineData("AB12.mp4", "")]
    public void FromEntry_RejectsAmbiguousUnsupportedOrMissingCode(string fileName, string title)
    {
        Assert.Null(MetadataProductCode.FromEntry(Entry(fileName, title)));
    }

    [Theory]
    [InlineData("abp_088", true)]
    [InlineData("ABP088", true)]
    [InlineData("ABP-88", false)]
    [InlineData("ABP-0880", false)]
    [InlineData("ABP-088A", false)]
    [InlineData("title ABP-088", false)]
    [InlineData("ABP-088 and JUR-459", false)]
    public void Matches_RequiresWholeDetailCodeIncludingLeadingZeroes(string detailCode, bool matches)
    {
        Assert.Equal(matches, new MetadataProductCode("ABP-088", false).Matches(detailCode));
    }

    [Fact]
    public void Matches_OnlyUsesSeparatorsAllowedByTheCodeForm()
    {
        Assert.True(new MetadataProductCode("010203-004", true).Matches("010203_004"));
        Assert.False(new MetadataProductCode("010203-004", true).Matches("010203 004"));
        Assert.False(new MetadataProductCode("HEYZO-1234", true).Matches("HEYZO_1234"));
        Assert.False(new MetadataProductCode("AB-12", false).Matches("AB12"));
    }

    [Theory]
    [InlineData("HEYZO_1234.mp4")]
    [InlineData("HEYZO1234.mp4")]
    public void Matches_DoesNotRejectTheCanonicalCodeOfTheGeneralForm(string fileName)
    {
        var product = Assert.IsType<MetadataProductCode>(MetadataProductCode.FromEntry(Entry(fileName, "")));
        Assert.False(product.IsUncensored);
        Assert.Equal("HEYZO-1234", product.Value);
        Assert.True(product.Matches(product.Value));
    }

    private static VideoEntry Entry(string fileName, string title) => new($"./Videos/{fileName}",
        new VideoMeta(title, null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty));
}
