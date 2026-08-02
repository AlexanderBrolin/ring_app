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
    }
}
