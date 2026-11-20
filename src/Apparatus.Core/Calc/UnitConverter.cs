using System.Globalization;
using System.Text.RegularExpressions;

namespace Apparatus.Core.Calc;

public sealed record UnitConversion(double Value, string FromUnit, double Result, string ToUnit, string Display);

/// <summary>Converts queries like "10 km to mi", "5 kg em lb" or "100 f para c".</summary>
public static partial class UnitConverter
{
    private enum Dimension { Length, Mass, Volume, Area, Data, Time, Speed, Temperature }

    private sealed record Unit(string Symbol, Dimension Dimension, double Factor, string[] Aliases);

    // Factor = how many base units (m, kg, L, m², byte, s, m/s) one of this unit equals.
    private static readonly Unit[] Units =
    {
        new("m", Dimension.Length, 1, new[] { "m", "metro", "metros", "meter", "meters" }),
        new("km", Dimension.Length, 1000, new[] { "km", "quilometro", "quilometros", "quilômetro", "quilômetros", "kilometer", "kilometers" }),
        new("cm", Dimension.Length, 0.01, new[] { "cm", "centimetro", "centimetros", "centímetro", "centímetros" }),
        new("mm", Dimension.Length, 0.001, new[] { "mm", "milimetro", "milimetros", "milímetro", "milímetros" }),
        new("mi", Dimension.Length, 1609.344, new[] { "mi", "mile", "miles", "milha", "milhas" }),
        new("yd", Dimension.Length, 0.9144, new[] { "yd", "yard", "yards", "jarda", "jardas" }),
        new("ft", Dimension.Length, 0.3048, new[] { "ft", "foot", "feet", "pe", "pé", "pes", "pés" }),
        new("in", Dimension.Length, 0.0254, new[] { "inch", "inches", "pol", "polegada", "polegadas", "\"" }),

        new("kg", Dimension.Mass, 1, new[] { "kg", "quilo", "quilos", "quilograma", "quilogramas", "kilogram", "kilograms" }),
        new("g", Dimension.Mass, 0.001, new[] { "g", "grama", "gramas", "gram", "grams" }),
        new("mg", Dimension.Mass, 1e-6, new[] { "mg", "miligrama", "miligramas" }),
        new("t", Dimension.Mass, 1000, new[] { "t", "ton", "tons", "tonelada", "toneladas" }),
        new("lb", Dimension.Mass, 0.45359237, new[] { "lb", "lbs", "libra", "libras", "pound", "pounds" }),
        new("oz", Dimension.Mass, 0.028349523125, new[] { "oz", "onca", "onça", "oncas", "onças", "ounce", "ounces" }),

        new("L", Dimension.Volume, 1, new[] { "l", "litro", "litros", "liter", "liters" }),
        new("mL", Dimension.Volume, 0.001, new[] { "ml", "mililitro", "mililitros" }),
        new("gal", Dimension.Volume, 3.785411784, new[] { "gal", "galao", "galão", "galoes", "galões", "gallon", "gallons" }),
        new("fl oz", Dimension.Volume, 0.0295735295625, new[] { "floz", "fl oz" }),
        new("cup", Dimension.Volume, 0.2365882365, new[] { "cup", "cups", "xicara", "xícara", "xicaras", "xícaras" }),

        new("m²", Dimension.Area, 1, new[] { "m2", "m²" }),
        new("km²", Dimension.Area, 1e6, new[] { "km2", "km²" }),
        new("ha", Dimension.Area, 10000, new[] { "ha", "hectare", "hectares" }),
        new("ft²", Dimension.Area, 0.09290304, new[] { "ft2", "ft²" }),
        new("acre", Dimension.Area, 4046.8564224, new[] { "acre", "acres", "alqueire" }),

        new("B", Dimension.Data, 1, new[] { "b", "byte", "bytes" }),
        new("KB", Dimension.Data, 1e3, new[] { "kb" }),
        new("MB", Dimension.Data, 1e6, new[] { "mb" }),
        new("GB", Dimension.Data, 1e9, new[] { "gb" }),
        new("TB", Dimension.Data, 1e12, new[] { "tb" }),
        new("KiB", Dimension.Data, 1024, new[] { "kib" }),
        new("MiB", Dimension.Data, 1048576, new[] { "mib" }),
        new("GiB", Dimension.Data, 1073741824, new[] { "gib" }),
        new("TiB", Dimension.Data, 1099511627776, new[] { "tib" }),

        new("s", Dimension.Time, 1, new[] { "s", "seg", "segundo", "segundos", "sec", "second", "seconds" }),
        new("ms", Dimension.Time, 0.001, new[] { "ms", "milissegundo", "milissegundos" }),
        new("min", Dimension.Time, 60, new[] { "min", "minuto", "minutos", "minute", "minutes" }),
        new("h", Dimension.Time, 3600, new[] { "h", "hr", "hora", "horas", "hour", "hours" }),
        new("d", Dimension.Time, 86400, new[] { "d", "dia", "dias", "day", "days" }),
        new("sem", Dimension.Time, 604800, new[] { "sem", "semana", "semanas", "week", "weeks" }),

        new("m/s", Dimension.Speed, 1, new[] { "m/s" }),
        new("km/h", Dimension.Speed, 1 / 3.6, new[] { "km/h", "kmh", "kph" }),
        new("mph", Dimension.Speed, 0.44704, new[] { "mph" }),
        new("kn", Dimension.Speed, 0.514444, new[] { "kn", "nó", "nos", "nós", "knot", "knots" }),
    };

