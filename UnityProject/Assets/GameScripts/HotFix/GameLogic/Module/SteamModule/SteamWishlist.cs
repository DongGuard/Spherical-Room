#if UNITY_STANDALONE_WIN
using System;
using System.Diagnostics;
using Steamworks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic
{
    /// <summary>
    /// Steam愿望单
    /// </summary>
    public class SteamWishlist : Singleton<SteamWishlist>, IUpdate
    {
        public void OnUpdate()
        {
          
        }

        /// <summary>
        /// 引导玩家打开steam商店并手动加入愿望单
        /// </summary>
        public void StartSteamClient()
        {
            if (SteamAPI.IsSteamRunning())
            {
                // 将 AppID 转换为 AppId_t 类型
                AppId_t appId = new AppId_t(480);  // 480 是测试用的 AppID
                SteamFriends.ActivateGameOverlayToStore(appId, EOverlayToStoreFlag.k_EOverlayToStoreFlag_None);
                Debug.Log("Opening Steam Store Overlay for AppID: " + appId);
            }
            else
            {
                try
                {
                    Process.Start("steam://open/main");
                    Debug.LogWarning("Steam is not running. Launching Steam client...");
                }
                catch (Exception e)
                {
                    Debug.LogError("Steam is not installed or cannot be opened. Exception: " + e.Message);
                    Application.OpenURL("https://store.steampowered.com/");
                }
            }
        }
    }
}
#endif