using PracticePlugin.Models;
using PracticePlugin.Views;
using Zenject;

namespace PracticePlugin.Installers
{
    internal class PlayerInstaller : MonoInstaller
    {
        public override void InstallBindings()
        {
            this.Container.BindInterfacesAndSelfTo<LevelFinishChecker>().AsCached().NonLazy();
            this._songTimeInfoEntity.PracticeMode = this._gameplayCoreSceneSetupData.practiceSettings != null;
            if (!this._songTimeInfoEntity.PracticeMode) {
                return;
            }
            this.Container.BindInterfacesAndSelfTo<PracticeUI>().FromNewComponentAsViewController().AsCached();
            this.Container.BindInterfacesAndSelfTo<LooperUI>().FromNewComponentOnNewGameObject().AsCached();
            this.Container.BindInterfacesAndSelfTo<SongSeeker>().FromNewComponentOnNewGameObject().AsCached();
            this.Container.BindInterfacesAndSelfTo<UIElementsCreator>().AsCached().NonLazy();
            this.Container.BindInterfacesAndSelfTo<AudioSpeedController>().FromNewComponentOnNewGameObject().AsCached().NonLazy();
            this.Container.BindInterfacesAndSelfTo<SongSeekBeatmapHandler>().AsCached();
        }

        [Inject]
        private GameplayCoreSceneSetupData _gameplayCoreSceneSetupData { get; set; }
        [Inject]
        private SongTimeInfoEntity _songTimeInfoEntity { get; set; }
    }
}
