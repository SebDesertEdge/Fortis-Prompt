using UnityEditor;
using UnityEngine;

namespace Code.Core.Utilities
{
#if UNITY_EDITOR
    public class ScriptableObjectUtility
    {
        /// <summary>
        //	This makes it easy to create, name and place unique new ScriptableObject asset files.
        /// </summary>
        public static T CreateAsset<T>(string path = null, string fileName = null) where T : ScriptableObject
        {
            var assetPath =  $"{path}/{fileName}.asset";
            if (System.IO.File.Exists(assetPath))
            {
                System.IO.File.Delete(assetPath);    
            }
            
            T asset = ScriptableObject.CreateInstance<T>();

            if (string.IsNullOrEmpty(path))
            {
                path = AssetDatabase.GetAssetPath(Selection.activeObject);
            }

            if (path == "")
            {
                path = "Assets/Data/RawData";
            }
            else if (System.IO.Path.GetExtension(path) != "")
            {
                path = path.Replace(System.IO.Path.GetFileName(AssetDatabase.GetAssetPath(Selection.activeObject)), "");
            }

            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
            }

            fileName = string.IsNullOrEmpty(fileName) ? $"New_{typeof(T).ToString()}" : fileName;
            string rawPathAndName = $"{path}/{fileName}.asset";
            string assetPathAndName = AssetDatabase.GenerateUniqueAssetPath(rawPathAndName);

            AssetDatabase.CreateAsset(asset, assetPathAndName);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
            return asset;
        }

        public static T GetAssetWithType<T>(string assetName) where T : UnityEngine.Object
        {
            string typeName = typeof(T).Name;
            var guids = AssetDatabase.FindAssets($"t:{typeName} {assetName}");
            foreach (string guid in guids)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null)
                {
                    return EditorUtility.InstanceIDToObject(asset.GetInstanceID()) as T;
                }
            }

            return default(T);
        }

        public static T[] GetAssetsWithType<T>(string assetName) where T : ScriptableObject
        {
            string[] guids = AssetDatabase.FindAssets($"t:{typeof(T).Name} {assetName}");
            T[] a = new T[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                a[i] = AssetDatabase.LoadAssetAtPath<T>(path);
            }
            return a;
        }

        public static T[] GetAssetsWithType<T>() where T : ScriptableObject
        {
            string[] guids = AssetDatabase.FindAssets("t:" + typeof(T).Name);
            T[] a = new T[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                a[i] = AssetDatabase.LoadAssetAtPath<T>(path);
            }
            return a;
        }
    }
#endif
}