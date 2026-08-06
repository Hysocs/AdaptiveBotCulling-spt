using System;
using System.Collections;
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
        public const string Version = "1.1.0";

        private const float VisibilityHoldSeconds = 1.5f;

        private sealed class BodyAnimatorState
        {
            public BotOwner Owner;
            public Animator Animator;
            public bool OriginalEnabled;
            public float NextRefresh;
            public bool HasVisibility;
            public bool EftVisible;
            public bool IsVisible;
            public int VisibilityGeneration;
            public bool MovementStopped;
            public readonly HashSet<AudioSource> PausedAudioSources =
                new HashSet<AudioSource>();
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
        private readonly Dictionary<LookSensor, Player> _lookSensorPlayers =
            new Dictionary<LookSensor, Player>();
        private ConfigEntry<bool> _disableAi;
        private ConfigEntry<bool> _onlySuppressCulledBots;
        private ConfigEntry<bool> _disableBodyAnimator;
        private ConfigEntry<bool> _pauseAudioSources;
        private ConfigEntry<bool> _disablePeriodicLookSensing;
        private Harmony _harmony;
        private float _nextMaintenance;

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
            _pauseAudioSources = Config.Bind("Experimental native culling",
                "Pause AudioSources", true,
                "Pauses playing looping Unity AudioSource components while a bot is culled and resumes only sources paused by this mod.");
            _disablePeriodicLookSensing = Config.Bind("Distance culling",
                "Disable periodic look sensing while culled", true,
                "Skips the global AI task scheduler's enemy visibility, weather, light, reporting, and goal checks for culled bots.");
            _disableAi.SettingChanged += OnDisableAiSettingChanged;
            _onlySuppressCulledBots.SettingChanged += OnDisableAiSettingChanged;
            _disableBodyAnimator.SettingChanged += OnBodySettingChanged;
            _pauseAudioSources.SettingChanged += OnBodySettingChanged;

            _harmony = new Harmony(Guid);
            Patch(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.PreActivate)), nameof(RegisterBot), false);
            Patch(AccessTools.Method(typeof(BotOwner), "method_10", Type.EmptyTypes), nameof(BotActivated), false);
            Patch(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual)), nameof(AllowBotManualUpdate), true);
            Patch(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.FixedUpdate)), nameof(AllowBotFixedUpdate), true);
            Patch(AccessTools.Method(typeof(AICoreAgent<BotLogicDecision>),
                nameof(AICoreAgent<BotLogicDecision>.Update)), nameof(AllowBrainUpdate), true);
            Patch(AccessTools.PropertyGetter(typeof(Player),
                nameof(Player.IsVisible)), nameof(OnEftVisibilityEvaluated), false);
            Patch(GetLookSensorPeriodicUpdate(),
                nameof(AllowPeriodicLookSensing), true);
            Patch(AccessTools.Method(typeof(Player), nameof(Player.OnDead),
                new[] { typeof(EDamageType) }), nameof(PrepareDeathPose), true);
            Patch(AccessTools.Method(typeof(Player), nameof(Player.CreateCorpse),
                Type.EmptyTypes), nameof(PrepareCorpsePose), true);
            Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.DoWorldTick)), nameof(OnWorldTick), false);

            Logger.LogInfo(Name + " " + Version +
                " loaded: event-driven EFT visibility, AI suppression, and hidden Animator culling.");
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

        private static MethodInfo GetLookSensorPeriodicUpdate()
        {
            foreach (Type contract in typeof(LookSensor).GetInterfaces())
            {
                MethodInfo[] methods = contract.GetMethods();
                if (methods.Length != 1 || methods[0].ReturnType != typeof(void))
                    continue;
                ParameterInfo[] parameters = methods[0].GetParameters();
                if (parameters.Length != 1 ||
                    parameters[0].ParameterType != typeof(float))
                    continue;
                InterfaceMapping mapping = typeof(LookSensor)
                    .GetInterfaceMap(contract);
                return mapping.TargetMethods[0];
            }
            return null;
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
            _instance.EnsureState(player, __instance);
            if (__instance.LookSensor != null && player != null)
                _instance._lookSensorPlayers[__instance.LookSensor] = player;
            _instance.UpdateBodyAnimator(player, true);
            if (player != null)
                _instance.ReceiveEftVisibility(player, player.IsVisible);
            _instance.UpdateBotSuppression(__instance, player);
        }

        private static void OnEftVisibilityEvaluated(Player __instance,
            bool __result)
        {
            _instance?.ReceiveEftVisibility(__instance, __result);
        }

        private static bool AllowPeriodicLookSensing(LookSensor __instance)
        {
            if (_instance == null || __instance == null ||
                !_instance._disablePeriodicLookSensing.Value)
                return true;
            return !_instance._lookSensorPlayers.TryGetValue(__instance,
                       out Player player) || !_instance.IsCulled(player);
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
            _instance.MaintenanceTick();
        }

        private void MaintenanceTick()
        {
            if (Time.unscaledTime < _nextMaintenance)
                return;
            _nextMaintenance = Time.unscaledTime + 1f;
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
            if (bot == null || player == null ||
                !_disableAi.Value ||
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

        private BodyAnimatorState EnsureState(Player player,
            BotOwner owner = null)
        {
            if (player == null)
                return null;
            if (!_bodyStates.TryGetValue(player, out BodyAnimatorState state))
            {
                state = new BodyAnimatorState { IsVisible = true };
                _bodyStates.Add(player, state);
            }
            if (owner != null)
                state.Owner = owner;
            return state;
        }

        private void ReceiveEftVisibility(Player player, bool visible)
        {
            if (player == null || player.IsYourPlayer)
                return;
            BodyAnimatorState state = EnsureState(player);
            if (state.HasVisibility && state.EftVisible == visible)
                return;
            state.HasVisibility = true;
            state.EftVisible = visible;
            int generation = ++state.VisibilityGeneration;
            if (visible || !IsAlive(player))
            {
                ApplyVisibility(player, state, true);
                return;
            }
            StartCoroutine(CullAfterHold(player, state, generation));
        }

        private IEnumerator CullAfterHold(Player player,
            BodyAnimatorState state, int generation)
        {
            yield return new WaitForSecondsRealtime(VisibilityHoldSeconds);
            if (player != null && state != null &&
                state.VisibilityGeneration == generation &&
                !state.EftVisible && IsAlive(player))
                ApplyVisibility(player, state, false);
        }

        private void ApplyVisibility(Player player, BodyAnimatorState state,
            bool visible)
        {
            if (state.IsVisible == visible)
                return;
            state.IsVisible = visible;
            UpdateBotSuppression(state.Owner, player);
            UpdateBodyAnimator(player, false);
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
                state.NextRefresh = Time.unscaledTime + 1f;
            }
            if (state.Animator != null)
                state.Animator.enabled = state.OriginalEnabled &&
                    (!_disableBodyAnimator.Value || state.IsVisible);
            UpdateAudioSources(player, state);
        }

        private void UpdateAudioSources(Player player,
            BodyAnimatorState state)
        {
            bool shouldPause = _pauseAudioSources.Value && !state.IsVisible;
            if (shouldPause)
            {
                foreach (AudioSource source in
                         player.GetComponentsInChildren<AudioSource>(true))
                {
                    if (source == null || !source.loop || !source.isPlaying ||
                        state.PausedAudioSources.Contains(source))
                        continue;
                    source.Pause();
                    state.PausedAudioSources.Add(source);
                }
                return;
            }
            ResumeAudioSources(state);
        }

        private static void ResumeAudioSources(BodyAnimatorState state)
        {
            foreach (AudioSource source in state.PausedAudioSources)
                if (source != null)
                    source.UnPause();
            state.PausedAudioSources.Clear();
        }

        private bool IsCulled(Player player)
        {
            return IsAlive(player) &&
                   _bodyStates.TryGetValue(player,
                       out BodyAnimatorState state) &&
                   !state.IsVisible;
        }

        private void WakeAndStampDeathPose(Player player, bool advanceState)
        {
            if (player == null || player.IsYourPlayer ||
                player.PlayerBones == null)
                return;
            if (_bodyStates.TryGetValue(player, out BodyAnimatorState state))
            {
                state.VisibilityGeneration++;
                state.HasVisibility = true;
                state.EftVisible = true;
                state.IsVisible = true;
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
            if (state == null)
                return;
            if (state.Animator != null)
                state.Animator.enabled = state.OriginalEnabled;
            ResumeAudioSources(state);
        }

        private void OnDestroy()
        {
            if (_disableAi != null)
                _disableAi.SettingChanged -= OnDisableAiSettingChanged;
            if (_disableBodyAnimator != null)
                _disableBodyAnimator.SettingChanged -= OnBodySettingChanged;
            if (_pauseAudioSources != null)
                _pauseAudioSources.SettingChanged -= OnBodySettingChanged;
            if (_onlySuppressCulledBots != null)
                _onlySuppressCulledBots.SettingChanged -= OnDisableAiSettingChanged;
            StopAllCoroutines();
            foreach (BodyAnimatorState state in _bodyStates.Values)
                Restore(state);
            _bodyStates.Clear();
            _harmony?.UnpatchSelf();
            _bots.Clear();
            _removedBots.Clear();
            _lookSensorPlayers.Clear();
            if (_instance == this)
                _instance = null;
        }
    }
}

