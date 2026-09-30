#if UNITY_STANDALONE_WIN
using Steamworks;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// Steam模块
    /// </summary>
    public class SteamModule : Singleton<SteamModule>, IUpdate
    {
        public SteamCloudStorage SteamCloudStorage => SteamCloudStorage.Instance; //云存储
        public SteamAchievement SteamAchievement => SteamAchievement.Instance; //成就
        public SteamWishlist SteamWishlist => SteamWishlist.Instance; //愿望单

        protected override void OnInit()
        {
            base.OnInit();
            if (!SteamManager.Initialized)
            {
                // 初始化 Steam API
                if (!SteamAPI.Init())
                {
                    Debug.LogError("Steam API Initialization failed.");
                }
                else
                {
                    Debug.LogWarning("Steam API Initialized.");
                }
            }

            SteamCloudStorage.Active();
            SteamAchievement.Active();
            SteamWishlist.Active();
            Application.quitting += OnApplicationQuit;
            // 获取玩家昵称
            string playerName = SteamFriends.GetPersonaName();
            Debug.Log("Steam API Initialization failed! SteamSDK初始化成功！" + " " + "玩家昵称: " + playerName);
        }

        public void OnUpdate()
        {
            if (SteamManager.Initialized)
            {
                // 每帧运行回调
                SteamAPI.RunCallbacks();
            }
        }

        private void OnApplicationQuit()
        {
            if (SteamManager.Initialized)
            {
                // 确保在退出时正确关闭 Steam API
                SteamAPI.Shutdown();
                Debug.Log("回收SteamSDK缓存成功!");
            }
        }
    }
}

#endif