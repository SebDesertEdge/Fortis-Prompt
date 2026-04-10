using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Fortis.Core.DependencyInjection
{
    public class DiContainer : MonoBehaviour
    {
        private static DiContainer _instance;
        private Dictionary<Type, object> _dependencies;
        private bool _initialized;

        private List<IInitializable> _initializables;
        private List<ITickable> _tickables;
        private List<IFixedTickable> _fixedTickables;
        private List<ILateTickable> _lateTickables;
        private List<IDisposable> _disposables;
        private List<IPausable> _pausables;
        private List<IFocusable> _focusables;
        private List<ICleanable> _cleanables;
        private FieldInfo[] _objectFields;
        private object _injection;
        private BindingFlags _bindingAttr = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        private bool _isPaused;

        public bool IsPaused
        {
            set => _isPaused = value;
        }
        
        private void Awake()
        {
            _instance = this;
            _dependencies = new Dictionary<Type, object>();
            _initializables = new List<IInitializable>();
            _tickables = new List<ITickable>();
            _fixedTickables = new List<IFixedTickable>();
            _lateTickables = new List<ILateTickable>();
            _disposables = new List<IDisposable>();
            _pausables = new List<IPausable>();
            _focusables = new List<IFocusable>();
            _cleanables = new List<ICleanable>();
            _initialized = false;
        }

        protected void OnDestroy()
        {
            Dispose();
        }

        protected virtual void OnApplicationQuit()
        {
            Dispose();
        }

        private void Dispose()
        {
            TearDown();
            _instance = null;
        }

        public void TearDown()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }

            // Destroy any MonoBehaviour-based bindings that were added as components
            foreach (var dep in _dependencies.Values)
            {
                if (dep is MonoBehaviour mb && mb != null && mb != this)
                {
                    Destroy(mb);
                }
            }

            _dependencies.Clear();
            _initializables.Clear();
            _tickables.Clear();
            _fixedTickables.Clear();
            _lateTickables.Clear();
            _disposables.Clear();
            _pausables.Clear();
            _focusables.Clear();
            _cleanables.Clear();
            _initialized = false;
        }
        
        private void Update()
        {
            if (!_initialized)
            {
                return;
            }
            if (_isPaused)
            {
                return;
            }

            foreach (var tickable in _tickables)
            {
                tickable.Tick();
            }
        }
        
        private void LateUpdate()
        {
            if (!_initialized)
            {
                return;
            }
            if (_isPaused)
            {
                return;
            }
            
            foreach (var lateTickable in _lateTickables)
            {
                lateTickable.LateTick();
            }
        }

        private void FixedUpdate()
        {
            if (!_initialized)
            {
                return;
            }
            if (_isPaused)
            {
                return;
            }
            
            foreach (var fixedTickable in _fixedTickables)
            {
                fixedTickable.FixedTick();
            }
        }

        private void OnApplicationPause(bool pause)
        {
            if (!_initialized)
            {
                return;
            }
            
            // This might not ever trigger if RunInBackground is set.
            for (var i = _pausables.Count - 1; i >= 0; --i)
            {
                _pausables[i].Pause(pause);
            }
        }

        private void OnApplicationFocus(bool focus)
        {
            if (!_initialized)
            {
                return;
            }
            
            for (var i = _focusables.Count - 1; i >= 0; --i)
            {
                _focusables[i].Focus(focus);
            }
        }

        public void Initialize()
        {
            foreach (var initializable in _initializables)
            {
                initializable.Initialize();
            }
            _initialized = true;
        }

        public void Cleanup()
        {
            for (var i = _cleanables.Count - 1; i >= 0; --i)
            {
                _cleanables[i].Cleanup();
            }
        }

        public void ResolveDependencies(bool throwException = false)
        {
            foreach (var dependency in _dependencies.Values)
            {
                ResolveDependenciesInternal(dependency, throwException);
            }
        }

        void AddInterfaces<T>(T instance)
        {
            var interfaces = typeof(T).GetInterfaces();

            var diInterfaces = new List<Type>
            {
                typeof(IDisposable), 
                typeof(IFixedTickable), 
                typeof(IInitializable),
                typeof(ILateTickable), 
                typeof(IPausable),
                typeof(ITickable),
                typeof(IFocusable),
                typeof(ICleanable)
            };

            interfaces = interfaces.Where(i => !diInterfaces.Contains(i)).ToArray();
            
            if (interfaces.Length == 0)
            {
                Debug.LogWarning($"[DiContainer] Called BindInterfaceAndSelf for type {typeof(T)} but no interfaces were found");
                return;
            }

            foreach (var typeInterface in interfaces)
            {
                AddToDependenciesDict(typeInterface, instance);
            }
        }

        void AddSubClasses<T>(T instance)
        {
            var type = typeof(T);
            var subTypes = GetSubtypes(type);

            foreach (var subType in subTypes)
            {
                AddToDependenciesDict(subType, instance);
            }
        }
        
        List<Type> GetSubtypes(Type baseType)
        {
            var subtypes = new List<Type>();

            // Get all loaded assemblies in the current application domain
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                try
                {
                    subtypes.AddRange(assembly.GetTypes()
                        .Where(myType => baseType.IsSubclassOf(myType) && myType.IsClass)
                        .ToList());
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Handle cases where some types in an assembly cannot be loaded
                    foreach (Exception loaderException in ex.LoaderExceptions)
                    {
                        Console.WriteLine($"Loader Exception: {loaderException.Message}");
                    }
                }
            }
            return subtypes;
        }

        public T Bind<T>(bool addInterfaces = false, bool addSubClasses = false) where T : class, new()
        {
            var instance = new T();
            BindInternal<T>(instance, addInterfaces, addSubClasses);
            return instance;
        }
        
        public T BindFromPrefab<T>(T prefab, Transform parentTransform = null, bool worldPositionStays = false, bool addInterfaces = false, bool addSubClasses = false) where T : Component
        {
            if (prefab == null)
            {
                return null;
            }

            if (parentTransform == null)
            {
                parentTransform = transform;
            }
            
            var prefabInstance = Instantiate(prefab, parentTransform, worldPositionStays);
            BindInternal<T>(prefabInstance, addInterfaces, addSubClasses);
            return prefabInstance.GetComponent<T>();
        }

        public T BindFromNewGameObject<T>(bool addInterfaces = false, bool addSubClasses = false) where T : Component
        {
            var instance = gameObject.AddComponent<T>();
            BindInternal<T>(instance, addInterfaces, addSubClasses);
            return instance.GetComponent<T>();
        }

        public T BindFromScriptableObject<T>(T scriptableObject, bool addInterfaces = false, bool addSubClasses = false) where T : ScriptableObject
        {
            BindInternal<T>(scriptableObject, addInterfaces, addSubClasses);
            return scriptableObject;
        }
        
        private void BindInternal<T>(T implementation, bool addInterfaces = false, bool addSubClasses = false)
        {
            var key = typeof(T);
            AddToDependenciesDict(key, implementation);

            if (implementation is IInitializable initializable)
            {
                _initializables.Add(initializable);
            }
            if (implementation is ITickable tickable)
            {
                _tickables.Add(tickable);
            }
            if (implementation is IFixedTickable fixedTickable)
            {
                _fixedTickables.Add(fixedTickable);
            }
            if (implementation is ILateTickable lateTickable)
            {
                _lateTickables.Add(lateTickable);
            }
            if (implementation is IDisposable disposable)
            {
                _disposables.Add(disposable);
            }
            if (implementation is IPausable pausable)
            {
                _pausables.Add(pausable);
            }
            if (implementation is IFocusable focusable)
            {
                _focusables.Add(focusable);
            }
            if (implementation is ICleanable cleanable)
            {
                _cleanables.Add(cleanable);
            }
            
            if (addInterfaces)
            {
                AddInterfaces(implementation);
            }
            if (addSubClasses)
            {
                AddSubClasses(implementation);
            }
        }

        private void AddToDependenciesDict(Type key, object implementation)
        {
            if (_dependencies.ContainsKey(key))
            {
                _dependencies[key] = implementation;
            }
            else
            {
                _dependencies.Add(key, implementation);
            }
        }
        
        public void UnBind<T>()
        {
            _dependencies.Remove(typeof(T));
        }
        
        private T ResolveInternal<T>(bool throwException = false)
        {
            if (_dependencies.ContainsKey(typeof(T)))
            {
                return (T) _dependencies[typeof(T)];
            }
            
            if (throwException)
            {
                throw new Exception($"[DiContainer] Error resolving the dependency type {typeof(T)}");
            }
            
            return default;
        }

        private object ResolveInternal(Type t, bool throwException = false)
        {
            if (_dependencies.TryGetValue(t, out var @internal))
            {
                return @internal;
            }
            
            if (throwException)
            {
                throw new Exception($"[DiContainer] Error resolving the dependency type {t}");
            }
            return null;
        }

        private void ResolveDependenciesInternal(object obj, bool throwException = false)
        {
            var type = obj.GetType();
            _objectFields = type.GetFields(_bindingAttr);

            for (var i=0 ; i < _objectFields.Length ; i++ )
            {
                if (!(Attribute.GetCustomAttribute(_objectFields[i], typeof(InjectAttribute)) is InjectAttribute))
                {
                    continue;
                }

                _injection = ResolveInternal(_objectFields[i].FieldType, throwException);

                if (_injection != null)
                {
                    _objectFields[i].SetValue(obj, _injection);
                }
            }
        }

        public static void ResolveDependencies(object obj, bool throwException = false)
        {
            if (_instance != null)
            {
                _instance.ResolveDependenciesInternal(obj, throwException);
                return;
            }
            
            if (throwException)
            {
                throw new Exception($"[DiContainer] The instance doesn't exist.");
            }
        }

        public static T Resolve<T>(bool throwException = false)
        {
            if (_instance != null)
            {
                return _instance.ResolveInternal<T>(throwException);
            }
            if (throwException)
            {
                throw new Exception($"[DiContainer] The instance doesn't exist.");
            }
            return default;
        }

        public static void Pause(bool paused)
        {
            if (_instance != null)
            {
                _instance.IsPaused = paused;
            }
        }

#if UNITY_EDITOR
        public static List<object> Dependencies(string filter = "") 
        {
            var dependencies = new List<object>();
            if (_instance == null)
            {
                return dependencies;
            }
            
            foreach (var dependency in _instance._dependencies)
            {
                if (!dependency.Key.IsClass)
                {
                    continue;
                }

                var dependencyName = dependency.Key.Name;
                if (!string.IsNullOrEmpty(filter) && !dependencyName.Contains(filter))
                {
                    continue;
                }
                if (dependency.Value == null)
                {
                    continue;
                }

                if (!dependencies.Exists(d => d.GetType().Name == dependency.Value.GetType().Name))
                {
                    dependencies.Add(dependency.Value);
                }
            }

            return dependencies.Distinct().OrderBy(d => d.GetType().Name).ToList();
        }
#endif
    }
}