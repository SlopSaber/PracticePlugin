using HarmonyLib;
using IPA.Loader;
using IPA.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Zenject;

namespace PracticePlugin.Models
{
    public class SongSeekBeatmapHandler : IDisposable
    {

        [Inject]
        public SongSeekBeatmapHandler(
            AudioTimeSyncController audioTimeSyncController,
            BeatmapCallbacksController beatmapCallbacksController,
            NoteCutSoundEffectManager noteCutSoundEffectManager,
            BasicBeatmapObjectManager beatmapObjectManager,
            IReadonlyBeatmapData beatmapData,
            GameplayModifiers gameplayModifiers,
            IGameEnergyCounter gameEnergyCounter,
            DiContainer di)
        {
            this._audioTimeSyncController = audioTimeSyncController;
            this._beatmapCallbacksController = beatmapCallbacksController;
            this._noteCutSoundEffectManager = noteCutSoundEffectManager;
            this._beatmapObjectManager = beatmapObjectManager;
            this._gameEnergyCounter = gameEnergyCounter;
            this._gameEnergyCounter.gameEnergyDidReach0Event += this.OnGameEnergyCounter_gameEnergyDidReach0Event;
            this._noFailOn0Energy = gameplayModifiers.noFailOn0Energy;
            this._failed = false;
            var callBackManager = Type.GetType("NoodleExtensions.Managers.NoodleObjectsCallbacksManager, NoodleExtensions");
            if (callBackManager != null) {
                this._noodleObjectsCallbacksManager = di.TryResolve(callBackManager);
            }
        }

        private void OnGameEnergyCounter_gameEnergyDidReach0Event()
        {
            if (!this._noFailOn0Energy) {
                _failed = true;
            }
            this._gameEnergyCounter.gameEnergyDidReach0Event -= OnGameEnergyCounter_gameEnergyDidReach0Event;
        }

        private readonly BeatmapCallbacksController _beatmapCallbacksController;
        private readonly NoteCutSoundEffectManager _noteCutSoundEffectManager;
        private readonly AudioTimeSyncController _audioTimeSyncController;
        private readonly BasicBeatmapObjectManager _beatmapObjectManager;
        private readonly object _noodleObjectsCallbacksManager;
        private static readonly Type s_customNotesControllerInfo = null;
        private static readonly MethodInfo s_handleNoteControllerNoteWasMissed = null;
        private static readonly float s_minAheadTime = 1f;
        private SliderInteractionManager[] _sliderInteractionManager = null;
        private readonly IGameEnergyCounter _gameEnergyCounter;
        private bool _noFailOn0Energy;
        private bool _failed;
        private float? _pendingSongTime;
        private bool _waitingForCompatibility;
        private bool _disposed;

        static SongSeekBeatmapHandler()
        {
            var info = PluginManager.GetPlugin("CustomNotes");
            if (info != null) {
                s_customNotesControllerInfo = Type.GetType("CustomNotes.Components.CustomNoteController, CustomNotes");
                s_handleNoteControllerNoteWasMissed = s_customNotesControllerInfo.GetMethod("HandleNoteControllerNoteWasMissed", BindingFlags.Instance | BindingFlags.Public);
            }
        }

