using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Fortis.Core.DependencyInjection
{
    public abstract class MonoInstaller: MonoBehaviour
    {
        public new bool DontDestroyOnLoad = true;
        public bool LogDependencyErrors = false;

        protected DiContainer Container;
        
        protected virtual void Awake()
        {
            if (DontDestroyOnLoad)
            {
                DontDestroyOnLoad(this);
            }
            Container = gameObject.AddComponent<DiContainer>();
        }

        protected void OnDestroy()
        {
        }
        
        protected void Start()
        {
            #pragma warning disable CS4014
            StartAsync();
            #pragma warning restore CS4014
        }

        protected virtual async Task StartAsync()
        {
            try
            {
                InstallBindings();
                
                Container.ResolveDependencies(LogDependencyErrors);
                Container.Initialize();
                
                await Run();
            }
            catch (OperationCanceledException e)
            {
                Debug.LogException(e);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        protected abstract void InstallBindings();
        protected abstract Task Run();
        protected abstract Task Restart();

        private void OnRestartApp()
        {
            #pragma warning disable CS4014
            OnRestartAppAsync();
            #pragma warning restore CS4014
        }

        private async Task OnRestartAppAsync()
        {
            try
            {
                Container.Cleanup();
                await Restart();
            }
            catch (OperationCanceledException e)
            {
                Debug.LogException(e);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        public async Task FullRestart()
        {
            try
            {
                Container.TearDown();

                InstallBindings();
                Container.ResolveDependencies(LogDependencyErrors);
                Container.Initialize();

                await Run();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}