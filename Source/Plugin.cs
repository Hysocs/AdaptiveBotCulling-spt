using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using EFT;
using FastAnimatorSystem;
using HarmonyLib;
using UnityEngine;

namespace AdaptiveBotCulling
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.hysocs.adaptivebotculling";
        public const string Name = "Adaptive Bot Culling";
        public const string Version = "1.5.0";

        private const float VisibilityHoldSeconds = 1.5f;

        private static readonly int VisibilityMask = BuildVisibilityMask();

        private sealed class BodyAnimatorState
        {
            public Animator Animator;
            public bool OriginalEnabled;
            public float NextRefresh;
            public float NextVisibilityUpdate;
            public Transform Head;
            public Transform Chest;
            public bool IsVisible;
            public bool RayVisible;
            public float ActiveUntil;
            public bool MovementStopped;
            public readonly HashSet<int> ColliderIds = new HashSet<int>();
        }

        private static Plugin _instance;
        private static readonly AccessTools.FieldRef<AICoreAgent<BotLogicDecision>,
            AICoreStrategy<BotLogicDecision>> BrainStrategy =
            AccessTools.FieldRefAccess<AICoreAgent<BotLogicDecision>,
                AICoreStrategy<BotLogicDecision>>("_strategy");
        private static readonly AccessTools.FieldRef<BaseBrain, BotOwner>
            BrainOwner = AccessTools.FieldRefAccess<BaseBrain, BotOwner>("_owner");
        private readonly HashSet<BotOwner> _bots = new HashSet<BotOwner>();
        private readonly List<BotOwner> _removedBots = new List<BotOwner>();
        private readonly Dictionary<Player, BodyAnimatorState> _bodyStates =
            new Dictionary<Player, BodyAnimatorState>();
        private readonly RaycastHit[] _visibilityHits = new RaycastHit[64];
        private readonly HashSet<int> _localPlayerColliderIds = new HashSet<int>();
        private readonly HashSet<int> _transparentColliderIds = new HashSet<int>();
        private readonly HashSet<int> _opaqueColliderIds = new HashSet<int>();

        private ConfigEntry<bool> _disableAi;
        private ConfigEntry<bool> _onlySuppressCulledBots;
        private ConfigEntry<bool> _disableBodyAnimator;
        private Harmony _harmony;
        private Player _mainPlayer;
        private Camera _camera;
        private float _nextCameraRefresh;

        private void Awake()
        {
            _instance = this;
            _disableAi = Config.Bind("Distance culling", "Disable AI", true,
                "Suppresses native EFT bot updates at every distance. Only the minimal mover and steering pipeline remains.");
            _onlySuppressCulledBots = Config.Bind("Distance culling",
                "Only disable AI when culled", true,
                "When enabled, visible/recently visible bots are untouched and run native EFT AI; only bots outside the detected visible region are suppressed.");
            _disableBodyAnimator = Config.Bind("Distance culling",
                "Disable body Animator", true,
                "Disables EFT's expensive body output Animator while a bot is hidden.");
            _disableAi.SettingChanged += OnDisableAiSettingChanged;
            _onlySuppressCulledBots.SettingChanged += OnDisableAiSettingChanged;
            _disableBodyAnimator.SettingChanged += OnBodySettingChanged;

            _harmony = new Harmony(Guid);
            Patch(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.PreActivate)), nameof(RegisterBot), false);
            Patch(AccessTools.Method(typeof(BotOwner), "method_10", Type.EmptyTypes), nameof(BotActivated), false);
            Patch(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual)), nameof(AllowBotManualUpdate), true);
            Patch(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.FixedUpdate)), nameof(AllowBotFixedUpdate), true);
            Patch(AccessTools.Method(typeof(AICoreAgent<BotLogicDecision>),
                nameof(AICoreAgent<BotLogicDecision>.Update)), nameof(AllowBrainUpdate), true);
            Patch(AccessTools.Method(typeof(Player), nameof(Player.OnDead),
                new[] { typeof(EDamageType) }), nameof(PrepareDeathPose), true);
            Patch(AccessTools.Method(typeof(Player), nameof(Player.CreateCorpse),
                Type.EmptyTypes), nameof(PrepareCorpsePose), true);
            Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.DoWorldTick)), nameof(OnWorldTick), false);

            Logger.LogInfo(Name + " " + Version +
                " loaded: original AI suppression and hidden Animator culling only.");
        }

        private void Patch(MethodBase target, string methodName, bool prefix)
        {
            if (target == null)
            {
                Logger.LogError("Missing EFT patch target for " + methodName);
                return;
            }
            HarmonyMethod patch = new HarmonyMethod(
                AccessTools.Method(typeof(Plugin), methodName));
            _harmony.Patch(target, prefix ? patch : null, prefix ? null : patch);
        }

        private static void RegisterBot(BotOwner __instance)
        {
            if (_instance != null && __instance != null)
                _instance._bots.Add(__instance);
        }

        private static void BotActivated(BotOwner __instance)
        {
            if (_instance == null || __instance == null)
                return;
            _instance._bots.Add(__instance);
            Player player = __instance.GetPlayer;
            _instance.UpdateBodyAnimator(player, true);
            _instance.UpdateVisibility(player);
            _instance.UpdateBotSuppression(__instance, player);
        }

        private static bool AllowBotManualUpdate(BotOwner __instance)
        {
            if (_instance == null || __instance == null ||
                __instance.BotState != EBotState.Active ||
                !_instance.ShouldDisableAi(__instance.GetPlayer))
                return true;

            Player player = __instance.GetPlayer;
            _instance.PrepareBotForGlobalSuppression(__instance, player, false);
            if (__instance.Mover != null)
            {
                __instance.Mover.ManualUpdate();
                if (player.UpdateQueue == EUpdateQueue.Update)
                {
                    __instance.Mover.ManualFixedUpdate();
                    __instance.Steering?.ManualFixedUpdate();
                }
            }
            return false;
        }

        private static bool AllowBotFixedUpdate(BotOwner __instance)
        {
            if (_instance == null || __instance == null ||
                __instance.BotState != EBotState.Active ||
                !_instance.ShouldDisableAi(__instance.GetPlayer))
                return true;

            Player player = __instance.GetPlayer;
            _instance.PrepareBotForGlobalSuppression(__instance, player, false);
            if (player.UpdateQueue == EUpdateQueue.FixedUpdate)
            {
                __instance.Steering?.ManualFixedUpdate();
                __instance.Mover?.ManualFixedUpdate();
            }
            return false;
        }

        private static bool AllowBrainUpdate(
            AICoreAgent<BotLogicDecision> __instance)
        {
            if (_instance == null || __instance == null ||
                !_instance._disableAi.Value)
                return true;
            BaseBrain brain = BrainStrategy(__instance) as BaseBrain;
            BotOwner owner = brain != null ? BrainOwner(brain) : null;
            Player player = owner?.GetPlayer;
            return brain == null || !_instance.ShouldDisableAi(player);
        }

        private static int BuildVisibilityMask()
        {
            int mask = Physics.DefaultRaycastLayers;
            foreach (string layerName in new[] { "Grass", "Foliage" })
            {
                int layer = LayerMask.NameToLayer(layerName);
                if (layer >= 0)
                    mask &= ~(1 << layer);
            }
            return mask;
        }

        private static void PrepareDeathPose(Player __instance)
        {
            _instance?.WakeAndStampDeathPose(__instance, true);
        }

        private static void PrepareCorpsePose(Player __instance)
        {
            if (_instance == null)
                return;
            _instance.WakeAndStampDeathPose(__instance, false);
            _instance.ReleaseDeathAnimation(__instance);
        }

        private static void OnWorldTick(GameWorld __instance, float dt)
        {
            if (_instance == null)
                return;
            Player nextMainPlayer = __instance != null
                ? __instance.MainPlayer : null;
            if (_instance._mainPlayer != nextMainPlayer)
                _instance.AttachMainPlayer(nextMainPlayer);
            _instance.Tick();
        }

        private void Tick()
        {
            RefreshCamera();
            _removedBots.Clear();
            foreach (BotOwner bot in _bots)
            {
                if (bot == null)
                {
                    _removedBots.Add(bot);
                    continue;
                }
                Player player = bot.GetPlayer;
                if (!IsAlive(player))
                    continue;
                UpdateVisibility(player);
                UpdateBotSuppression(bot, player);
                UpdateBodyAnimator(player, false);
            }
            foreach (BotOwner bot in _removedBots)
                _bots.Remove(bot);
        }

        private bool ShouldDisableAi(Player player)
        {
            if (!_disableAi.Value || !IsAlive(player))
                return false;
            if (!_onlySuppressCulledBots.Value)
                return true;
            return _bodyStates.TryGetValue(player, out BodyAnimatorState state) &&
                   !state.IsVisible;
        }

        private void UpdateBotSuppression(BotOwner bot, Player player)
        {
            if (!_bodyStates.TryGetValue(player, out BodyAnimatorState state))
                return;
            if (ShouldDisableAi(player))
            {
                PrepareBotForGlobalSuppression(bot, player,
                    !state.MovementStopped);
            }
            else if (state.MovementStopped)
            {
                bot.Mover?.MovementResume();
                state.MovementStopped = false;
            }
        }

        private void PrepareBotForGlobalSuppression(BotOwner bot,
            Player player, bool force)
        {
            if (bot == null || player == null || !_disableAi.Value ||
                !IsAlive(player))
                return;
            if (!_bodyStates.TryGetValue(player, out BodyAnimatorState state))
            {
                state = new BodyAnimatorState();
                _bodyStates.Add(player, state);
            }
            bool routeReturned = bot.Mover != null &&
                bot.Mover.HasPathAndNoComplete;
            if (!force && state.MovementStopped && !routeReturned)
                return;
            bot.ShootData?.EndShoot();
            bot.AimingManager?.CurrentAiming?.LoseTarget();
            if (bot.Mover != null)
            {
                bot.Mover.Stop();
                bot.Mover.SetTargetMoveSpeed(0f);
            }
            bot.Sprint(false, withDebugCallback: false);
            if (player.MovementContext != null)
                player.MovementContext.SetTilt(0f);
            state.MovementStopped = true;
        }

        private void AttachMainPlayer(Player player)
        {
            _mainPlayer = player;
            _localPlayerColliderIds.Clear();
            _transparentColliderIds.Clear();
            _opaqueColliderIds.Clear();
            CacheColliderIds(player, _localPlayerColliderIds);
        }

        private void RefreshCamera()
        {
            if (_camera != null && _camera.enabled &&
                _camera.gameObject.activeInHierarchy &&
                _camera.targetTexture == null)
                return;
            if (Time.unscaledTime < _nextCameraRefresh)
                return;
            _nextCameraRefresh = Time.unscaledTime + 1f;
            Camera best = null;
            float bestScore = float.MinValue;
            foreach (Camera candidate in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (candidate == null || !candidate.enabled ||
                    !candidate.gameObject.activeInHierarchy ||
                    candidate.orthographic || candidate.targetTexture != null)
                    continue;
                float score = candidate.depth;
                if (candidate.CompareTag("MainCamera")) score += 500f;
                if (candidate.name.IndexOf("FPS",
                        StringComparison.OrdinalIgnoreCase) >= 0) score += 1000f;
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            _camera = best != null ? best : Camera.main;
        }

        private void UpdateVisibility(Player player)
        {
            if (!_bodyStates.TryGetValue(player, out BodyAnimatorState state))
            {
                state = new BodyAnimatorState();
                _bodyStates.Add(player, state);
                CacheVisibilityData(player, state);
            }
            float now = Time.unscaledTime;
            bool eftVisible = player.PlayerBody != null && player.IsVisible;
            if (eftVisible)
                state.ActiveUntil = now + VisibilityHoldSeconds;
            if (_camera == null || now < state.NextVisibilityUpdate)
            {
                state.IsVisible = eftVisible || state.RayVisible ||
                    now < state.ActiveUntil;
                return;
            }
            float distance = (player.Position -
                _camera.transform.position).magnitude;
            float rate = distance <= 100f ? 10f : 4f;
            state.NextVisibilityUpdate = now + 1f / rate +
                (player.GetInstanceID() & 3) * 0.001f;
            if (!IsOnScreen(state.Head) && !IsOnScreen(state.Chest))
            {
                state.RayVisible = false;
                state.IsVisible = eftVisible || now < state.ActiveUntil;
                return;
            }
            state.RayVisible = IsPointVisible(player, state, state.Head) ||
                IsPointVisible(player, state, state.Chest);
            if (state.RayVisible)
                state.ActiveUntil = now + VisibilityHoldSeconds;
            state.IsVisible = eftVisible || state.RayVisible ||
                now < state.ActiveUntil;
        }

        private bool IsOnScreen(Transform point)
        {
            if (point == null || _camera == null)
                return false;
            Vector3 viewport = _camera.WorldToViewportPoint(point.position);
            return viewport.z > 0f && viewport.x >= 0f && viewport.x <= 1f &&
                   viewport.y >= 0f && viewport.y <= 1f;
        }

        private bool IsPointVisible(Player target, BodyAnimatorState state,
            Transform point)
        {
            if (point == null || _camera == null)
                return false;
            Vector3 origin = _camera.transform.position;
            Vector3 delta = point.position - origin;
            float distance = delta.magnitude;
            if (distance <= 0.05f)
                return true;
            int count = Physics.RaycastNonAlloc(origin, delta / distance,
                _visibilityHits, distance + 0.05f, VisibilityMask,
                QueryTriggerInteraction.Ignore);
            if (count == 0)
                return true;
            float closest = float.MaxValue;
            bool closestIsTarget = false;
            for (int i = 0; i < count; i++)
            {
                Collider collider = _visibilityHits[i].collider;
                if (collider == null)
                    continue;
                int id = collider.GetInstanceID();
                if (_localPlayerColliderIds.Contains(id) ||
                    IsPlayerCollider(collider, _mainPlayer) ||
                    IsTransparentCollider(collider, id))
                    continue;
                float hitDistance = _visibilityHits[i].distance;
                if (hitDistance >= closest)
                    continue;
                bool belongs = state.ColliderIds.Contains(id) ||
                    IsPlayerCollider(collider, target);
                if (!belongs && IsAnyPlayerCollider(collider))
                    continue;
                closest = hitDistance;
                closestIsTarget = belongs;
            }
            return closest == float.MaxValue || closestIsTarget ||
                   closest >= distance - 0.05f;
        }

        private void UpdateBodyAnimator(Player player, bool forceRefresh)
        {
            if (player == null || player.IsYourPlayer ||
                player.PlayerBones == null)
                return;
            if (!_bodyStates.TryGetValue(player, out BodyAnimatorState state))
            {
                state = new BodyAnimatorState();
                _bodyStates.Add(player, state);
                forceRefresh = true;
            }
            if (forceRefresh || state.Animator == null ||
                Time.unscaledTime >= state.NextRefresh)
            {
                Animator current = player.PlayerBones.PlayableAnimator != null
                    ? player.PlayerBones.PlayableAnimator.outputAnimator : null;
                if (current != null && current != state.Animator)
                {
                    Restore(state);
                    state.Animator = current;
                    state.OriginalEnabled = current.enabled;
                }
                CacheVisibilityData(player, state);
                state.NextRefresh = Time.unscaledTime + 1f;
            }
            if (state.Animator != null)
                state.Animator.enabled = state.OriginalEnabled &&
                    (!_disableBodyAnimator.Value || state.IsVisible);
        }

        private static void CacheVisibilityData(Player player,
            BodyAnimatorState state)
        {
            if (player == null || state == null || player.PlayerBones == null)
                return;
            state.Head = player.PlayerBones.Head != null
                ? player.PlayerBones.Head.Original : null;
            state.Chest = player.PlayerBones.Ribcage != null
                ? player.PlayerBones.Ribcage.Original : null;
            CacheColliderIds(player, state.ColliderIds);
        }

        private static void CacheColliderIds(Player player,
            HashSet<int> destination)
        {
            destination.Clear();
            if (player == null)
                return;
            foreach (Collider collider in
                     player.GetComponentsInChildren<Collider>(true))
                if (collider != null)
                    destination.Add(collider.GetInstanceID());
        }

        private static bool IsPlayerCollider(Collider collider, Player player)
        {
            if (collider == null || player == null)
                return false;
            BodyPartCollider body = collider.GetComponentInParent<BodyPartCollider>();
            if (body != null && body.Player != null &&
                body.Player.ProfileId == player.ProfileId)
                return true;
            Player colliderPlayer = collider.GetComponentInParent<Player>();
            return colliderPlayer == player;
        }

        private static bool IsAnyPlayerCollider(Collider collider)
        {
            if (collider == null)
                return false;
            BodyPartCollider body = collider.GetComponentInParent<BodyPartCollider>();
            return (body != null && body.Player != null) ||
                   collider.GetComponentInParent<Player>() != null;
        }

        private bool IsTransparentCollider(Collider collider, int id)
        {
            if (_transparentColliderIds.Contains(id)) return true;
            if (_opaqueColliderIds.Contains(id)) return false;
            bool transparent = IsRendererlessBoxCollider(collider);
            (transparent ? _transparentColliderIds : _opaqueColliderIds).Add(id);
            return transparent;
        }

        private static bool IsRendererlessBoxCollider(Collider collider)
        {
            if (!(collider is BoxCollider))
                return false;
            Bounds bounds = collider.bounds;
            Transform root = collider.transform;
            for (int depth = 0; root != null && depth < 3;
                 depth++, root = root.parent)
            {
                foreach (Renderer renderer in
                         root.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !renderer.enabled ||
                        !renderer.gameObject.activeInHierarchy ||
                        !renderer.bounds.Intersects(bounds))
                        continue;
                    foreach (Material material in renderer.sharedMaterials)
                        if (material != null)
                            return false;
                }
            }
            return true;
        }

        private void WakeAndStampDeathPose(Player player, bool advanceState)
        {
            if (player == null || player.IsYourPlayer ||
                player.PlayerBones == null)
                return;
            if (_bodyStates.TryGetValue(player, out BodyAnimatorState state))
            {
                state.IsVisible = true;
                state.RayVisible = true;
                state.ActiveUntil = float.PositiveInfinity;
                Restore(state);
            }
            PlayableAnimator playable = player.PlayerBones.PlayableAnimator;
            if (playable == null || playable.outputAnimator == null)
                return;
            playable.outputAnimator.enabled = true;
            if (!playable.Initialized || !playable.Graph.IsValid())
                return;
            if (advanceState)
                player.BodyUpdate(Mathf.Max(Time.deltaTime, 0.0001f));
            playable.Graph.Play();
            playable.Graph.Evaluate(0f);
        }

        private void ReleaseDeathAnimation(Player player)
        {
            if (player == null || player.PlayerBones == null)
                return;
            PlayableAnimator playable = player.PlayerBones.PlayableAnimator;
            if (playable != null)
            {
                if (playable.Initialized && playable.Graph.IsValid())
                    playable.Stop();
                if (playable.outputAnimator != null)
                    playable.outputAnimator.enabled = false;
            }
            if (_bodyStates.TryGetValue(player, out BodyAnimatorState state))
            {
                state.Animator = null;
                state.OriginalEnabled = false;
                state.MovementStopped = true;
            }
        }

        private void OnDisableAiSettingChanged(object sender, EventArgs args)
        {
            foreach (BotOwner bot in _bots)
            {
                if (bot == null) continue;
                Player player = bot.GetPlayer;
                if (ShouldDisableAi(player))
                {
                    if (_bodyStates.TryGetValue(player, out BodyAnimatorState state))
                        state.MovementStopped = false;
                    PrepareBotForGlobalSuppression(bot, player, true);
                }
                else
                {
                    bot.Mover?.MovementResume();
                    if (_bodyStates.TryGetValue(player, out BodyAnimatorState state))
                        state.MovementStopped = false;
                }
            }
        }

        private void OnBodySettingChanged(object sender, EventArgs args)
        {
            foreach (BotOwner bot in _bots)
                if (bot != null)
                    UpdateBodyAnimator(bot.GetPlayer, true);
        }

        private static bool IsAlive(Player player)
        {
            return player != null && player.HealthController != null &&
                   player.HealthController.IsAlive;
        }

        private static void Restore(BodyAnimatorState state)
        {
            if (state != null && state.Animator != null)
                state.Animator.enabled = state.OriginalEnabled;
        }

        private void OnDestroy()
        {
            if (_disableAi != null)
                _disableAi.SettingChanged -= OnDisableAiSettingChanged;
            if (_disableBodyAnimator != null)
                _disableBodyAnimator.SettingChanged -= OnBodySettingChanged;
            if (_onlySuppressCulledBots != null)
                _onlySuppressCulledBots.SettingChanged -= OnDisableAiSettingChanged;
            foreach (BodyAnimatorState state in _bodyStates.Values)
                Restore(state);
            _bodyStates.Clear();
            _harmony?.UnpatchSelf();
            _bots.Clear();
            _removedBots.Clear();
            _mainPlayer = null;
            _camera = null;
            _localPlayerColliderIds.Clear();
            _transparentColliderIds.Clear();
            _opaqueColliderIds.Clear();
            if (_instance == this)
                _instance = null;
        }
    }
}
