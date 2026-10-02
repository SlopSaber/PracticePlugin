using HarmonyLib;
using IPA.Loader;
using IPA.Utilities;
using IPA.Utilities.Async;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
            _metadataPreparation = SeekMetadataPreparation.Prepare(
                _noodleObjectsCallbacksManager == null ? null : callBackManager, s_customNotesControllerInfo);
            _metadataPreparation.ContinueWith(completed => {
                if (_disposed) {
                    if (completed.IsFaulted) _ = completed.Exception;
                    return;
                }
                try {
                    _seekMetadata = completed.GetAwaiter().GetResult();
                    ResumePendingSeek();
                }
                catch (Exception e) {
                    ClearPendingSeek();
                    Logger.Error(e);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                UnityMainThreadTaskScheduler.Default);
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
        private static readonly float s_minAheadTime = 1f;
        private SliderInteractionManager[] _sliderInteractionManager = null;
        private readonly IGameEnergyCounter _gameEnergyCounter;
        private bool _noFailOn0Energy;
        private bool _failed;
        private float? _pendingSongTime;
        private bool _waitingForCompatibility;
        private bool _disposed;
        private readonly Task<SeekMetadataPreparation.Result> _metadataPreparation;
        private SeekMetadataPreparation.Result _seekMetadata;

        static SongSeekBeatmapHandler()
        {
            var info = PluginManager.GetPlugin("CustomNotes");
            if (info != null) {
                s_customNotesControllerInfo = Type.GetType("CustomNotes.Components.CustomNoteController, CustomNotes");
                if (s_customNotesControllerInfo == null) throw new NullReferenceException();
            }
        }

        public void OnSongTimeChanged(float newSongTime)
        {
            if (this._failed || this._disposed) {
                return;
            }
            if (Plugin.CompatibilityPending || !_metadataPreparation.IsCompleted) {
                _pendingSongTime = newSongTime;
                if (!_waitingForCompatibility) {
                    _waitingForCompatibility = true;
                    Plugin.CompatibilityFinished += OnCompatibilityFinished;
                }
                return;
            }
            if (_seekMetadata == null) _seekMetadata = _metadataPreparation.GetAwaiter().GetResult();

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
                _seekMetadata.NoodleStartFilterTime.SetValue(this._noodleObjectsCallbacksManager, newSongTime + aheadTime);
                _seekMetadata.NoodlePreviousSongTime.SetValue(this._noodleObjectsCallbacksManager, newSongTime);
                if (_seekMetadata.NoodleCallbacksInTime.GetValue(this._noodleObjectsCallbacksManager) is CallbacksInTime callbacks) {
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
            if (enabled) {
                ResumePendingSeek();
                return;
            }
            ClearPendingSeek();
        }

        private void ResumePendingSeek()
        {
            if (Plugin.CompatibilityPending || !_metadataPreparation.IsCompleted) return;
            float? songTime = _pendingSongTime;
            ClearPendingSeek();
            if (!_disposed && !_failed && songTime.HasValue && _audioTimeSyncController)
                OnSongTimeChanged(songTime.Value);
        }

        private void ClearPendingSeek()
        {
            _pendingSongTime = null;
            _waitingForCompatibility = false;
            Plugin.CompatibilityFinished -= OnCompatibilityFinished;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearPendingSeek();
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
                _seekMetadata.CustomNoteWasMissed.Invoke(customNote, new object[] { nc });
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

    internal static class SeekMetadataPreparation
    {
        internal sealed class Result
        {
            internal readonly FieldInfo NoodleStartFilterTime;
            internal readonly FieldInfo NoodlePreviousSongTime;
            internal readonly FieldInfo NoodleCallbacksInTime;
            internal readonly MethodInfo CustomNoteWasMissed;

            internal Result(FieldInfo startFilterTime, FieldInfo previousSongTime,
                FieldInfo callbacksInTime, MethodInfo customNoteWasMissed)
            {
                NoodleStartFilterTime = startFilterTime;
                NoodlePreviousSongTime = previousSongTime;
                NoodleCallbacksInTime = callbacksInTime;
                CustomNoteWasMissed = customNoteWasMissed;
            }
        }

        private sealed class Request
        {
            internal readonly Type NoodleType;
            internal readonly Type CustomType;

            internal Request(Type noodleType, Type customType)
            {
                NoodleType = noodleType;
                CustomType = customType;
            }
        }

        private static Task<Result> _preparation;
        private static Request _request;

        internal static void Prewarm()
        {
            Type noodleType = null;
            Type customType = null;
            bool customEnabled = PluginManager.GetPlugin("CustomNotes") != null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                string name = assembly.GetName().Name;
                if (name == "NoodleExtensions")
                    noodleType = assembly.GetType("NoodleExtensions.Managers.NoodleObjectsCallbacksManager");
                else if (customEnabled && name == "CustomNotes")
                    customType = assembly.GetType("CustomNotes.Components.CustomNoteController");
            }
            Prepare(noodleType, customType).ContinueWith(completed => {
                _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        internal static Task<Result> Prepare(Type noodleType, Type customType)
        {
            if (_preparation != null && _request.NoodleType == noodleType &&
                _request.CustomType == customType && !_preparation.IsFaulted && !_preparation.IsCanceled)
                return _preparation;
            var request = new Request(noodleType, customType);
            Task<Result> previous = _preparation;
            _request = request;
            _preparation = previous == null
                ? Task.Factory.StartNew(PrepareResult, request, CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
                : previous.ContinueWith(completed => {
                    if (completed.IsFaulted) _ = completed.Exception;
                    return PrepareResult(request);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _preparation;
        }

        private static Result PrepareResult(object state)
        {
            var request = (Request)state;
            MethodInfo customMethod = request.CustomType?.GetMethod(
                "HandleNoteControllerNoteWasMissed", BindingFlags.Instance | BindingFlags.Public);
            FieldInfo startFilterTime = request.NoodleType == null ? null
                : AccessTools.Field(request.NoodleType, "_startFilterTime");
            FieldInfo previousSongTime = request.NoodleType == null ? null
                : AccessTools.Field(request.NoodleType, "_prevSongtime");
            FieldInfo callbacksInTime = request.NoodleType == null ? null
                : AccessTools.Field(request.NoodleType, "_callbacksInTime");
            return new Result(startFilterTime, previousSongTime, callbacksInTime, customMethod);
        }
    }
}
