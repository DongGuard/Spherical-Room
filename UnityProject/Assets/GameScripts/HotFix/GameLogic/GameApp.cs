using System.Collections.Generic;
using System.Reflection;
using GameLogic;
using TEngine;

#pragma warning disable CS0436


/// <summary>
/// 游戏App。
/// </summary>
public partial class GameApp
{
    private static List<Assembly> _hotfixAssembly;

    /// <summary>
    /// 热更域App主入口。
    /// </summary>
    /// <param name="objects"></param>
    public static void Entrance(object[] objects)
    {
        GameEventHelper.Init();
        _hotfixAssembly = (List<Assembly>)objects[0];
        Log.Warning("======= 看到此条日志代表你成功运行了热更新代码 =======");
        Log.Warning("======= Entrance GameApp =======");
        Utility.Unity.AddDestroyListener(Release);
        StartGameLogic();
    }

    private static void StartGameLogic()
    {
        GameEvent.Get<ILoginUI>().ShowLoginUI();
        GameModule.UI.Active();
#if UNITY_EDITOR
        GameModule.SteamModule.Active(); // 只在编辑器下执行
#elif UNITY_STANDALONE_WIN
        GameModule.PCDebugModule.Active();
#elif UNITY_ANDROID || UNITY_IOS
    GameModule.TapTap.Active();  // 在安卓和iOS下执行
#endif
    }

    private static void Release()
    {
        SingletonSystem.Release();
        Log.Warning("======= Release GameApp =======");
    }
}