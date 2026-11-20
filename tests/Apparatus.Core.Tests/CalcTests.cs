using Apparatus.Core.Calc;

namespace Apparatus.Core.Tests;

public class CalcTests
{
    private static double Eval(string s)
    {
        Assert.True(ExpressionEvaluator.TryEvaluate(s, out double v), $"'{s}' should evaluate");
        return v;
    }

    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("10/4", 2.5)]
    [InlineData("2^10", 1024)]
    [InlineData("2^3^2", 512)]
    [InlineData("-2^2", -4)]
    [InlineData("2^-1", 0.5)]
    [InlineData("1,5*2", 3)]          // pt-BR decimal comma
    [InlineData("1.5*2", 3)]
    [InlineData("2(3+4)", 14)]        // implicit multiplication
    [InlineData("sqrt(16)+1", 5)]
    [InlineData("max(3;9;2)", 9)]
    [InlineData("10 ÷ 4 × 2", 5)]
    [InlineData("2 ** 3", 8)]
    [InlineData("1e3+1", 1001)]
    [InlineData("--3+1", 4)]
    public void Evaluates(string expr, double expected) => Assert.Equal(expected, Eval(expr), 9);

    [Fact]
    public void Constants_Work() => Assert.Equal(Math.PI * 2, Eval("pi*2"), 9);

    [Theory]
    [InlineData("firefox")]
    [InlineData("2023")]               // a bare number is not a calculation
    [InlineData("pi")]
    [InlineData("1/0")]
    [InlineData("2+")]
    [InlineData("(1+2")]
    [InlineData("foo(2)")]
    [InlineData("chrome+x")]
    [InlineData("")]
    public void Rejects_NonMath(string expr) => Assert.False(ExpressionEvaluator.TryEvaluate(expr, out _));

    [Theory]
    [InlineData(3, "3")]
    [InlineData(2.5, "2,5")]
    [InlineData(1d / 3, "0,333333333333")]
    public void Formats_PtBr(double v, string expected) => Assert.Equal(expected, ExpressionEvaluator.Format(v));

    [Theory]
    [InlineData("10 km to mi", 6.21371, "mi")]
    [InlineData("1 mi em km", 1.609344, "km")]
    [InlineData("5 kg para lb", 11.0231, "lb")]
    [InlineData("1 GiB to MB", 1073.741824, "MB")]
    [InlineData("100 c to f", 212, "°F")]
    [InlineData("32 f para c", 0, "°C")]
    [InlineData("0 c to k", 273.15, "K")]
    [InlineData("90 km/h to m/s", 25, "m/s")]
    [InlineData("2 h em min", 120, "min")]
    [InlineData("1,5 l em ml", 1500, "mL")]
    public void ConvertsUnits(string query, double expected, string unit)
    {
        Assert.True(UnitConverter.TryConvert(query, out var c), query);
        Assert.Equal(expected, c.Result, 3);
        Assert.Equal(unit, c.ToUnit);
    }

    [Theory]
    [InlineData("10 km to kg")]       // different dimensions
    [InlineData("10 foo to bar")]
    [InlineData("km to mi")]
    [InlineData("hello world")]
    public void Rejects_BadConversions(string query) => Assert.False(UnitConverter.TryConvert(query, out _));
}
