using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Bridge between the game's TikTok LIVE connection and the grass system. Gifts may arrive on any
    /// thread (websocket callbacks usually do); they are queued and applied on the main thread.
    ///
    /// Hook up whichever connector you use:
    ///   receiver.EnqueueGift(user, giftName, repeatCount, isStreakInProgress);
    ///   receiver.EnqueueGiftJson(json);   // TikTok-Live-Connector / TikFinity style "gift" payloads
    /// Gift effects (add grass / remove grass / taller grass) come from the settings asset's gift table.
    /// </summary>
    public sealed class TikTokGiftReceiver : MonoBehaviour
    {
        [Tooltip("Defaults to GrassLevelManager.Instance.")]
        [SerializeField] private GrassLevelManager manager;
        [Tooltip("Log every gift and what it did.")]
        [SerializeField] private bool logGifts = true;

        [Header("Testing without a live stream")]
        [Tooltip("F5 = first Add Grass gift, F6 = first Remove Grass gift, F7 = first Increase Height gift.")]
        [SerializeField] private bool enableDebugKeys = true;

        private readonly ConcurrentQueue<TikTokGiftEvent> _queue = new ConcurrentQueue<TikTokGiftEvent>();
        private TikTokGiftRouter _router;

        /// <summary>Raised on the main thread after each gift is processed.</summary>
        public event Action<GiftApplication> GiftProcessed;

        private GrassLevelManager Manager => manager != null ? manager : GrassLevelManager.Instance;

        /// <summary>Thread-safe.</summary>
        public void EnqueueGift(string userName, string giftName, int repeatCount = 1, bool isStreakInProgress = false)
        {
            _queue.Enqueue(new TikTokGiftEvent(userName, giftName, repeatCount, isStreakInProgress));
        }

        /// <summary>Thread-safe.</summary>
        public void EnqueueGift(TikTokGiftEvent gift) => _queue.Enqueue(gift);

        [Serializable]
        private sealed class GiftJson
        {
            public string uniqueId;
            public string nickname;
            public string giftName;
            public int repeatCount = 1;
            public bool repeatEnd;
            public int giftType;
        }

        /// <summary>
        /// Thread-safe. Accepts the common TikTok-Live-Connector "gift" shape:
        /// { "uniqueId", "nickname", "giftName", "repeatCount", "repeatEnd", "giftType" }.
        /// giftType 1 = streakable; those are applied once, when repeatEnd is true.
        /// </summary>
        public bool EnqueueGiftJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            GiftJson data;
            try { data = JsonUtility.FromJson<GiftJson>(json); }
            catch (Exception e)
            {
                Debug.LogWarning("[PlayShift Grass] Could not parse gift JSON: " + e.Message);
                return false;
            }
            if (data == null || string.IsNullOrEmpty(data.giftName)) return false;
            string user = !string.IsNullOrEmpty(data.nickname) ? data.nickname : data.uniqueId;
            bool inProgress = data.giftType == 1 && !data.repeatEnd;
            EnqueueGift(user, data.giftName, Mathf.Max(1, data.repeatCount), inProgress);
            return true;
        }

        private void Update()
        {
            var m = Manager;
            if (m == null || m.Progression == null) return;
            if (_router == null)
            {
                _router = new TikTokGiftRouter(m.Progression, m.Settings.BuildGiftRules())
                {
                    IgnoreStreakInProgress = m.Settings.ignoreStreakInProgress,
                };
            }

            HandleDebugKeys(m);

            while (_queue.TryDequeue(out var gift))
            {
                var result = _router.Handle(gift);
                if (logGifts)
                    Debug.Log($"[PlayShift Grass] Gift '{gift.GiftName}' x{gift.RepeatCount} from {gift.UserName}: {result.Outcome} " +
                              (result.WasApplied ? $"({result.Action} {result.Applied:0.##})" : string.Empty));
                if (result.WasApplied && m.Hud != null) m.Hud.ShowGiftToast(result);
                GiftProcessed?.Invoke(result);
            }
        }

        /// <summary>Re-reads the gift table from the settings asset (e.g. after editing it in play mode).</summary>
        public void ReloadRules()
        {
            var m = Manager;
            if (_router != null && m != null) _router.SetRules(m.Settings.BuildGiftRules());
        }

        private void HandleDebugKeys(GrassLevelManager m)
        {
            if (!enableDebugKeys) return;
            if (KeyDown(0)) EnqueueFirst(m, GiftActionType.AddGrass);
            if (KeyDown(1)) EnqueueFirst(m, GiftActionType.RemoveGrass);
            if (KeyDown(2)) EnqueueFirst(m, GiftActionType.IncreaseHeight);
        }

        private void EnqueueFirst(GrassLevelManager m, GiftActionType action)
        {
            foreach (var rule in m.Settings.giftRules)
                if (rule != null && rule.action == action)
                {
                    EnqueueGift("debug", rule.giftName);
                    return;
                }
            Debug.LogWarning($"[PlayShift Grass] No gift rule with action {action} to simulate.");
        }

        private static bool KeyDown(int slot)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return false;
            switch (slot)
            {
                case 0: return kb.f5Key.wasPressedThisFrame;
                case 1: return kb.f6Key.wasPressedThisFrame;
                default: return kb.f7Key.wasPressedThisFrame;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            switch (slot)
            {
                case 0: return Input.GetKeyDown(KeyCode.F5);
                case 1: return Input.GetKeyDown(KeyCode.F6);
                default: return Input.GetKeyDown(KeyCode.F7);
            }
#else
            return false;
#endif
        }
    }
}