    private static readonly Dictionary<string, Unit> ByAlias = BuildAliasMap();

    private static Dictionary<string, Unit> BuildAliasMap()
    {
        var map = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in Units)
            foreach (var a in u.Aliases)
                map.TryAdd(a, u);
        return map;
    }

    private static readonly string[] CelsiusAliases = { "c", "°c", "celsius" };
    private static readonly string[] FahrenheitAliases = { "f", "°f", "fahrenheit" };
    private static readonly string[] KelvinAliases = { "k", "kelvin" };

    [GeneratedRegex(@"^\s*(?<n>[-+]?\d+(?:[.,]\d+)?)\s*(?<from>[^\s\d][^\s]*(?:\s+oz)?)\s+(?:to|em|in|para|->|=|→)\s+(?<to>\S+(?:\s+oz)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryConvert(string text, out UnitConversion conversion)
    {
        conversion = null!;
        var m = Pattern().Match(text);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return false;

        string from = m.Groups["from"].Value.Trim(), to = m.Groups["to"].Value.Trim();

        if (TryTemperature(from, out var tf) && TryTemperature(to, out var tt))
        {
            double kelvin = tf switch { 'c' => value + 273.15, 'f' => (value - 32) * 5 / 9 + 273.15, _ => value };
            double result = tt switch { 'c' => kelvin - 273.15, 'f' => (kelvin - 273.15) * 9 / 5 + 32, _ => kelvin };
            string fs = TempSymbol(tf), ts = TempSymbol(tt);
            conversion = new UnitConversion(value, fs, result, ts, $"{Format(value)} {fs} = {Format(result)} {ts}");
            return true;
        }

        if (!ByAlias.TryGetValue(from, out var uf) || !ByAlias.TryGetValue(to, out var ut)) return false;
        if (uf.Dimension != ut.Dimension) return false;

        double converted = value * uf.Factor / ut.Factor;
        conversion = new UnitConversion(value, uf.Symbol, converted, ut.Symbol,
            $"{Format(value)} {uf.Symbol} = {Format(converted)} {ut.Symbol}");
        return true;
    }

    private static bool TryTemperature(string alias, out char kind)
    {
        kind = '\0';
        if (CelsiusAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) kind = 'c';
        else if (FahrenheitAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) kind = 'f';
        else if (KelvinAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) kind = 'k';
        return kind != '\0';
    }

    private static string TempSymbol(char kind) => kind switch { 'c' => "°C", 'f' => "°F", _ => "K" };

    public static string Format(double value)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        if (Math.Abs(value) >= 1e15 || (Math.Abs(value) < 1e-6 && value != 0)) return value.ToString("0.#####E+0", pt);
        return Math.Round(value, 6).ToString("#,0.######", pt);
    }
}
