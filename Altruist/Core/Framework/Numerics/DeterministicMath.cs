/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0

The algorithms below are a port of fdlibm as shipped by V8 (src/base/ieee754.cc):

    Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
    Developed at SunSoft, a Sun Microsystems, Inc. business.
    Permission to use, copy, modify, and distribute this software is freely granted,
    provided that this notice is preserved.

    Modified by Google Inc. Copyright 2016 the V8 project authors. All rights reserved.
    Use of that source code is governed by a BSD-style license.
*/

namespace Altruist.Numerics;

/// <summary>Math layer. Transcendental functions (sin, cos, tan, atan, atan2, exp, log, pow) that give
/// the same bits on every platform, runtime and version, and the same bits as V8's <c>Math.*</c>
/// (Node, Chrome, the TypeScript client packages) for every input.
/// <para><see cref="Math"/> and <see cref="MathF"/> hand these functions to the operating system's C
/// runtime, so their results differ between Windows, Linux and macOS in the last bit for some inputs.
/// This class is a port of fdlibm exactly as V8 uses it (<c>src/base/ieee754.cc</c>), written with only
/// IEEE 754 <c>+ - * /</c>, exact operations (<see cref="Math.Abs(double)"/>, <see cref="Math.Floor(double)"/>,
/// <see cref="Math.Sqrt"/>) and bit manipulation, never fused multiply-add, so every result is fixed.
/// Results are within 1 ulp of exact; NaN inputs give NaN (payload unspecified).</para>
/// <para>Use it wherever results must replay exactly or match the TypeScript twins: authoritative
/// deterministic simulations, replays, lockstep, golden tests. Use <see cref="Math"/> / <see cref="MathF"/>
/// everywhere else: they are faster (often hardware-accelerated) and may be more accurate.</para>
/// <para>The <see cref="float"/> overloads compute in double and round once:
/// <c>(float)F((double)x)</c>.</para></summary>
public static class DeterministicMath
{
    // ── Public API: double ────────────────────────────────────────────────

