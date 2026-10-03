using System.Buffers.Binary;
using System.Numerics;

namespace ScumStudio.Formats.AssetRegistry;

/// <summary>
/// Google CityHash64 v1.1.1 (the variant Unreal Engine 4.27 ships in <c>CityHash.cpp</c> and the Python
/// <c>cityhash</c> module used by <c>assetreg.py</c> implements; v1.1 differs for inputs over 64 bytes).
/// Used for AssetRegistry name batch hashes.
/// </summary>
public static class CityHash
{
    private const ulong K0 = 0xc3a5c85c97cb3127UL;
    private const ulong K1 = 0xb492b66fbe98f273UL;
    private const ulong K2 = 0x9ae16a3b2f90404fUL;
    private const ulong KMul = 0x9ddfea08eb382d69UL;

    /// <summary>CityHash64 of a byte string.</summary>
    public static ulong CityHash64(ReadOnlySpan<byte> s)
    {
        var len = (ulong)s.Length;
        if (len <= 32)
        {
            return len <= 16 ? HashLen0To16(s) : HashLen17To32(s);
        }

        if (len <= 64)
        {
            return HashLen33To64(s);
        }

        var x = Fetch64(s, s.Length - 40);
        var y = Fetch64(s, s.Length - 16) + Fetch64(s, s.Length - 56);
        var z = HashLen16(Fetch64(s, s.Length - 48) + len, Fetch64(s, s.Length - 24));
        var v = WeakHashLen32WithSeeds(s, s.Length - 64, len, z);
        var w = WeakHashLen32WithSeeds(s, s.Length - 32, y + K1, x);
        x = (x * K1) + Fetch64(s, 0);

        // Decrease len to the nearest multiple of 64 and process 64-byte chunks.
        var remaining = (s.Length - 1) & ~63;
        var p = 0;
        do
        {
            x = Rotate(x + y + v.First + Fetch64(s, p + 8), 37) * K1;
            y = Rotate(y + v.Second + Fetch64(s, p + 48), 42) * K1;
            x ^= w.Second;
            y += v.First + Fetch64(s, p + 40);
            z = Rotate(z + w.First, 33) * K1;
            v = WeakHashLen32WithSeeds(s, p, v.Second * K1, x + w.First);
            w = WeakHashLen32WithSeeds(s, p + 32, z + w.Second, y + Fetch64(s, p + 16)); // v1.1.1 (v1.1 had z + y)
            (z, x) = (x, z);
            p += 64;
            remaining -= 64;
        }
        while (remaining != 0);

        return HashLen16(HashLen16(v.First, w.First) + (ShiftMix(y) * K1) + z, HashLen16(v.Second, w.Second) + x);
    }

    private static ulong Fetch64(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt64LittleEndian(s[p..]);

    private static ulong Fetch32(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);

    private static ulong Rotate(ulong val, int shift) => shift == 0 ? val : BitOperations.RotateRight(val, shift);

    private static ulong ShiftMix(ulong val) => val ^ (val >> 47);

    private static ulong HashLen16(ulong u, ulong v)
    {
        // Hash128to64(uint128(u, v))
        var a = (u ^ v) * KMul;
        a ^= a >> 47;
        var b = (v ^ a) * KMul;
        b ^= b >> 47;
        b *= KMul;
        return b;
    }

    private static ulong HashLen16(ulong u, ulong v, ulong mul)
    {
        var a = (u ^ v) * mul;
        a ^= a >> 47;
        var b = (v ^ a) * mul;
        b ^= b >> 47;
        b *= mul;
        return b;
    }

    private static ulong HashLen0To16(ReadOnlySpan<byte> s)
    {
        var len = (ulong)s.Length;
        if (len >= 8)
        {
            var mul = K2 + (len * 2);
            var a = Fetch64(s, 0) + K2;
            var b = Fetch64(s, s.Length - 8);
            var c = (Rotate(b, 37) * mul) + a;
            var d = (Rotate(a, 25) + b) * mul;
            return HashLen16(c, d, mul);
        }

        if (len >= 4)
        {
            var mul = K2 + (len * 2);
            var a = Fetch32(s, 0);
            return HashLen16(len + (a << 3), Fetch32(s, s.Length - 4), mul);
        }

        if (len > 0)
        {
            uint a = s[0];
            uint b = s[s.Length >> 1];
            uint c = s[s.Length - 1];
            var y = a + (b << 8);
            var z = (uint)len + (c << 2);
            return ShiftMix((y * K2) ^ (z * K0)) * K2;
        }

        return K2;
    }

    private static ulong HashLen17To32(ReadOnlySpan<byte> s)
    {
        var len = (ulong)s.Length;
        var mul = K2 + (len * 2);
        var a = Fetch64(s, 0) * K1;
        var b = Fetch64(s, 8);
        var c = Fetch64(s, s.Length - 8) * mul;
        var d = Fetch64(s, s.Length - 16) * K2;
        return HashLen16(Rotate(a + b, 43) + Rotate(c, 30) + d, a + Rotate(b + K2, 18) + c, mul);
    }

    private static ulong HashLen33To64(ReadOnlySpan<byte> s)
    {
        var len = (ulong)s.Length;
        var mul = K2 + (len * 2);
        var a = Fetch64(s, 0) * K2;
        var b = Fetch64(s, 8);
        var c = Fetch64(s, s.Length - 24);
        var d = Fetch64(s, s.Length - 32);
        var e = Fetch64(s, 16) * K2;
        var f = Fetch64(s, 24) * 9;
        var g = Fetch64(s, s.Length - 8);
        var h = Fetch64(s, s.Length - 16) * mul;
        var u = Rotate(a + g, 43) + ((Rotate(b, 30) + c) * 9);
        var v = ((a + g) ^ d) + f + 1;
        var w = BinaryPrimitives.ReverseEndianness((u + v) * mul) + h;
        var x = Rotate(e + f, 42) + c;
        var y = (BinaryPrimitives.ReverseEndianness((v + w) * mul) + g) * mul;
        var z = e + f + c;
        a = BinaryPrimitives.ReverseEndianness(((x + z) * mul) + y) + b;
        b = ShiftMix(((z + a) * mul) + d + h) * mul;
        return b + x;
    }

    private static (ulong First, ulong Second) WeakHashLen32WithSeeds(ReadOnlySpan<byte> s, int p, ulong a, ulong b)
    {
        var w = Fetch64(s, p);
        var x = Fetch64(s, p + 8);
        var y = Fetch64(s, p + 16);
        var z = Fetch64(s, p + 24);
        a += w;
        b = Rotate(b + a + z, 21);
        var c = a;
        a += x;
        a += y;
        b += Rotate(a, 44);
        return (a + z, b + c);
    }
}
