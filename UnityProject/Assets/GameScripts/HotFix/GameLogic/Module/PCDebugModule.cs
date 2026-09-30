using TEngine;

#if UNITY_STANDALONE_WIN

namespace GameLogic
{
    /// <summary>
    /// PC存储调试模块，在没有接入steam的情况下
    /// </summary>
    public class PCDebugModule : Singleton<PCDebugModule>, IUpdate
    {
        private const string save = "save";

        protected override void OnInit()
        {
            base.OnInit();
        }

        public void OnUpdate()
        {
        }

        /// <summary>
        /// 读取云存储
        /// </summary>
        public string LoadCloudStorage()
        {
            if (Utility.PlayerPrefs.HasKey(save))
            {
                return Utility.PlayerPrefs.GetString(save);
            }

            return null;
        }

        // 删除云存档文件
        public void DeleteCloudSave()
        {
            if (Utility.PlayerPrefs.HasKey(save))
            {
                Utility.PlayerPrefs.DeleteKey(save);
            }
        }

        /// <summary>
        /// 保存云存储
        /// </summary>
        public void SaveCloudStorage(string saveValue)
        {
            Utility.PlayerPrefs.SetString(save, saveValue);
        }
    }
}

#endif