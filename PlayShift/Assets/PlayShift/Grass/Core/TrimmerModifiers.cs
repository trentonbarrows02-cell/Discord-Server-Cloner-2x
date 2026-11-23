using System;
using System.Collections.Generic;

namespace PlayShift.Grass
{
    public enum TrimmerModifierType
    {
        /// <summary>Faster walking and blade speed. Magnitude 0.5 = +50%.</summary>
        SpeedBoost,
        /// <summary>Wider cut. Magnitude 0.5 = +50% radius.</summary>
        RadiusBoost,
        /// <summary>Slower walking and blades. Magnitude 0.3 = -30% (never below 20%).</summary>
        Slow,
        /// <summary>Extra cutting heads beside the main one. Magnitude = number of heads added.</summary>
        MultiBlade,
    }

    /// <summary>
    /// Timed gameplay modifiers for the trimmer (gift effects, power-ups). Engine-free: the Unity layer reads
    /// the combined multipliers each frame; nothing here knows about TikTok or networking.
    /// </summary>
    public sealed class TrimmerModifiers
    {
        public const int MaxExtraBlades = 3;
        public const float MinSpeedMultiplier = 0.2f;

        private readonly List<Active> _active = new List<Active>();

        private struct Active
        {
            public TrimmerModifierType Type;
            public float Magnitude;
            public float Remaining;
        }

        /// <summary>Raised when a modifier starts or ends.</summary>
        public event Action Changed;

        public float MoveSpeedMultiplier { get; private set; } = 1f;
        public float SpinMultiplier { get; private set; } = 1f;
        public float RadiusMultiplier { get; private set; } = 1f;
        public int ExtraBlades { get; private set; }
        public int ActiveCount => _active.Count;

        /// <summary>Starts a modifier for durationSeconds (stacks with others of the same type).</summary>
        public void Add(TrimmerModifierType type, float magnitude, float durationSeconds)
        {
            if (durationSeconds <= 0f || magnitude <= 0f) return;
            _active.Add(new Active { Type = type, Magnitude = magnitude, Remaining = durationSeconds });
            Recompute();
        }

        public void Tick(float deltaSeconds)
        {
            if (deltaSeconds <= 0f || _active.Count == 0) return;
            bool expired = false;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var a = _active[i];
                a.Remaining -= deltaSeconds;
                if (a.Remaining <= 0f) { _active.RemoveAt(i); expired = true; }
                else _active[i] = a;
            }
            if (expired) Recompute();
        }

        public void Clear()
        {
            if (_active.Count == 0) return;
            _active.Clear();
            Recompute();
        }

        /// <summary>Seconds left on the longest-running modifier of a type, or 0.</summary>
        public float RemainingSeconds(TrimmerModifierType type)
        {
            float r = 0f;
            foreach (var a in _active) if (a.Type == type && a.Remaining > r) r = a.Remaining;
            return r;
        }

        private void Recompute()
        {
            float speed = 1f, radius = 1f;
            int blades = 0;
            foreach (var a in _active)
            {
                switch (a.Type)
                {
                    case TrimmerModifierType.SpeedBoost: speed *= 1f + a.Magnitude; break;
                    case TrimmerModifierType.Slow: speed *= Math.Max(0f, 1f - a.Magnitude); break;
                    case TrimmerModifierType.RadiusBoost: radius *= 1f + a.Magnitude; break;
                    case TrimmerModifierType.MultiBlade: blades += (int)Math.Round(a.Magnitude); break;
                }
            }
            MoveSpeedMultiplier = Math.Max(MinSpeedMultiplier, speed);
            SpinMultiplier = MoveSpeedMultiplier;
            RadiusMultiplier = radius;
            ExtraBlades = Math.Min(MaxExtraBlades, blades);
            Changed?.Invoke();
        }
    }
}
