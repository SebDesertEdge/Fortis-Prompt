using System;
using System.Threading.Tasks;
using Fortis.Analytics;
using Fortis.Analytics.Strategies;
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

            switch (config.Implementation)
            {
                case AnalyticsImplementation.WorkerThread:
                    Container.BindFromNewGameObject<ResilientAnalytics>(addInterfaces: true);
                    break;
                case AnalyticsImplementation.Throttled:
                    Container.Bind<ThrottledAnalyticsService>(addInterfaces: true);
                    break;
                case AnalyticsImplementation.HybridDispatch:
                    Container.Bind<HybridDispatchAnalyticsService>(addInterfaces: true);
                    break;
                case AnalyticsImplementation.Awaitable:
                    Container.BindFromNewGameObject<AwaitableAnalyticsService>(addInterfaces: true);
                    break;
                default:
                    Container.Bind<AnalyticsService>(addInterfaces: true);
                    break;
            }
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