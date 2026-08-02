using Unity.Mathematics;

namespace Ring.Simulation.Core
{
    /// FNV-1a 64-bit over 8-byte words in canonical field order.
    public static class StateHash64
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        public static ulong Begin() => OffsetBasis;

        public static ulong Add(ulong hash, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= Prime;
            }
            return hash;
        }

        public static ulong Add(ulong hash, float value)
        {
            if (value == 0f) value = 0f; // -0.0 -> +0.0 (canonicalize sign bit)
            return Add(hash, (ulong)math.asuint(value));
        }

        public static ulong Add(ulong hash, float2 value)
        {
            return Add(Add(hash, value.x), value.y);
        }

        public static ulong Add(ulong hash, int value) => Add(hash, (ulong)(uint)value);

        public static ulong Add(ulong hash, bool value) => Add(hash, value ? 1UL : 0UL);
    }
}
