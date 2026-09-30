using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Launcher
{
    /// <summary>
    /// UI加载界面。
    /// </summary>
    public class UILoading : UIBase
    {
        [SerializeField]
        public Text text;
        public float interval = 0.01f;
        private bool _isRunning = true;

        private void Awake()
        {
            StartLoop().Forget();
        }

        private async UniTask StartLoop()
        {
            int dotCount = 0;
            while (_isRunning)
            {
                dotCount = (dotCount % 4) + 1; // 1-4循环
                text.text = new string('.', dotCount);
                await UniTask.Delay(System.TimeSpan.FromSeconds(interval));
            }
        }

        private void OnDestroy()
        {
            _isRunning = false; // 停止循环
        }
    }
}