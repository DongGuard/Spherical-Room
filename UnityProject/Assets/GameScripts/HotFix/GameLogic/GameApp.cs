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
        MirrorHotfixInitializer.Initialize(_hotfixAssembly);
        Log.Warning("======= 看到此条日志代表你成功运行了热更新代码 =======");
        Log.Warning("======= Entrance GameApp =======");
        Utility.Unity.AddDestroyListener(Release);
        StartGameLogic();
    }

    private static void StartGameLogic()
    {
        LobbyUIFlow.Instance.ShowMenu();
        GameModule.UI.Active();
#if UNITY_STANDALONE_WIN
        if (SteamManager.Initialized)
        {
            GameModule.SteamModule.Active(); 
        }
        else
        {
            Log.Warning("Steam 未启动或初始化失败，使用本地模式");
            GameModule.PCDebugModule.Active();
            GameEvent.Send(Constant.GameEvent.CloseLoading);
        }
#else
        GameEvent.Send(Constant.GameEvent.CloseLoading);
#endif
    }

    private static void Release()
    {
        SingletonSystem.Release();
        Log.Warning("======= Release GameApp =======");
    }
}