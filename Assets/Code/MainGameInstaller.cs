using System.Threading.Tasks;
using Fortis.Core.DependencyInjection;

namespace Fortis
{
    public class MainGameInstaller : MonoInstaller
    {
        public class Settings
        {
            public ResilientAnalyticsConfig ResilientAnalyticsConfig;
        }

        public Settings _settings;
        
        protected override void InstallBindings()
        {
            Container.BindFromScriptableObject(_settings.ResilientAnalyticsConfig);
            Container.Bind<AnalyticsService>();
        }

        protected override Task Run()
        {
            throw new System.NotImplementedException();
        }

        protected override Task Restart()
        {
            throw new System.NotImplementedException();
        }
    }
}