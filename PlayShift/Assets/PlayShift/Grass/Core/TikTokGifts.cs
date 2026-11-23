using System;
using System.Collections.Generic;

namespace PlayShift.Grass
{
    public enum GiftActionType
    {
        /// <summary>Regrows previously cut grass (units).</summary>
        AddGrass,
        RemoveGrass,
        IncreaseHeight,
        /// <summary>Timed trimmer modifiers; Amount = magnitude, DurationSeconds = how long.</summary>
        SpeedBoost,
        RadiusBoost,
        SlowTrimmer,
        MultiBlade,
    }

    /// <summary>Maps one TikTok gift name to an effect on the current level.</summary>
    [Serializable]
    public sealed class GiftRule
    {
        public string GiftName;
        public GiftActionType Action;

        /// <summary>Grass units for Add/Remove, centimetres for IncreaseHeight. Per gift.</summary>
        public double Amount;

        /// <summary>When true, a combo of N gifts applies Amount N times (for modifiers: the duration N times).</summary>
        public bool ScaleByRepeatCount = true;

        /// <summary>Duration for the timed trimmer modifiers, in seconds.</summary>
        public float DurationSeconds = 10f;

        public GiftRule(string giftName, GiftActionType action, double amount, bool scaleByRepeatCount = true, float durationSeconds = 10f)
        {
            GiftName = giftName;
            Action = action;
            Amount = amount;
            ScaleByRepeatCount = scaleByRepeatCount;
            DurationSeconds = durationSeconds;
        }

        public bool IsModifier => Action == GiftActionType.SpeedBoost || Action == GiftActionType.RadiusBoost ||
                                  Action == GiftActionType.SlowTrimmer || Action == GiftActionType.MultiBlade;
    }

    /// <summary>
    /// Connector-agnostic gift event. Fill it from whatever TikTok LIVE bridge the game uses
    /// (TikTok-Live-Connector, TikFinity, a websocket relay, ...).
    /// </summary>
    public struct TikTokGiftEvent
    {
        public string UserName;
        public string GiftName;
        public int RepeatCount;

        /// <summary>
        /// TikTok sends streakable gifts (giftType 1) repeatedly while the combo runs, with a growing
        /// repeatCount, then once more with repeatEnd = true. Mark the in-progress ones so the combo
        /// is applied once, at the end, instead of being double counted.
        /// </summary>
        public bool IsStreakInProgress;

        public TikTokGiftEvent(string userName, string giftName, int repeatCount = 1, bool isStreakInProgress = false)
        {
            UserName = userName;
            GiftName = giftName;
            RepeatCount = repeatCount;
            IsStreakInProgress = isStreakInProgress;
        }
    }

    public enum GiftOutcome
    {
        Applied,
        IgnoredStreakInProgress,
        UnknownGift,
        NotPlaying,
        NoEffect,
    }

    public readonly struct GiftApplication
    {
        public readonly GiftOutcome Outcome;
        public readonly TikTokGiftEvent Gift;
        public readonly GiftActionType Action;
        public readonly double Requested;
        public readonly double Applied;
        /// <summary>For timed modifiers: seconds applied.</summary>
        public readonly float Duration;

        public GiftApplication(GiftOutcome outcome, TikTokGiftEvent gift, GiftActionType action, double requested, double applied, float duration = 0f)
        {
            Outcome = outcome;
            Gift = gift;
            Action = action;
            Requested = requested;
            Applied = applied;
            Duration = duration;
        }

        public bool WasApplied => Outcome == GiftOutcome.Applied;

        /// <summary>Short line for an on-screen gift feed, e.g. "@sam sent Rose x5: +12,500 grass".</summary>
        public string Describe()
        {
            string who = string.IsNullOrEmpty(Gift.UserName) ? "Someone" : "@" + Gift.UserName;
            string what = Gift.RepeatCount > 1 ? $"{Gift.GiftName} x{Gift.RepeatCount}" : Gift.GiftName;
            switch (Action)
            {
                case GiftActionType.AddGrass: return $"{who} sent {what}: +{(long)Applied:N0} grass";
                case GiftActionType.RemoveGrass: return $"{who} sent {what}: -{(long)Applied:N0} grass";
                case GiftActionType.IncreaseHeight: return $"{who} sent {what}: grass +{Applied:0.#} cm taller";
                case GiftActionType.SpeedBoost: return $"{who} sent {what}: SPEED BOOST {Duration:0}s";
                case GiftActionType.RadiusBoost: return $"{who} sent {what}: WIDER CUT {Duration:0}s";
                case GiftActionType.SlowTrimmer: return $"{who} sent {what}: trimmer SLOWED {Duration:0}s";
                default: return $"{who} sent {what}: +{Applied:0} BLADE {Duration:0}s";
            }
        }
    }

