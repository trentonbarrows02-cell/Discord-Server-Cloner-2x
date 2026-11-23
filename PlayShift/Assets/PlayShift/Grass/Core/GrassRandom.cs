namespace PlayShift.Grass
{
    /// <summary>Small deterministic RNG (SplitMix64) so level layouts are reproducible and testable.</summary>
    public struct GrassRandom
    {
        private ulong _state;

        public GrassRandom(int seed)
        {
            _state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
        }

        public ulong NextULong()
        {
            ulong z = (_state += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float NextFloat() => (NextULong() >> 40) * (1f / (1UL << 24));

        /// <summary>Uniform in [0, maxExclusive).</summary>
        public int NextInt(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(NextULong() % (ulong)maxExclusive);

        public float Range(float min, float max) => min + (max - min) * NextFloat();

        /// <summary>Stateless hash of a lattice point to [0, 1). Used for smooth height noise.</summary>
        public static float Hash01(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)x * 0x8DA6B343u ^ (uint)z * 0xD8163841u ^ (uint)seed * 0xCB1AB31Fu;
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return (h >> 8) * (1f / (1u << 24));
            }
        }

        /// <summary>Smooth 2D value noise in [0, 1].</summary>
        public static float ValueNoise(float x, float z, int seed)
        {
            int x0 = FloorToInt(x), z0 = FloorToInt(z);
            float fx = x - x0, fz = z - z0;
            float ux = fx * fx * (3f - 2f * fx);
            float uz = fz * fz * (3f - 2f * fz);
            float a = Hash01(x0, z0, seed), b = Hash01(x0 + 1, z0, seed);
            float c = Hash01(x0, z0 + 1, seed), d = Hash01(x0 + 1, z0 + 1, seed);
            float ab = a + (b - a) * ux;
            float cd = c + (d - c) * ux;
            return ab + (cd - ab) * uz;
        }

        private static int FloorToInt(float v) => v >= 0f ? (int)v : (int)v - 1;
    }
}
