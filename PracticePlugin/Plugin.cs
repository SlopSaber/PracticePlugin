using HarmonyLib;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using PracticePlugin.Installers;
using PracticePlugin.ScoreSaberPatch;
using IPA.Utilities.Async;
using SiraUtil.Zenject;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IPALogger = IPA.Logging.Logger;

namespace PracticePlugin
{
    [Plugin(RuntimeOptions.DynamicInit)]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; }
        private Harmony _harmony;
        private int _enableRevision;
        private bool _enabled;
        private Task<FixDictionary.DiscoveryResult> _compatibilityPreparation;
        internal static bool CompatibilityPending { get; private set; }
        internal static event Action<bool> CompatibilityFinished;
        [Init]
        /// <summary>
        /// Called when the plugin is first loaded by IPA (either when the game starts or when the plugin is enabled if it starts disabled).
        /// [Init] methods that use a Constructor or called before regular methods like InitWithConfig.
        /// Only use [Init] with one Constructor.
        /// </summary>
        public void Init(IPALogger logger, Config conf, Zenjector zenjector)
        {
            Log = logger;
            Log.Info("PracticePlugin initialized.");
            Configuration.PluginConfig.Instance = conf.Generated<Configuration.PluginConfig>();
            Log.Debug("Config loaded");
            try {
                _compatibilityPreparation = FixDictionary.PrepareDiscovery(false);
            }
            catch (Exception e) {
                Log.Error(e);
            }
            zenjector.Install<PlayerInstaller>(Location.StandardPlayer);
            zenjector.Install<PracticeMenuInstaller>(Location.Menu);
            zenjector.Install<PracticeAppInstaller>(Location.App);
        }

        [OnStart]
        public void OnApplicationStart()
        {
            Log.Debug("OnApplicationStart");
        }
        [OnExit]
        public void OnApplicationQuit()
        {
            _enabled = false;
            _enableRevision++;
            FinishCompatibility(false);
            Log.Debug("OnApplicationQuit");
        }
        [OnEnable]
        public Task PrepareCompatibilityAndEnable()
        {
            _enabled = true;
            int revision = ++_enableRevision;
            CompatibilityPending = true;
            try {
                _compatibilityPreparation = FixDictionary.PrepareDiscovery();
            }
            catch (Exception e) {
                Log.Error(e);
                FinishCompatibility(true);
                return Task.CompletedTask;
            }
            return _compatibilityPreparation.ContinueWith(completed => {
                if (!_enabled || revision != _enableRevision) {
                    if (completed.IsFaulted) _ = completed.Exception;
                    return;
                }
                try {
                    FixDictionary.PublishDiscovery(completed.GetAwaiter().GetResult());
                    ApplyPatches();
                }
                catch (Exception e) {
                    Log.Error(e);
                }
                finally {
                    FinishCompatibility(true);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                UnityMainThreadTaskScheduler.Default);
        }

        public void OnEnable()
        {
            _enabled = true;
            _enableRevision++;
            CompatibilityPending = true;
            try {
                ApplyPatches();
            }
            finally {
                FinishCompatibility(true);
            }
        }

        private void ApplyPatches()
        {
            try {
                this._harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e) {
                Log.Error(e);
            }
        }

        [OnDisable]
        public void OnDisable()
        {
            _enabled = false;
            _enableRevision++;
            FinishCompatibility(false);
            try {
                this._harmony?.UnpatchSelf();
                this._harmony = null;
            }
            catch (Exception e) {
                Log.Error(e);
            }
        }

        private static void FinishCompatibility(bool enabled)
        {
            CompatibilityPending = false;
            Action<bool> handlers = CompatibilityFinished;
            if (handlers == null) return;
            foreach (Action<bool> handler in handlers.GetInvocationList()) {
                try { handler(enabled); }
                catch (Exception e) { Log.Error(e); }
            }
        }
    }
}
