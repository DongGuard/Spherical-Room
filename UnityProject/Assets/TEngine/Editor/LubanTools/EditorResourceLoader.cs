#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

public static class EditorResourceLoader
{
    /// <summary>
    /// 根据资源名字加载任意类型的资源
    /// </summary>
    /// <typeparam name="T">资源类型，例如 GameObject, Texture2D, Sprite</typeparam>
    /// <param name="resourceName">资源名字</param>
    /// <param name="searchFolders">搜索路径，可为空数组表示全项目</param>
    /// <returns>加载到的资源，找不到返回 null</returns>
    public static T LoadAssetByName<T>(string resourceName, string[] searchFolders = null) where T : UnityEngine.Object
    {
        if (string.IsNullOrEmpty(resourceName))
            return null;

        if (searchFolders == null || searchFolders.Length == 0)
            searchFolders = new[] { "Assets" };

        // 查找资源 GUID
        string[] guids = AssetDatabase.FindAssets(resourceName + $" t:{typeof(T).Name}", searchFolders);

        if (guids.Length == 0)
        {
            Debug.LogWarning($"EditorResourceLoader: 没有找到资源 {resourceName} 类型 {typeof(T).Name}");
            return null;
        }

        // 取第一个匹配的 GUID
        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        T asset = AssetDatabase.LoadAssetAtPath<T>(path);
        return asset;
    }

    /// <summary>
    /// 实例化 Prefab 到场景中（Editor 下安全）
    /// </summary>
    public static GameObject InstantiatePrefab(GameObject prefab, Transform parent = null)
    {
        if (prefab == null) return null;
        GameObject go = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (parent != null) go.transform.SetParent(parent);
        return go;
    }
    
    public static T LoadTable<T>(string tableName) where T : class
    {
        // Editor 下路径
        string path = $"Assets/AssetRaw/Configs/bytes/pkj_{tableName.ToLower()}.bytes";

        if (!System.IO.File.Exists(path))
        {
            Debug.LogError($"文件不存在: {path}");
            return null;
        }

        byte[] bytes = System.IO.File.ReadAllBytes(path);
        var buf = new Luban.ByteBuf(bytes);

        // 动态构造 T，要求 T 有 (ByteBuf) 构造函数
        T table = System.Activator.CreateInstance(typeof(T), buf) as T;
        return table;
    }

}
#endif