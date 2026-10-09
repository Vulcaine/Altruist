using System.Buffers.Binary;
using System.IO.Compression;
using Altruist.Numerics;

namespace Tests.Altruist.Numerics;

/// <summary>Math layer, transcendental functions: <see cref="DeterministicMath"/> against vectors recorded
/// from V8's <c>Math.*</c> (<c>Resources/DeterministicMath/*.bin.gz</c>, written by
/// <c>generate-vectors.mjs</c> next to them). Every result must have exactly the recorded bits (NaN:
/// any NaN), on every platform. The float overloads must equal the double version rounded once.</summary>
public class DeterministicMathTests
{
    private const int MinimumVectors = 20000;

    private static readonly Dictionary<string, Func<double, double>> Unary = new()
    {
        ["sin"] = DeterministicMath.Sin,
        ["cos"] = DeterministicMath.Cos,
        ["tan"] = DeterministicMath.Tan,
        ["atan"] = DeterministicMath.Atan,
        ["exp"] = DeterministicMath.Exp,
        ["log"] = DeterministicMath.Log,
    };

    private static readonly Dictionary<string, Func<double, double, double>> Binary = new()
    {
        ["atan2"] = DeterministicMath.Atan2,
        ["pow"] = DeterministicMath.Pow,
    };

    private static readonly Dictionary<string, Func<float, float>> UnaryFloat = new()
    {
        ["sin"] = DeterministicMath.Sin,
        ["cos"] = DeterministicMath.Cos,
        ["tan"] = DeterministicMath.Tan,
        ["atan"] = DeterministicMath.Atan,
        ["exp"] = DeterministicMath.Exp,
        ["log"] = DeterministicMath.Log,
    };

    private static readonly Dictionary<string, Func<float, float, float>> BinaryFloat = new()
    {
        ["atan2"] = DeterministicMath.Atan2,
        ["pow"] = DeterministicMath.Pow,
    };

    public static TheoryData<string> UnaryNames => new(Unary.Keys);

    public static TheoryData<string> BinaryNames => new(Binary.Keys);

    [Theory]
    [MemberData(nameof(UnaryNames))]
    public void Unary_matches_V8_bit_for_bit(string name)
    {
        var f = Unary[name];
        var records = Load(name, 2);
        var failures = records
            .Where(r => !SameBits(f(Bits(r[0])), Bits(r[1])))
            .Select(r => $"{name}({Describe(r[0])}) = {Describe(f(Bits(r[0])))}, V8 {Describe(r[1])}")
            .ToList();
        Assert.True(failures.Count == 0, Report(name, records.Count, failures));
    }

    [Theory]
    [MemberData(nameof(BinaryNames))]
    public void Binary_matches_V8_bit_for_bit(string name)
    {
        var f = Binary[name];
        var records = Load(name, 3);
        var failures = records
            .Where(r => !SameBits(f(Bits(r[0]), Bits(r[1])), Bits(r[2])))
            .Select(r => $"{name}({Describe(r[0])}, {Describe(r[1])}) = {Describe(f(Bits(r[0]), Bits(r[1])))}, V8 {Describe(r[2])}")
            .ToList();
        Assert.True(failures.Count == 0, Report(name, records.Count, failures));
    }

    [Fact]
    public void SinCos_matches_Sin_and_Cos_bit_for_bit()
    {
        var failures = Load("sin", 2).Concat(Load("cos", 2))
            .Select(r => Bits(r[0]))
            .Where(x =>
            {
                var (s, c) = DeterministicMath.SinCos(x);
                return !SameBits(s, DeterministicMath.Sin(x)) || !SameBits(c, DeterministicMath.Cos(x));
            })
            .Select(x => $"SinCos({x:R})")
            .ToList();
        Assert.True(failures.Count == 0, Report("SinCos", failures.Count, failures));
    }

    [Theory]
    [MemberData(nameof(UnaryNames))]
    public void Unary_float_overload_is_the_double_version_rounded_once(string name)
    {
        var f = Unary[name];
        var g = UnaryFloat[name];
        var failures = FloatInputs(name)
            .Where(x => !SameBits(g(x), (float)f(x)))
            .Select(x => $"{name}({x:R}f)")
            .ToList();
        Assert.True(failures.Count == 0, Report(name + "(float)", failures.Count, failures));
    }

    [Theory]
    [MemberData(nameof(BinaryNames))]
    public void Binary_float_overload_is_the_double_version_rounded_once(string name)
    {
        var f = Binary[name];
        var g = BinaryFloat[name];
        var failures = Load(name, 3)
            .Select(r => ((float)Bits(r[0]), (float)Bits(r[1])))
            .Where(p => !SameBits(g(p.Item1, p.Item2), (float)f(p.Item1, p.Item2)))
            .Select(p => $"{name}({p.Item1:R}f, {p.Item2:R}f)")
            .ToList();
        Assert.True(failures.Count == 0, Report(name + "(float)", failures.Count, failures));
    }

    [Fact]
    public void Float_SinCos_matches_float_Sin_and_Cos()
    {
        var failures = FloatInputs("sin")
            .Where(x =>
            {
                var (s, c) = DeterministicMath.SinCos(x);
                return !SameBits(s, DeterministicMath.Sin(x)) || !SameBits(c, DeterministicMath.Cos(x));
            })
            .Select(x => $"SinCos({x:R}f)")
            .ToList();
        Assert.True(failures.Count == 0, Report("SinCos(float)", failures.Count, failures));
    }

    // ── Vectors ───────────────────────────────────────────────────────────

    private static IReadOnlyList<ulong[]> Load(string name, int width)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "DeterministicMath", $"{name}.bin.gz");
        using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var recordSize = width * sizeof(ulong);
        Assert.Equal(0, bytes.Length % recordSize);
        var records = Enumerable.Range(0, bytes.Length / recordSize)
            .Select(i => Enumerable.Range(0, width)
                .Select(j => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((i * width + j) * sizeof(ulong))))
                .ToArray())
            .ToList();
        Assert.True(records.Count >= MinimumVectors, $"{name}: only {records.Count} vectors in {path}");
        return records;
    }

    private static IEnumerable<float> FloatInputs(string name) => Load(name, 2).Select(r => (float)Bits(r[0]));

    private static double Bits(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    private static bool SameBits(double actual, double expected) =>
        double.IsNaN(expected) ? double.IsNaN(actual) : BitConverter.DoubleToUInt64Bits(actual) == BitConverter.DoubleToUInt64Bits(expected);

    private static bool SameBits(float actual, float expected) =>
        float.IsNaN(expected) ? float.IsNaN(actual) : BitConverter.SingleToUInt32Bits(actual) == BitConverter.SingleToUInt32Bits(expected);

    private static string Describe(ulong bits) => $"{Bits(bits):R} [0x{bits:X16}]";

    private static string Describe(double value) => Describe(BitConverter.DoubleToUInt64Bits(value));

    private static string Report(string name, int total, IReadOnlyList<string> failures) =>
        $"{name}: {failures.Count} of {total} differ:\n" + string.Join("\n", failures.Take(25));
}
