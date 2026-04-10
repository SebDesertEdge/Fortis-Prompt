using System;
using System.Threading.Tasks;
using Code;
using Fortis.Core.DependencyInjection;

namespace Fortis
{
    public class MainGameInstaller : MonoInstaller
    {
        [Serializable]
        public class Settings
        {
            public AnalyticsConfig AnalyticsConfig;
        }

        public Settings _settings;
        
        protected override void InstallBindings()
        {
            var config = Container.BindFromScriptableObject(_settings.AnalyticsConfig);
            if (config == null)
            {
                throw new Exception("AnalyticsConfig not found");
            }

            // Add here the claude implementation just to test the exception coming from UnityEngine.Random.value
            // not be called in the main thread.
            // Container.BindFromNewGameObject<Claude.Analytics.ResilientAnalytics>();
            
            Container.Bind<ResilientAnalytics>(addInterfaces: true);
        }

        protected override Task Run()
        {
            return Task.CompletedTask;
        }

        protected override Task Restart()
        {
            return Task.CompletedTask;
        }
    }
}