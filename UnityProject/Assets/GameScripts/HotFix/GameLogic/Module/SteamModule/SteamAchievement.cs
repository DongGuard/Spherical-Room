#if UNITY_STANDALONE_WIN
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// Steam成就
    /// </summary>
    public class SteamAchievement : Singleton<SteamAchievement>, IUpdate
    {
        protected override void OnInit()
        {
            base.OnInit();
            if (SteamAPI.Init())
            {
                // 监听成就存储回调
                Callback<UserAchievementStored_t>.Create(OnAchievementStored);
                // 监听用户统计回调
                Callback<UserStatsReceived_t>.Create(OnUserStatsReceived);
            }
        }

        private void OnAchievementStored(UserAchievementStored_t callback)
        {
            Debug.Log("Achievement stored: " + callback.m_nGameID);
        }

        private void OnUserStatsReceived(UserStatsReceived_t callback)
        {
            Debug.Log("User stats received: " + callback.m_nGameID);
        }


        public void OnUpdate()
        {
        }

        /// <summary>
        /// 判断成就是否解锁
        /// </summary>
        /// <param name="achievementID"></param>
        /// <returns></returns>
        public bool IsAchievementUnlocked(string achievementID)
        {
            bool achieved = false;
            SteamUserStats.GetAchievement(achievementID, out achieved);
            return achieved;
        }

        /// <summary>
        /// 解锁成就
        /// </summary>
        /// <param name="achievementID"></param>
        public void UnlockAchievement(string achievementID)
        {
            if (SteamUserStats.SetAchievement(achievementID))
            {
                SteamUserStats.StoreStats(); // 确保成就被存储
                Debug.Log("Achievement unlocked: " + achievementID);
            }
            else
            {
                Debug.LogError("Failed to unlock achievement: " + achievementID);
            }
        }

        /// <summary>
        /// 获取某个成就的解锁时间。
        /// </summary>
        /// <param name="achievementID"></param>
        public void CheckAchievement(string achievementID)
        {
            bool achieved = false;
            uint unlockTime = 0;
            if (SteamUserStats.GetAchievementAndUnlockTime(achievementID, out achieved, out unlockTime))
            {
                if (achieved)
                {
                    Debug.Log($"Achievement {achievementID} is unlocked at {unlockTime}");
                }
                else
                {
                    Debug.Log($"Achievement {achievementID} is not unlocked yet.");
                }
            }
            else
            {
                Debug.LogError($"Failed to get achievement status for {achievementID}");
            }
        }
    }
}
#endif