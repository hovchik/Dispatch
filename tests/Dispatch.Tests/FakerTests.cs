using System.Globalization;
using System.Text.RegularExpressions;
using Dispatch.Application.Variables;

namespace Dispatch.Tests;

public class FakerTests
{
    private static readonly IReadOnlyDictionary<string, string> NoVariables = new Dictionary<string, string>();

    [Fact]
    public void Offers_more_than_a_hundred_dynamic_variables_and_every_one_generates()
    {
        Assert.True(Faker.Names.Count >= 100, $"only {Faker.Names.Count}");
        var faker = new Faker(new Random(1));
        foreach (var name in Faker.Names)
        {
            var call = name.Contains('(') ? name.Replace("min,max", "1,5").Replace("length", "4").Replace("-30,30", "-1,1")
                .Replace("a,b,c", "x,y").Replace("(n)", "(2)") : name;
            var value = faker.Generate(call);
            Assert.False(string.IsNullOrEmpty(value), $"{call} produced nothing");
        }
    }

    [Theory]
    [InlineData("{{$randomEmail}}", @"^[a-z0-9.]+@[a-z.]+$")]
    [InlineData("{{$randomFullName}}", @"^[A-Z][a-z]+ [A-Z][a-z]+$")]
    [InlineData("{{$randomIPV6}}", @"^([0-9a-f]{4}:){7}[0-9a-f]{4}$")]
    [InlineData("{{$randomMACAddress}}", @"^([0-9a-f]{2}:){5}[0-9a-f]{2}$")]
    [InlineData("{{$randomHexColor}}", @"^#[0-9a-f]{6}$")]
    [InlineData("{{$randomPrice}}", @"^\d+\.\d\d$")]
    [InlineData("{{$randomSemver}}", @"^\d+\.\d+\.\d+$")]
    [InlineData("{{$randomUUID}}", @"^[0-9a-f-]{36}$")]
    [InlineData("{{$randomCountryCode}}", @"^[A-Z]{2}$")]
    [InlineData("{{$randomString(20)}}", @"^[A-Za-z0-9]{20}$")]
    [InlineData("{{ $randomDigits(6) }}", @"^\d{6}$")]
    [InlineData("{{$randomDate(-5, 5)}}", @"^\d{4}-\d\d-\d\d$")]
    public void Placeholders_generate_well_formed_values(string template, string pattern) =>
        Assert.Matches(new Regex(pattern), VariableResolver.Resolve(template, NoVariables));

    [Fact]
    public void Parameterized_int_respects_bounds()
    {
        for (var i = 0; i < 200; i++)
        {
            var value = int.Parse(VariableResolver.Resolve("{{$randomInt(10,12)}}", NoVariables), CultureInfo.InvariantCulture);
            Assert.InRange(value, 10, 12);
        }
    }

    [Fact]
    public void Random_element_picks_from_the_list()
    {
        for (var i = 0; i < 50; i++)
            Assert.Contains(VariableResolver.Resolve("{{$randomElement(red,green,blue)}}", NoVariables), new[] { "red", "green", "blue" });
    }

    [Fact]
    public void Credit_card_numbers_pass_the_luhn_check()
    {
        var faker = new Faker(new Random(7));
        for (var n = 0; n < 50; n++)
        {
            var digits = faker.CreditCard();
            Assert.Equal(16, digits.Length);
            var sum = 0;
            for (var i = 0; i < digits.Length; i++)
            {
                var d = digits[digits.Length - 1 - i] - '0';
                if (i % 2 == 1)
                {
                    d *= 2;
                    if (d > 9) d -= 9;
                }
                sum += d;
            }
            Assert.Equal(0, sum % 10);
        }
    }

    [Fact]
    public void Iban_checksum_is_valid()
    {
        var iban = new Faker(new Random(3)).Iban();
        var rearranged = iban[4..] + "1314" + iban[2..4];
        var remainder = rearranged.Aggregate(0, (r, c) => (r * 10 + (c - '0')) % 97);
        Assert.Equal(1, remainder);
    }

    [Fact]
    public void Seeded_fakers_are_repeatable() =>
        Assert.Equal(new Faker(new Random(42)).Generate("$randomFullName"), new Faker(new Random(42)).Generate("$randomFullName"));

    [Fact]
    public void Variables_still_win_and_unknown_dynamic_names_are_left_alone()
    {
        var vars = new Dictionary<string, string> { ["$randomEmail"] = "fixed@x.io" };
        Assert.Equal("fixed@x.io", VariableResolver.Resolve("{{$randomEmail}}", vars));
        Assert.Equal("{{$nope}}", VariableResolver.Resolve("{{$nope}}", NoVariables));
        Assert.Equal("{{$nope(1)}}", VariableResolver.Resolve("{{$nope(1)}}", NoVariables));
    }
}