    /// <summary>Applies TikTok gifts to the running level according to a configurable rule table.</summary>
    public sealed class TikTokGiftRouter
    {
        private readonly GrassProgression _progression;
        private readonly TrimmerModifiers _modifiers;
        private readonly Dictionary<string, GiftRule> _rules = new Dictionary<string, GiftRule>(StringComparer.OrdinalIgnoreCase);

        public TikTokGiftRouter(GrassProgression progression, IEnumerable<GiftRule> rules = null, TrimmerModifiers modifiers = null)
        {
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
            _modifiers = modifiers;
            SetRules(rules ?? CreateDefaultRules());
        }

        public bool IgnoreStreakInProgress { get; set; } = true;

        public void SetRules(IEnumerable<GiftRule> rules)
        {
            _rules.Clear();
            foreach (var rule in rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.GiftName)) continue;
                _rules[rule.GiftName.Trim()] = rule;
            }
        }

        public bool TryGetRule(string giftName, out GiftRule rule)
        {
            rule = null;
            return !string.IsNullOrWhiteSpace(giftName) && _rules.TryGetValue(giftName.Trim(), out rule);
        }

        public GiftApplication Handle(TikTokGiftEvent gift)
        {
            if (gift.RepeatCount < 1) gift.RepeatCount = 1;
            if (!TryGetRule(gift.GiftName, out var rule))
                return new GiftApplication(GiftOutcome.UnknownGift, gift, default, 0, 0);
            if (IgnoreStreakInProgress && gift.IsStreakInProgress)
                return new GiftApplication(GiftOutcome.IgnoredStreakInProgress, gift, rule.Action, 0, 0);

            if (rule.IsModifier)
            {
                float duration = rule.DurationSeconds * (rule.ScaleByRepeatCount ? gift.RepeatCount : 1);
                if (!_progression.IsPlaying)
                    return new GiftApplication(GiftOutcome.NotPlaying, gift, rule.Action, rule.Amount, 0, duration);
                if (_modifiers == null || rule.Amount <= 0 || duration <= 0)
                    return new GiftApplication(GiftOutcome.NoEffect, gift, rule.Action, rule.Amount, 0, duration);
                _modifiers.Add(ToModifier(rule.Action), (float)rule.Amount, duration);
                return new GiftApplication(GiftOutcome.Applied, gift, rule.Action, rule.Amount, rule.Amount, duration);
            }

            double requested = rule.Amount * (rule.ScaleByRepeatCount ? gift.RepeatCount : 1);
            if (!_progression.IsPlaying)
                return new GiftApplication(GiftOutcome.NotPlaying, gift, rule.Action, requested, 0);

            double applied;
            switch (rule.Action)
            {
                case GiftActionType.AddGrass: applied = _progression.AddGrass(ToUnits(requested)); break;
                case GiftActionType.RemoveGrass: applied = _progression.RemoveGrass(ToUnits(requested)); break;
                default: applied = _progression.IncreaseHeight((float)requested); break;
            }

            var outcome = applied > 0 ? GiftOutcome.Applied : GiftOutcome.NoEffect;
            return new GiftApplication(outcome, gift, rule.Action, requested, applied);
        }

        private static TrimmerModifierType ToModifier(GiftActionType a)
        {
            switch (a)
            {
                case GiftActionType.SpeedBoost: return TrimmerModifierType.SpeedBoost;
                case GiftActionType.RadiusBoost: return TrimmerModifierType.RadiusBoost;
                case GiftActionType.SlowTrimmer: return TrimmerModifierType.Slow;
                default: return TrimmerModifierType.MultiBlade;
            }
        }

        private static long ToUnits(double value) => value <= 0 ? 0 : (long)Math.Min(Math.Round(value), long.MaxValue / 2);

        /// <summary>Starter table; tune amounts per stream in the settings asset.</summary>
        public static List<GiftRule> CreateDefaultRules()
        {
            return new List<GiftRule>
            {
                new GiftRule("Rose", GiftActionType.AddGrass, 2_500),
                new GiftRule("Finger Heart", GiftActionType.AddGrass, 10_000),
                new GiftRule("Doughnut", GiftActionType.AddGrass, 50_000),
                new GiftRule("TikTok", GiftActionType.RemoveGrass, 5_000),
                new GiftRule("GG", GiftActionType.RemoveGrass, 5_000),
                new GiftRule("Ice Cream Cone", GiftActionType.RemoveGrass, 15_000),
                new GiftRule("Perfume", GiftActionType.IncreaseHeight, 5),
                new GiftRule("Galaxy", GiftActionType.IncreaseHeight, 20),
                new GiftRule("Hand Hearts", GiftActionType.SpeedBoost, 0.5, durationSeconds: 15f),
                new GiftRule("Sunglasses", GiftActionType.RadiusBoost, 0.6, durationSeconds: 15f),
                new GiftRule("Snail", GiftActionType.SlowTrimmer, 0.4, durationSeconds: 10f),
                new GiftRule("Corgi", GiftActionType.MultiBlade, 1, durationSeconds: 20f),
            };
        }
    }
}