        public void OnSongTimeChanged(float newSongTime)
        {
            if (this._failed || this._disposed) {
                return;
            }
            if (Plugin.CompatibilityPending) {
                _pendingSongTime = newSongTime;
                if (!_waitingForCompatibility) {
                    _waitingForCompatibility = true;
                    Plugin.CompatibilityFinished += OnCompatibilityFinished;
                }
                return;
            }

            var samplePos = newSongTime / this._audioTimeSyncController.songEndTime;
            var audioSource = this._audioTimeSyncController._audioSource;
            audioSource.timeSamples = Mathf.RoundToInt(Mathf.Lerp(0, audioSource.clip.samples, samplePos));
            var aheadTime = Mathf.Min(newSongTime, s_minAheadTime);
            audioSource.time -= aheadTime;
            this._audioTimeSyncController._prevAudioSamplePos = -1;
            this._audioTimeSyncController._songTime = newSongTime;
            this._noteCutSoundEffectManager._prevNoteATime = -1f;
            this._noteCutSoundEffectManager._prevNoteBTime = -1f;
            this._beatmapCallbacksController.SetField("_startFilterTime", newSongTime + aheadTime);
            this._beatmapCallbacksController._prevSongTime = newSongTime;
            var dic = this._beatmapCallbacksController._callbacksInTimes;
            foreach (var item in dic.Values) {
                item.lastProcessedNode = null;
            }
            if (this._noodleObjectsCallbacksManager != null) {
                var noodleObjectsCallbacksManagerStartFilerSongTime = AccessTools.Field(Type.GetType("NoodleExtensions.Managers.NoodleObjectsCallbacksManager, NoodleExtensions"), "_startFilterTime");
                noodleObjectsCallbacksManagerStartFilerSongTime.SetValue(this._noodleObjectsCallbacksManager, newSongTime + aheadTime);
                var noodleObjectsCallbacksManagerPrevSongTime = AccessTools.Field(Type.GetType("NoodleExtensions.Managers.NoodleObjectsCallbacksManager, NoodleExtensions"), "_prevSongtime");
                noodleObjectsCallbacksManagerPrevSongTime.SetValue(this._noodleObjectsCallbacksManager, newSongTime);
                var noodleObjectsCallbacksManagerCallbacksIntime = AccessTools.Field(Type.GetType("NoodleExtensions.Managers.NoodleObjectsCallbacksManager, NoodleExtensions"), "_callbacksInTime");
                if (noodleObjectsCallbacksManagerCallbacksIntime.GetValue(this._noodleObjectsCallbacksManager) is CallbacksInTime callbacks) {
                    callbacks.lastProcessedNode = null;
                }
            }
            // Thank you Kyle 1413!
            var basicGameNotePoolContainer = this._beatmapObjectManager._basicGameNotePoolContainer;
            var burstSliderHeadGameNotePoolContainer = this._beatmapObjectManager._burstSliderHeadGameNotePoolContainer;
            var burstSliderGameNotePoolContainer = this._beatmapObjectManager._burstSliderGameNotePoolContainer;
            var obstaclePoolContainer = this._beatmapObjectManager._obstaclePoolContainer;
            var cutSoundPoolContainer = this._noteCutSoundEffectManager._noteCutSoundEffectPoolContainer;
            this.DespawnNotes(basicGameNotePoolContainer);
            this.DespawnNotes(burstSliderHeadGameNotePoolContainer);
            this.DespawnNotes(burstSliderGameNotePoolContainer);
            foreach (var item in obstaclePoolContainer.activeItems.ToArray()) {
                if (item == null) {
                    continue;
                }
                item._finishMovementTime = -1f;
                item.ManualUpdate();
            }
            foreach (var item in cutSoundPoolContainer.activeItems.ToArray()) {
                item?.StopPlayingAndFinish();
            }
            if (this._sliderInteractionManager == null) {
                this._sliderInteractionManager = Resources.FindObjectsOfTypeAll<SliderInteractionManager>();
            }
            foreach (var mang in this._sliderInteractionManager) {
                if (mang == null) continue;
                var activeSlider = mang._activeSliders;
                foreach (var slider in activeSlider.ToArray()) {
                    mang.RemoveActiveSlider(slider);
                }
                foreach (var item in mang?.GetComponentsInChildren<SliderHapticFeedbackInteractionEffect>()) {
                    item.enabled = false;
                }
            }
        }

        private void OnCompatibilityFinished(bool enabled)
        {
            float? songTime = _pendingSongTime;
            _pendingSongTime = null;
            _waitingForCompatibility = false;
            Plugin.CompatibilityFinished -= OnCompatibilityFinished;
            if (enabled && !_disposed && !_failed && songTime.HasValue && _audioTimeSyncController)
                OnSongTimeChanged(songTime.Value);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pendingSongTime = null;
            _waitingForCompatibility = false;
            Plugin.CompatibilityFinished -= OnCompatibilityFinished;
            _gameEnergyCounter.gameEnergyDidReach0Event -= OnGameEnergyCounter_gameEnergyDidReach0Event;
        }

        /// <summary>
        /// Deapawn notes
        /// </summary>
        /// <typeparam name="T"><see cref="NoteController"/></typeparam>
        /// <param name="memoryPoolContainer"></param>
        private void DespawnNotes<T>(MemoryPoolContainer<T> memoryPoolContainer) where T : NoteController
        {
            if (memoryPoolContainer == null) {
                return;
            }
            // Callbacks dirty the pool's cached list; snapshot it once before despawning.
            foreach (var item in memoryPoolContainer.activeItems.ToArray()) {
                if (item == null) continue;
                this.RaiseCustomNoteMissEvent(item);
                var movement = item?._noteMovement;
#if false
                if (movement?.movementPhase == NoteMovement.MovementPhase.MovingOnTheFloor) {
                    movement?.HandleFloorMovementDidFinish();
                }
#endif
                movement?.HandleNoteJumpDidFinish();
            }
        }

        private void RaiseCustomNoteMissEvent(NoteController nc)
        {
            if (s_customNotesControllerInfo == null) {
                return;
            }
            var customNote = nc.gameObject.GetComponentInChildren(s_customNotesControllerInfo);
            if (customNote != null) {
                s_handleNoteControllerNoteWasMissed.Invoke(customNote, new object[] { nc });
            }
        }

        public void ChangeSongStartTime(float newSongTime)
        {
            this._audioTimeSyncController._prevAudioSamplePos = -1;
            this._audioTimeSyncController._startSongTime = newSongTime;
            var initData = this._audioTimeSyncController._initData;
            initData.SetField("startSongTime", newSongTime);
            this._audioTimeSyncController.SetField("_initData", initData);
            this._noteCutSoundEffectManager._prevNoteATime = -1f;
            this._noteCutSoundEffectManager._prevNoteBTime = -1f;
            this._beatmapCallbacksController.SetField("_startFilterTime", newSongTime + 1f);
            this._beatmapCallbacksController._prevSongTime = newSongTime;
        }
    }
}