    /// <summary>Sine of <paramref name="x"/> radians. Bit-identical on every platform and to V8's
    /// <c>Math.sin</c>. ±0 → ±0; ±∞ and NaN → NaN. Use in deterministic simulations, replays,
    /// lockstep and goldens; use <see cref="Math.Sin"/> elsewhere (faster).</summary>
    public static double Sin(double x)
    {
        var ix = High(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return KernelSin(x, 0.0, 0);
        if (ix >= 0x7FF00000) return x - x;
        var n = RemPio2(x, out var y0, out var y1);
        return (n & 3) switch
        {
            0 => KernelSin(y0, y1, 1),
            1 => KernelCos(y0, y1),
            2 => -KernelSin(y0, y1, 1),
            _ => -KernelCos(y0, y1),
        };
    }

    /// <summary>Cosine of <paramref name="x"/> radians. Bit-identical on every platform and to V8's
    /// <c>Math.cos</c>. ±∞ and NaN → NaN. Use in deterministic simulations, replays, lockstep and
    /// goldens; use <see cref="Math.Cos"/> elsewhere (faster).</summary>
    public static double Cos(double x)
    {
        var ix = High(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return KernelCos(x, 0.0);
        if (ix >= 0x7FF00000) return x - x;
        var n = RemPio2(x, out var y0, out var y1);
        return (n & 3) switch
        {
            0 => KernelCos(y0, y1),
            1 => -KernelSin(y0, y1, 1),
            2 => -KernelCos(y0, y1),
            _ => KernelSin(y0, y1, 1),
        };
    }

    /// <summary>Sine and cosine of <paramref name="x"/> radians with one argument reduction; each
    /// component is bit-identical to <see cref="Sin(double)"/> / <see cref="Cos(double)"/> (so to V8's
    /// <c>Math.sin</c> / <c>Math.cos</c>) on every platform. Use in deterministic simulations, replays,
    /// lockstep and goldens; use <see cref="Math.SinCos"/> elsewhere (faster).</summary>
    public static (double Sin, double Cos) SinCos(double x)
    {
        var ix = High(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return (KernelSin(x, 0.0, 0), KernelCos(x, 0.0));
        if (ix >= 0x7FF00000) return (x - x, x - x);
        var n = RemPio2(x, out var y0, out var y1);
        var s = KernelSin(y0, y1, 1);
        var c = KernelCos(y0, y1);
        return (n & 3) switch
        {
            0 => (s, c),
            1 => (c, -s),
            2 => (-s, -c),
            _ => (-c, s),
        };
    }

    /// <summary>Tangent of <paramref name="x"/> radians. Bit-identical on every platform and to V8's
    /// <c>Math.tan</c>. ±0 → ±0; ±∞ and NaN → NaN. Use in deterministic simulations, replays,
    /// lockstep and goldens; use <see cref="Math.Tan"/> elsewhere (faster).</summary>
    public static double Tan(double x)
    {
        var ix = High(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return KernelTan(x, 0.0, 1);
        if (ix >= 0x7FF00000) return x - x;
        var n = RemPio2(x, out var y0, out var y1);
        return KernelTan(y0, y1, 1 - ((n & 1) << 1));
    }

    /// <summary>Arc tangent of <paramref name="x"/>, in [-π/2, π/2]. Bit-identical on every platform
    /// and to V8's <c>Math.atan</c>. ±0 → ±0; ±∞ → ±π/2; NaN → NaN. Use in deterministic simulations,
    /// replays, lockstep and goldens; use <see cref="Math.Atan"/> elsewhere (faster).</summary>
    public static double Atan(double x)
    {
        var hx = High(x);
        var ix = hx & 0x7FFFFFFF;
        if (ix >= 0x44100000)
        {
            if (ix > 0x7FF00000 || (ix == 0x7FF00000 && Low(x) != 0)) return x + x;
            return hx > 0 ? AtanHi3 + AtanLo3 : -AtanHi3 - AtanLo3;
        }
        int id;
        if (ix < 0x3FDC0000)
        {
            if (ix < 0x3E400000 && Huge + x > 1.0) return x;
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3FF30000)
            {
                if (ix < 0x3FE60000) { id = 0; x = (2.0 * x - 1.0) / (2.0 + x); }
                else { id = 1; x = (x - 1.0) / (x + 1.0); }
            }
            else if (ix < 0x40038000) { id = 2; x = (x - 1.5) / (1.0 + 1.5 * x); }
            else { id = 3; x = -1.0 / x; }
        }
        var z = x * x;
        var w = z * z;
        var s1 = z * (AT0 + w * (AT2 + w * (AT4 + w * (AT6 + w * (AT8 + w * AT10)))));
        var s2 = w * (AT1 + w * (AT3 + w * (AT5 + w * (AT7 + w * AT9))));
        if (id < 0) return x - x * (s1 + s2);
        z = AtanHi[id] - ((x * (s1 + s2) - AtanLo[id]) - x);
        return hx < 0 ? -z : z;
    }

    /// <summary>Angle of the point (<paramref name="x"/>, <paramref name="y"/>) from the +x axis, in
    /// [-π, π] (note the argument order: y first, as <see cref="Math.Atan2"/>). Bit-identical on every
    /// platform and to V8's <c>Math.atan2(y, x)</c>, including the signed-zero and infinity cases.
    /// Use in deterministic simulations, replays, lockstep and goldens; use <see cref="Math.Atan2"/>
    /// elsewhere (faster).</summary>
    public static double Atan2(double y, double x)
    {
        const double tiny = 1.0e-300;
        const double piO4 = 7.8539816339744827900E-01;
        const double piO2 = 1.5707963267948965580E+00;
        const double pi = 3.1415926535897931160E+00;
        const double piLo = 1.2246467991473531772E-16;

        int hx = High(x), hy = High(y);
        uint lx = Low(x), ly = Low(y);
        int ix = hx & 0x7FFFFFFF, iy = hy & 0x7FFFFFFF;
        if (((uint)ix | ((lx | unchecked((uint)-(int)lx)) >> 31)) > 0x7FF00000u ||
            ((uint)iy | ((ly | unchecked((uint)-(int)ly)) >> 31)) > 0x7FF00000u)
            return x + y;
        if ((unchecked((uint)(hx - 0x3FF00000)) | lx) == 0) return Atan(y);
        var m = ((hy >> 31) & 1) | ((hx >> 30) & 2);

        if ((iy | (int)ly) == 0)
        {
            return m switch
            {
                0 or 1 => y,
                2 => pi + tiny,
                _ => -pi - tiny,
            };
        }
        if ((ix | (int)lx) == 0) return hy < 0 ? -piO2 - tiny : piO2 + tiny;

        if (ix == 0x7FF00000)
        {
            if (iy == 0x7FF00000)
            {
                return m switch
                {
                    0 => piO4 + tiny,
                    1 => -piO4 - tiny,
                    2 => 3.0 * piO4 + tiny,
                    _ => -3.0 * piO4 - tiny,
                };
            }
            return m switch
            {
                0 => 0.0,
                1 => -0.0,
                2 => pi + tiny,
                _ => -pi - tiny,
            };
        }
        if (iy == 0x7FF00000) return hy < 0 ? -piO2 - tiny : piO2 + tiny;

        double z;
        var k = (iy - ix) >> 20;
        if (k > 60)
        {
            z = piO2 + 0.5 * piLo;
            m &= 1;
        }
        else if (hx < 0 && k < -60)
        {
            z = 0.0;
        }
        else
        {
            z = Atan(Math.Abs(y / x));
        }
        return m switch
        {
            0 => z,
            1 => -z,
            2 => pi - (z - piLo),
            _ => (z - piLo) - pi,
        };
    }

    /// <summary>e raised to <paramref name="x"/>. Bit-identical on every platform and to V8's
    /// <c>Math.exp</c>. Overflows to +∞ above ~709.78, underflows to +0 below ~-745.13; +∞ → +∞,
    /// -∞ → +0, NaN → NaN. Use in deterministic simulations, replays, lockstep and goldens; use
    /// <see cref="Math.Exp"/> elsewhere (faster).</summary>
    public static double Exp(double x)
    {
        const double oThreshold = 7.09782712893383973096e+02;
        const double uThreshold = -7.45133219101941108420e+02;
        const double ln2Hi = 6.93147180369123816490e-01;
        const double ln2Lo = 1.90821492927058770002e-10;
        const double invLn2 = 1.44269504088896338700e+00;
        const double twoM1000 = 9.33263618503218878990e-302;
        const double two1023 = 8.988465674311579539e307;
        const double e = 2.718281828459045;

        var hx = unchecked((uint)High(x));
        var xsb = (int)((hx >> 31) & 1);
        hx &= 0x7FFFFFFF;

        if (hx >= 0x40862E42)
        {
            if (hx >= 0x7FF00000)
            {
                if (((hx & 0xFFFFF) | Low(x)) != 0) return x + x;
                return xsb == 0 ? x : 0.0;
            }
            if (x > oThreshold) return Huge * Huge;
            if (x < uThreshold) return twoM1000 * twoM1000;
        }

        double hi = 0.0, lo = 0.0;
        var k = 0;
        if (hx > 0x3FD62E42)
        {
            if (hx < 0x3FF0A2B2)
            {
                // V8 special-cases exp(1) to return the correctly rounded E.
                if (x == 1.0) return e;
                hi = x - (xsb == 0 ? ln2Hi : -ln2Hi);
                lo = xsb == 0 ? ln2Lo : -ln2Lo;
                k = 1 - xsb - xsb;
            }
            else
            {
                k = (int)(invLn2 * x + (xsb == 0 ? 0.5 : -0.5));
                double tk = k;
                hi = x - tk * ln2Hi;
                lo = tk * ln2Lo;
            }
            x = hi - lo;
        }
        else if (hx < 0x3E300000)
        {
            if (Huge + x > 1.0) return 1.0 + x;
        }

        var t = x * x;
        var twopk = k >= -1021
            ? FromWords(0x3FF00000 + unchecked((int)((uint)k << 20)), 0)
            : FromWords(unchecked((int)(0x3FF00000u + ((uint)(k + 1000) << 20))), 0);
        var c = x - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        if (k == 0) return 1.0 - ((x * c) / (c - 2.0) - x);
        var y = 1.0 - ((lo - (x * c) / (2.0 - c)) - hi);
        if (k >= -1021)
        {
            if (k == 1024) return y * 2.0 * two1023;
            return y * twopk;
        }
        return y * twopk * twoM1000;
    }

    /// <summary>Natural logarithm of <paramref name="x"/>. Bit-identical on every platform and to V8's
    /// <c>Math.log</c>. ±0 → -∞; negative, -∞ and NaN → NaN; +∞ → +∞; exact 0 at 1. Use in
    /// deterministic simulations, replays, lockstep and goldens; use <see cref="Math.Log(double)"/>
    /// elsewhere (faster).</summary>
    public static double Log(double x)
    {
        const double ln2Hi = 6.93147180369123816490e-01;
        const double ln2Lo = 1.90821492927058770002e-10;
        const double two54 = 1.80143985094819840000e+16;
        const double lg1 = 6.666666666666735130e-01;
        const double lg2 = 3.999999999940941908e-01;
        const double lg3 = 2.857142874366239149e-01;
        const double lg4 = 2.222219843214978396e-01;
        const double lg5 = 1.818357216161805012e-01;
        const double lg6 = 1.531383769920937332e-01;
        const double lg7 = 1.479819860511658591e-01;

        var hx = High(x);
        var lx = Low(x);
        var k = 0;
        if (hx < 0x00100000)
        {
            if (((uint)(hx & 0x7FFFFFFF) | lx) == 0) return double.NegativeInfinity;
            if (hx < 0) return double.NaN;
            k -= 54;
            x *= two54;
            hx = High(x);
        }
        if (hx >= 0x7FF00000) return x + x;
        k += (hx >> 20) - 1023;
        hx &= 0x000FFFFF;
        var i = (hx + 0x95F64) & 0x100000;
        x = WithHigh(x, hx | (i ^ 0x3FF00000));
        k += i >> 20;
        var f = x - 1.0;
        double dk;
        if ((0x000FFFFF & (2 + hx)) < 3)
        {
            if (f == 0.0)
            {
                if (k == 0) return 0.0;
                dk = k;
                return dk * ln2Hi + dk * ln2Lo;
            }
            var rr = f * f * (0.5 - 0.33333333333333333 * f);
            if (k == 0) return f - rr;
            dk = k;
            return dk * ln2Hi - ((rr - dk * ln2Lo) - f);
        }
        var s = f / (2.0 + f);
        dk = k;
        var z = s * s;
        i = hx - 0x6147A;
        var w = z * z;
        var j = 0x6B851 - hx;
        var t1 = w * (lg2 + w * (lg4 + w * lg6));
        var t2 = z * (lg1 + w * (lg3 + w * (lg5 + w * lg7)));
        i |= j;
        var r = t2 + t1;
        if (i > 0)
        {
            var hfsq = 0.5 * f * f;
            if (k == 0) return f - (hfsq - s * (hfsq + r));
            return dk * ln2Hi - ((hfsq - (s * (hfsq + r) + dk * ln2Lo)) - f);
        }
        if (k == 0) return f - s * (f - r);
        return dk * ln2Hi - ((s * (f - r) - dk * ln2Lo) - f);
    }

    /// <summary><paramref name="x"/> raised to <paramref name="y"/>. Bit-identical on every platform and
    /// to V8's <c>Math.pow</c> / <c>**</c>, including JavaScript's special cases: <c>y</c> = ±0 → 1
    /// (even for NaN <c>x</c>), NaN <c>y</c> → NaN, and (±1)^(±∞) → NaN (where
    /// <see cref="Math.Pow"/> gives 1). Use in deterministic simulations, replays, lockstep and goldens;
    /// use <see cref="Math.Pow"/> elsewhere (faster).</summary>
    public static double Pow(double x, double y)
    {
        int hx = High(x), hy = High(y);
        uint lx = Low(x), ly = Low(y);
        int ix = hx & 0x7FFFFFFF, iy = hy & 0x7FFFFFFF;

        if ((iy | (int)ly) == 0) return 1.0;
        if (ix > 0x7FF00000 || (ix == 0x7FF00000 && lx != 0) || iy > 0x7FF00000 || (iy == 0x7FF00000 && ly != 0))
            return x + y;

        var yisint = PowExponentKind(hx, iy, ly);

        if (ly == 0)
        {
            if (iy == 0x7FF00000)
            {
                if ((unchecked((uint)(ix - 0x3FF00000)) | lx) == 0) return y - y;
                if (ix >= 0x3FF00000) return hy >= 0 ? y : 0.0;
                return hy < 0 ? -y : 0.0;
            }
            if (iy == 0x3FF00000) return hy < 0 ? 1.0 / x : x;
            if (hy == 0x40000000) return x * x;
            if (hy == 0x3FE00000 && hx >= 0) return Math.Sqrt(x);
        }

        var ax = Math.Abs(x);
        if (lx == 0 && (ix == 0x7FF00000 || ix == 0 || ix == 0x3FF00000))
        {
            var z = ax;
            if (hy < 0) z = 1.0 / z;
            if (hx < 0)
            {
                if (((ix - 0x3FF00000) | yisint) == 0) z = double.NaN;
                else if (yisint == 1) z = -z;
            }
            return z;
        }

        var n = (hx >> 31) + 1;
        if ((n | yisint) == 0) return double.NaN;
        var s = (n | (yisint - 1)) == 0 ? -1.0 : 1.0;

        double t1, t2;
        if (iy > 0x41E00000)
        {
            if (iy > 0x43F00000)
            {
                if (ix <= 0x3FEFFFFF) return hy < 0 ? Huge * Huge : Tiny * Tiny;
                if (ix >= 0x3FF00000) return hy > 0 ? Huge * Huge : Tiny * Tiny;
            }
            if (ix < 0x3FEFFFFF) return hy < 0 ? s * Huge * Huge : s * Tiny * Tiny;
            if (ix > 0x3FF00000) return hy > 0 ? s * Huge * Huge : s * Tiny * Tiny;
            PowLog2NearOne(ax, out t1, out t2);
        }
        else
        {
            PowLog2(ax, ix, out t1, out t2);
        }

        return PowExp2(y, t1, t2, s);
    }

    // ── Public API: float ─────────────────────────────────────────────────

    /// <summary><c>(float)Sin((double)x)</c>: sine in single precision, bit-identical on every platform
    /// (and to <c>Math.fround(Math.sin(x))</c> in V8). Use in deterministic simulations, replays,
    /// lockstep and goldens; use <see cref="MathF.Sin"/> elsewhere (faster).</summary>
    public static float Sin(float x) => (float)Sin((double)x);

    /// <summary><c>(float)Cos((double)x)</c>: cosine in single precision, bit-identical on every platform
    /// (and to <c>Math.fround(Math.cos(x))</c> in V8). Use in deterministic simulations, replays,
    /// lockstep and goldens; use <see cref="MathF.Cos"/> elsewhere (faster).</summary>
    public static float Cos(float x) => (float)Cos((double)x);

    /// <summary><see cref="SinCos(double)"/> rounded to single precision: each component equals
    /// <see cref="Sin(float)"/> / <see cref="Cos(float)"/>, bit-identical on every platform. Use in
    /// deterministic simulations, replays, lockstep and goldens; use <see cref="MathF.SinCos"/>
    /// elsewhere (faster).</summary>
    public static (float Sin, float Cos) SinCos(float x)
    {
        var (s, c) = SinCos((double)x);
        return ((float)s, (float)c);
    }

    /// <summary><c>(float)Tan((double)x)</c>: tangent in single precision, bit-identical on every
    /// platform (and to <c>Math.fround(Math.tan(x))</c> in V8). Use in deterministic simulations,
    /// replays, lockstep and goldens; use <see cref="MathF.Tan"/> elsewhere (faster).</summary>
    public static float Tan(float x) => (float)Tan((double)x);

    /// <summary><c>(float)Atan((double)x)</c>: arc tangent in single precision, bit-identical on every
    /// platform (and to <c>Math.fround(Math.atan(x))</c> in V8). Use in deterministic simulations,
    /// replays, lockstep and goldens; use <see cref="MathF.Atan"/> elsewhere (faster).</summary>
    public static float Atan(float x) => (float)Atan((double)x);

    /// <summary><c>(float)Atan2((double)y, (double)x)</c>: angle of (x, y) in single precision,
    /// bit-identical on every platform (and to <c>Math.fround(Math.atan2(y, x))</c> in V8). Use in
    /// deterministic simulations, replays, lockstep and goldens; use <see cref="MathF.Atan2"/>
    /// elsewhere (faster).</summary>
    public static float Atan2(float y, float x) => (float)Atan2((double)y, (double)x);

    /// <summary><c>(float)Exp((double)x)</c>: e^x in single precision, bit-identical on every platform
    /// (and to <c>Math.fround(Math.exp(x))</c> in V8). Use in deterministic simulations, replays,
    /// lockstep and goldens; use <see cref="MathF.Exp"/> elsewhere (faster).</summary>
    public static float Exp(float x) => (float)Exp((double)x);

    /// <summary><c>(float)Log((double)x)</c>: natural logarithm in single precision, bit-identical on
    /// every platform (and to <c>Math.fround(Math.log(x))</c> in V8). Use in deterministic simulations,
    /// replays, lockstep and goldens; use <see cref="MathF.Log(float)"/> elsewhere (faster).</summary>
    public static float Log(float x) => (float)Log((double)x);

    /// <summary><c>(float)Pow((double)x, (double)y)</c>: x^y in single precision with JavaScript's
    /// special cases (see <see cref="Pow(double,double)"/>), bit-identical on every platform (and to
    /// <c>Math.fround(Math.pow(x, y))</c> in V8). Use in deterministic simulations, replays, lockstep
    /// and goldens; use <see cref="MathF.Pow"/> elsewhere (faster).</summary>
    public static float Pow(float x, float y) => (float)Pow((double)x, (double)y);

    // ── Bits ──────────────────────────────────────────────────────────────

    private static int High(double d) => (int)(BitConverter.DoubleToInt64Bits(d) >> 32);

    private static uint Low(double d) => unchecked((uint)BitConverter.DoubleToInt64Bits(d));

    private static double FromWords(int high, uint low) =>
        BitConverter.Int64BitsToDouble(((long)high << 32) | low);

    private static double WithHigh(double d, int high) => FromWords(high, Low(d));

    private static double WithLow(double d, uint low) => FromWords(High(d), low);

    // ── Shared constants ──────────────────────────────────────────────────

    private const double Huge = 1.0e300;
    private const double Tiny = 1.0e-300;
    private const double Two24 = 1.67772160000000000000e+07;
    private const double TwoN24 = 5.96046447753906250000e-08;

    // exp(r) rational approximation (exp and pow).
    private const double P1 = 1.66666666666666019037e-01;
    private const double P2 = -2.77777777770155933842e-03;
    private const double P3 = 6.61375632143793436117e-05;
    private const double P4 = -1.65339022054652515390e-06;
    private const double P5 = 4.13813679705723846039e-08;

    // atan
    private const double AtanHi3 = 1.57079632679489655800e+00;
    private const double AtanLo3 = 6.12323399573676603587e-17;
    private const double AT0 = 3.33333333333329318027e-01;
    private const double AT1 = -1.99999999998764832476e-01;
    private const double AT2 = 1.42857142725034663711e-01;
    private const double AT3 = -1.11111104054623557880e-01;
    private const double AT4 = 9.09088713343650656196e-02;
    private const double AT5 = -7.69187620504482999495e-02;
    private const double AT6 = 6.66107313738753120669e-02;
    private const double AT7 = -5.83357013379057348645e-02;
    private const double AT8 = 4.97687799461593236017e-02;
    private const double AT9 = -3.65315727442169155270e-02;
    private const double AT10 = 1.62858201153657823623e-02;

    private static ReadOnlySpan<double> AtanHi =>
    [
        4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00,
    ];

    private static ReadOnlySpan<double> AtanLo =>
    [
        2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17,
    ];

    // ── Trigonometric kernels (|x| ≤ ~π/4, y = tail of x) ────────────────

    private static double KernelSin(double x, double y, int iy)
    {
        const double s1 = -1.66666666666666324348e-01;
        const double s2 = 8.33333333332248946124e-03;
        const double s3 = -1.98412698298579493134e-04;
        const double s4 = 2.75573137070700676789e-06;
        const double s5 = -2.50507602534068634195e-08;
        const double s6 = 1.58969099521155010221e-10;

        var ix = High(x) & 0x7FFFFFFF;
        if (ix < 0x3E400000 && (int)x == 0) return x;
        var z = x * x;
        var v = z * x;
        var r = s2 + z * (s3 + z * (s4 + z * (s5 + z * s6)));
        if (iy == 0) return x + v * (s1 + z * r);
        return x - ((z * (0.5 * y - v * r) - y) - v * s1);
    }

    private static double KernelCos(double x, double y)
    {
        const double c1 = 4.16666666666666019037e-02;
        const double c2 = -1.38888888888741095749e-03;
        const double c3 = 2.48015872894767294178e-05;
        const double c4 = -2.75573143513906633035e-07;
        const double c5 = 2.08757232129817482790e-09;
        const double c6 = -1.13596475577881948265e-11;

        var ix = High(x) & 0x7FFFFFFF;
        if (ix < 0x3E400000 && (int)x == 0) return 1.0;
        var z = x * x;
        var r = z * (c1 + z * (c2 + z * (c3 + z * (c4 + z * (c5 + z * c6)))));
        if (ix < 0x3FD33333) return 1.0 - (0.5 * z - (z * r - x * y));
        var qx = ix > 0x3FE90000 ? 0.28125 : FromWords(ix - 0x00200000, 0);
        var iz = 0.5 * z - qx;
        var a = 1.0 - qx;
        return a - (iz - (z * r - x * y));
    }

    private static ReadOnlySpan<double> TanCoefficients =>
    [
        3.33333333333334091986e-01, 1.33333333333201242699e-01, 5.39682539762260521377e-02,
        2.18694882948595424599e-02, 8.86323982359930005737e-03, 3.59207910759131235356e-03,
        1.45620945432529025516e-03, 5.88041240820264096874e-04, 2.46463134818469906812e-04,
        7.81794442939557092300e-05, 7.14072491382608190305e-05, -1.85586374855275456654e-05,
        2.59073051863633712884e-05,
    ];

    /// <summary>tan(x + y) when <paramref name="iy"/> is 1, -1/tan(x + y) when it is -1.</summary>
    private static double KernelTan(double x, double y, int iy)
    {
        const double pio4 = 7.85398163397448278999e-01;
        const double pio4Lo = 3.06161699786838301793e-17;
        var T = TanCoefficients;

        var hx = High(x);
        var ix = hx & 0x7FFFFFFF;
        if (ix < 0x3E300000 && (int)x == 0)
        {
            if (((uint)ix | Low(x) | (uint)(iy + 1)) == 0) return 1.0 / Math.Abs(x);
            if (iy == 1) return x;
            return NegativeReciprocal(x, y, x + y);
        }
        if (ix >= 0x3FE59428)
        {
            if (hx < 0)
            {
                x = -x;
                y = -y;
            }
            x = (pio4 - x) + (pio4Lo - y);
            y = 0.0;
        }
        var z = x * x;
        var w = z * z;
        var r = T[1] + w * (T[3] + w * (T[5] + w * (T[7] + w * (T[9] + w * T[11]))));
        var v = z * (T[2] + w * (T[4] + w * (T[6] + w * (T[8] + w * (T[10] + w * T[12])))));
        var s = z * x;
        r = y + z * (s * (r + v) + y);
        r += T[0] * s;
        w = x + r;
        if (ix >= 0x3FE59428)
        {
            v = iy;
            return (1 - ((hx >> 30) & 2)) * (v - 2.0 * (x - (w * w / (w + v) - r)));
        }
        if (iy == 1) return w;
        return NegativeReciprocal(x, r, w);
    }

    /// <summary>-1 / w computed carefully, where w = x + tail rounded.</summary>
    private static double NegativeReciprocal(double x, double tail, double w)
    {
        var z = WithLow(w, 0);
        var v = tail - (z - x);
        var a = -1.0 / w;
        var t = WithLow(a, 0);
        var s = 1.0 + t * z;
        return t + a * (s + t * v);
    }

    // ── Argument reduction: x - n·π/2 as y0 + y1, returns n ──────────────

    private const double InvPio2 = 6.36619772367581382433e-01;
    private const double Pio2_1 = 1.57079632673412561417e+00;
    private const double Pio2_1t = 6.07710050650619224932e-11;
    private const double Pio2_2 = 6.07710050630396597660e-11;
    private const double Pio2_2t = 2.02226624879595063154e-21;
    private const double Pio2_3 = 2.02226624871116645580e-21;
    private const double Pio2_3t = 8.47842766036889956997e-32;

    private static ReadOnlySpan<int> NPio2Hw =>
    [
        0x3FF921FB, 0x400921FB, 0x4012D97C, 0x401921FB, 0x401F6A7A, 0x4022D97C, 0x4025FDBB, 0x402921FB,
        0x402C463A, 0x402F6A7A, 0x4031475C, 0x4032D97C, 0x40346B9C, 0x4035FDBB, 0x40378FDB, 0x403921FB,
        0x403AB41B, 0x403C463A, 0x403DD85A, 0x403F6A7A, 0x40407E4C, 0x4041475C, 0x4042106C, 0x4042D97C,
        0x4043A28C, 0x40446B9C, 0x404534AC, 0x4045FDBB, 0x4046C6CB, 0x40478FDB, 0x404858EB, 0x404921FB,
    ];

    private static int RemPio2(double x, out double y0, out double y1)
    {
        var hx = High(x);
        var ix = hx & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB)
        {
            y0 = x;
            y1 = 0.0;
            return 0;
        }
        if (ix < 0x4002D97C) return RemPio2Small(x, hx, ix, out y0, out y1);
        if (ix <= 0x413921FB) return RemPio2Medium(x, hx, ix, out y0, out y1);
        if (ix >= 0x7FF00000)
        {
            y0 = y1 = x - x;
            return 0;
        }
        var n = RemPio2Large(x, ix, out y0, out y1);
        if (hx >= 0) return n;
        y0 = -y0;
        y1 = -y1;
        return -n;
    }

    /// <summary>|x| &lt; 3π/4: n = ±1.</summary>
    private static int RemPio2Small(double x, int hx, int ix, out double y0, out double y1)
    {
        double z;
        if (hx > 0)
        {
            z = x - Pio2_1;
            if (ix != 0x3FF921FB)
            {
                y0 = z - Pio2_1t;
                y1 = (z - y0) - Pio2_1t;
            }
            else
            {
                z -= Pio2_2;
                y0 = z - Pio2_2t;
                y1 = (z - y0) - Pio2_2t;
            }
            return 1;
        }
        z = x + Pio2_1;
        if (ix != 0x3FF921FB)
        {
            y0 = z + Pio2_1t;
            y1 = (z - y0) + Pio2_1t;
        }
        else
        {
            z += Pio2_2;
            y0 = z + Pio2_2t;
            y1 = (z - y0) + Pio2_2t;
        }
        return -1;
    }

    /// <summary>|x| ≤ 2^19·π/2: Cody-Waite reduction with up to three pieces of π/2.</summary>
    private static int RemPio2Medium(double x, int hx, int ix, out double y0, out double y1)
    {
        var t = Math.Abs(x);
        var n = (int)(t * InvPio2 + 0.5);
        double fn = n;
        var r = t - fn * Pio2_1;
        var w = fn * Pio2_1t;
        y0 = r - w;
        if (n >= 32 || ix == NPio2Hw[n - 1])
        {
            var j = ix >> 20;
            var i = j - ((High(y0) >> 20) & 0x7FF);
            if (i > 16)
            {
                t = r;
                w = fn * Pio2_2;
                r = t - w;
                w = fn * Pio2_2t - ((t - r) - w);
                y0 = r - w;
                i = j - ((High(y0) >> 20) & 0x7FF);
                if (i > 49)
                {
                    t = r;
                    w = fn * Pio2_3;
                    r = t - w;
                    w = fn * Pio2_3t - ((t - r) - w);
                    y0 = r - w;
                }
            }
        }
        y1 = (r - y0) - w;
        if (hx >= 0) return n;
        y0 = -y0;
        y1 = -y1;
        return -n;
    }

    /// <summary>Large finite |x|: split into three 24-bit pieces and reduce with
    /// <see cref="KernelRemPio2"/>. Returns n for |x| (the caller applies the sign).</summary>
    private static int RemPio2Large(double x, int ix, out double y0, out double y1)
    {
        var e0 = (ix >> 20) - 1046;
        var z = FromWords(ix - unchecked((int)((uint)e0 << 20)), Low(x));
        Span<double> tx = stackalloc double[3];
        for (var i = 0; i < 2; i++)
        {
            tx[i] = (int)z;
            z = (z - tx[i]) * Two24;
        }
        tx[2] = z;
        var nx = 3;
        while (tx[nx - 1] == 0.0) nx--;
        return KernelRemPio2(tx[..nx], e0, out y0, out y1);
    }

    // 2/π in 24-bit chunks.
    private static ReadOnlySpan<int> TwoOverPi =>
    [
        0xA2F983, 0x6E4E44, 0x1529FC, 0x2757D1, 0xF534DD, 0xC0DB62, 0x95993C, 0x439041, 0xFE5163,
        0xABDEBB, 0xC561B7, 0x246E3A, 0x424DD2, 0xE00649, 0x2EEA09, 0xD1921C, 0xFE1DEB, 0x1CB129,
        0xA73EE8, 0x8235F5, 0x2EBB44, 0x84E99C, 0x7026B4, 0x5F7E41, 0x3991D6, 0x398353, 0x39F49C,
        0x845F8B, 0xBDF928, 0x3B1FF8, 0x97FFDE, 0x05980F, 0xEF2F11, 0x8B5A0A, 0x6D1F6D, 0x367ECF,
        0x27CB09, 0xB74F46, 0x3F669E, 0x5FEA2D, 0x7527BA, 0xC7EBE5, 0xF17B3D, 0x0739F7, 0x8A5292,
        0xEA6BFB, 0x5FB11F, 0x8D5D08, 0x560330, 0x46FC7B, 0x6BABF0, 0xCFBC20, 0x9AF436, 0x1DA9E3,
        0x91615E, 0xE61B08, 0x659985, 0x5F14A0, 0x68408D, 0xFFD880, 0x4D7327, 0x310606, 0x1556CA,
        0x73A8C9, 0x60E27B, 0xC08C6B,
    ];

    // π/2 in 24-bit chunks.
    private static ReadOnlySpan<double> PIo2 =>
    [
        1.57079625129699707031e+00, 7.54978941586159635335e-08, 5.39030252995776476554e-15,
        3.28200341580791294123e-22, 1.27065575308067607349e-29, 1.22933308981111328932e-36,
        2.73370053816464559624e-44, 2.16741683877804819444e-51,
    ];

    /// <summary>fdlibm <c>__kernel_rem_pio2</c> at the precision fdlibm's rem_pio2 asks for
    /// (prec = 2, jk = 4). <paramref name="x"/> holds the 24-bit pieces of |x| scaled by 2^-e0.
    /// Returns n mod 8.</summary>
    private static int KernelRemPio2(ReadOnlySpan<double> x, int e0, out double y0, out double y1)
    {
        const int jk = 4;
        const int jp = jk;
        Span<int> iq = stackalloc int[20];
        Span<double> f = stackalloc double[20];
        Span<double> fq = stackalloc double[20];
        Span<double> q = stackalloc double[20];

        var jx = x.Length - 1;
        var jv = (e0 - 3) / 24;
        if (jv < 0) jv = 0;
        var q0 = e0 - 24 * (jv + 1);

        for (int i = 0, j = jv - jx; i <= jx + jk; i++, j++) f[i] = j < 0 ? 0.0 : TwoOverPi[j];
        for (var i = 0; i <= jk; i++) q[i] = Convolve(x, f, jx + i);

        var jz = jk;
        int n, ih;
        double z;
        while (true)
        {
            // Distill q[] into iq[] reversingly.
            z = q[jz];
            for (int i = 0, j = jz; j > 0; i++, j--)
            {
                double fw = (int)(TwoN24 * z);
                iq[i] = (int)(z - Two24 * fw);
                z = q[j - 1] + fw;
            }

            z = ScaleB(z, q0);
            z -= 8.0 * Math.Floor(z * 0.125);
            n = (int)z;
            z -= n;
            ih = 0;
            if (q0 > 0)
            {
                var i = iq[jz - 1] >> (24 - q0);
                n += i;
                iq[jz - 1] -= i << (24 - q0);
                ih = iq[jz - 1] >> (23 - q0);
            }
            else if (q0 == 0)
            {
                ih = iq[jz - 1] >> 23;
            }
            else if (z >= 0.5)
            {
                ih = 2;
            }

            if (ih > 0)
            {
                n += 1;
                var carry = 0;
                for (var i = 0; i < jz; i++)
                {
                    var j = iq[i];
                    if (carry == 0)
                    {
                        if (j != 0)
                        {
                            carry = 1;
                            iq[i] = 0x1000000 - j;
                        }
                    }
                    else
                    {
                        iq[i] = 0xFFFFFF - j;
                    }
                }
                if (q0 == 1) iq[jz - 1] &= 0x7FFFFF;
                else if (q0 == 2) iq[jz - 1] &= 0x3FFFFF;
                if (ih == 2)
                {
                    z = 1.0 - z;
                    if (carry != 0) z -= ScaleB(1.0, q0);
                }
            }

            if (z != 0.0) break;
            var any = 0;
            for (var i = jz - 1; i >= jk; i--) any |= iq[i];
            if (any != 0) break;

            // Recompute with more terms of 2/π.
            var k = 1;
            while (jk >= k && iq[jk - k] == 0) k++;
            for (var i = jz + 1; i <= jz + k; i++)
            {
                f[jx + i] = TwoOverPi[jv + i];
                q[i] = Convolve(x, f, jx + i);
            }
            jz += k;
        }

        // Chop off zero terms, or break z into 24-bit chunks.
        if (z == 0.0)
        {
            jz -= 1;
            q0 -= 24;
            while (iq[jz] == 0)
            {
                jz--;
                q0 -= 24;
            }
        }
        else
        {
            z = ScaleB(z, -q0);
            if (z >= Two24)
            {
                double fw = (int)(TwoN24 * z);
                iq[jz] = (int)(z - Two24 * fw);
                jz += 1;
                q0 += 24;
                iq[jz] = (int)fw;
            }
            else
            {
                iq[jz] = (int)z;
            }
        }

        // Convert the integer chunks to floating point.
        var scale = ScaleB(1.0, q0);
        for (var i = jz; i >= 0; i--)
        {
            q[i] = scale * iq[i];
            scale *= TwoN24;
        }

        // fq[] = PIo2[0..jp] * q[jz..0].
        for (var i = jz; i >= 0; i--)
        {
            var fw = 0.0;
            for (var k = 0; k <= jp && k <= jz - i; k++) fw += PIo2[k] * q[i + k];
            fq[jz - i] = fw;
        }

        var sum = 0.0;
        for (var i = jz; i >= 0; i--) sum += fq[i];
        y0 = ih == 0 ? sum : -sum;
        var tail = fq[0] - sum;
        for (var i = 1; i <= jz; i++) tail += fq[i];
        y1 = ih == 0 ? tail : -tail;
        return n & 7;
    }

    /// <summary>Σ x[j]·f[top - j] for j = 0..x.Length-1, summed in index order.</summary>
    private static double Convolve(ReadOnlySpan<double> x, ReadOnlySpan<double> f, int top)
    {
        var fw = 0.0;
        for (var j = 0; j < x.Length; j++) fw += x[j] * f[top - j];
        return fw;
    }

    /// <summary>x·2^n, correctly rounded (fdlibm <c>scalbn</c>; exact unless the result is subnormal
    /// or out of range).</summary>
    private static double ScaleB(double x, int n)
    {
        const double two54 = 1.80143985094819840000e+16;
        const double twoM54 = 5.55111512312578270212e-17;

        var hx = High(x);
        var k = (hx & 0x7FF00000) >> 20;
        if (k == 0)
        {
            if (((uint)(hx & 0x7FFFFFFF) | Low(x)) == 0) return x;
            x *= two54;
            hx = High(x);
            k = ((hx & 0x7FF00000) >> 20) - 54;
            if (n < -50000) return Tiny * x;
        }
        if (k == 0x7FF) return x + x;
        k += n;
        if (k > 0x7FE) return Huge * CopySign(Huge, x);
        if (k > 0) return WithHigh(x, (hx & unchecked((int)0x800FFFFF)) | (k << 20));
        if (k <= -54)
        {
            return n > 50000 ? Huge * CopySign(Huge, x) : Tiny * CopySign(Tiny, x);
        }
        k += 54;
        return WithHigh(x, (hx & unchecked((int)0x800FFFFF)) | (k << 20)) * twoM54;
    }

    private static double CopySign(double magnitude, double sign) =>
        BitConverter.Int64BitsToDouble(
            (BitConverter.DoubleToInt64Bits(magnitude) & long.MaxValue) |
            (BitConverter.DoubleToInt64Bits(sign) & long.MinValue));

    // ── pow helpers ───────────────────────────────────────────────────────

    /// <summary>For negative x: 0 when y is not an integer, 1 when an odd integer, 2 when even.
    /// Always 0 for x ≥ +0 (unused there).</summary>
    private static int PowExponentKind(int hx, int iy, uint ly)
    {
        if (hx >= 0) return 0;
        if (iy >= 0x43400000) return 2;
        if (iy < 0x3FF00000) return 0;
        var k = (iy >> 20) - 0x3FF;
        if (k > 20)
        {
            var j = (int)(ly >> (52 - k));
            return (j << (52 - k)) == (int)ly ? 2 - (j & 1) : 0;
        }
        if (ly != 0) return 0;
        var jj = iy >> (20 - k);
        return (jj << (20 - k)) == iy ? 2 - (jj & 1) : 0;
    }

    private const double Ivln2 = 1.44269504088896338700e+00;
    private const double Ivln2H = 1.44269502162933349609e+00;
    private const double Ivln2L = 1.92596299112661746887e-08;

    /// <summary>log2(ax) as t1 + t2 for |1 - ax| ≤ 2^-20 (the |y| &gt; 2^31 path).</summary>
    private static void PowLog2NearOne(double ax, out double t1, out double t2)
    {
        var t = ax - 1.0;
        var w = (t * t) * (0.5 - t * (0.3333333333333333333333 - t * 0.25));
        var u = Ivln2H * t;
        var v = t * Ivln2L - w * Ivln2;
        t1 = WithLow(u + v, 0);
        t2 = v - (t1 - u);
    }

    /// <summary>log2(ax) as t1 + t2 (t1 with its low word zeroed), general path.</summary>
    private static void PowLog2(double ax, int ix, out double t1, out double t2)
    {
        const double two53 = 9007199254740992.0;
        const double l1 = 5.99999999999994648725e-01;
        const double l2 = 4.28571428578550184252e-01;
        const double l3 = 3.33333329818377432918e-01;
        const double l4 = 2.72728123808534006489e-01;
        const double l5 = 2.30660745775561754067e-01;
        const double l6 = 2.06975017800338417784e-01;
        const double cp = 9.61796693925975554329e-01;
        const double cpH = 9.61796700954437255859e-01;
        const double cpL = -7.02846165095275826516e-09;
        const double dpH1 = 5.84962487220764160156e-01;
        const double dpL1 = 1.35003920212974897128e-08;

        var n = 0;
        if (ix < 0x00100000)
        {
            ax *= two53;
            n -= 53;
            ix = High(ax);
        }
        n += (ix >> 20) - 0x3FF;
        var j = ix & 0x000FFFFF;
        ix = j | 0x3FF00000;
        int k;
        if (j <= 0x3988E)
        {
            k = 0;
        }
        else if (j < 0xBB67A)
        {
            k = 1;
        }
        else
        {
            k = 0;
            n += 1;
            ix -= 0x00100000;
        }
        ax = WithHigh(ax, ix);
        var bp = k == 0 ? 1.0 : 1.5;
        var dpH = k == 0 ? 0.0 : dpH1;
        var dpL = k == 0 ? 0.0 : dpL1;

        var u = ax - bp;
        var v = 1.0 / (ax + bp);
        var ss = u * v;
        var sH = WithLow(ss, 0);
        var tH = FromWords(((ix >> 1) | 0x20000000) + 0x00080000 + (k << 18), 0);
        var tL = ax - (tH - bp);
        var sL = v * ((u - sH * tH) - sH * tL);
        var s2 = ss * ss;
        var r = s2 * s2 * (l1 + s2 * (l2 + s2 * (l3 + s2 * (l4 + s2 * (l5 + s2 * l6)))));
        r += sL * (sH + ss);
        s2 = sH * sH;
        tH = WithLow(3.0 + s2 + r, 0);
        tL = r - ((tH - 3.0) - s2);
        u = sH * tH;
        v = sL * tH + tL * ss;
        var pH = WithLow(u + v, 0);
        var pL = v - (pH - u);
        var zH = cpH * pH;
        var zL = cpL * pH + pL * cp + dpL;
        double t = n;
        t1 = WithLow(((zH + zL) + dpH) + t, 0);
        t2 = zL - (((t1 - t) - dpH) - zH);
    }

    /// <summary>s·2^(y·(t1 + t2)), where t1 + t2 = log2|x| and <paramref name="s"/> is the result
    /// sign (±1); overflows to s·∞ and underflows to s·0.</summary>
    private static double PowExp2(double y, double t1, double t2, double s)
    {
        const double ovt = 8.0085662595372944372e-17;
        const double lg2 = 6.93147180559945286227e-01;
        const double lg2H = 6.93147182464599609375e-01;
        const double lg2L = -1.90465429995776804525e-09;

        var y1 = WithLow(y, 0);
        var pL = (y - y1) * t1 + y * t2;
        var pH = y1 * t1;
        var z = pL + pH;
        var j = High(z);
        var i = unchecked((int)Low(z));
        if (j >= 0x40900000)
        {
            if (((j - 0x40900000) | i) != 0) return s * Huge * Huge;
            if (pL + ovt > z - pH) return s * Huge * Huge;
        }
        else if ((j & 0x7FFFFFFF) >= 0x4090CC00)
        {
            if ((unchecked((uint)j - 0xC090CC00u) | (uint)i) != 0) return s * Tiny * Tiny;
            if (pL <= z - pH) return s * Tiny * Tiny;
        }

        i = j & 0x7FFFFFFF;
        var k = (i >> 20) - 0x3FF;
        var n = 0;
        if (i > 0x3FE00000)
        {
            n = j + (0x00100000 >> (k + 1));
            k = ((n & 0x7FFFFFFF) >> 20) - 0x3FF;
            var tn = FromWords(n & ~(0x000FFFFF >> k), 0);
            n = ((n & 0x000FFFFF) | 0x00100000) >> (20 - k);
            if (j < 0) n = -n;
            pH -= tn;
        }
        var t = WithLow(pL + pH, 0);
        var u = t * lg2H;
        var v = (pL - (t - pH)) * lg2 + t * lg2L;
        z = u + v;
        var w = v - (z - u);
        t = z * z;
        var t1R = z - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        var r = (z * t1R) / ((t1R - 2.0) - (w + z * w));
        z = 1.0 - (r - z);
        var shift = unchecked((int)((uint)n << 20));
        z = ((High(z) + shift) >> 20) <= 0 ? ScaleB(z, n) : WithHigh(z, High(z) + shift);
        return s * z;
    }
}
