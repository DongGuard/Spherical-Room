using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using TEngine;

namespace GameLogic
{
    /// <summary>
    /// 游戏内 HUD：退出按钮。主机显示"解散房间"，客户端显示"退出房间"，
    /// </summary>
    [Window(UILayer.UI)]
    class GameUI : UIWindow
    {
        #region 脚本工具生成的代码
        private Button _btnExit;
        private TMP_Text _mtmp_textExit;
        protected override void ScriptGenerator()
        {
            _btnExit = FindChildComponent<Button>("Back/m_btnExit");
            _mtmp_textExit = FindChildComponent<TMP_Text>("Back/m_btnExit/Text (TMP)");
        }
        #endregion

        protected override void OnCreate()
        {
            _btnExit.onClick.AddListener(OnClickExitBtn);
            RefreshRoleText();

            // Back 是全屏视觉边框，不参与射线检测，否则会挡住"点击画面重新锁定光标"
            Image backImage = FindChildComponent<Image>("Back");
            if (backImage != null)
            {
                backImage.raycastTarget = false;
            }
        }

        protected override void OnRefresh()
        {
            RefreshRoleText();
        }

        private void RefreshRoleText()
        {
            if (RoomManager.IsValid)
            {
                _mtmp_textExit.text = RoomManager.Instance.IsHost ? "解散房间" : "退出房间";
            }
        }

        private void OnClickExitBtn()
        {
            Debug.Log("[GameUI] 退出按钮被点击");
            _btnExit.interactable = false;
            GameModule.UI.ShowUI<TipsUI>(RoomManager.Instance.IsHost ? "正在解散房间..." : "正在退出房间...");
            ExitAsync().Forget();
        }

        private async UniTaskVoid ExitAsync()
        {
            // 等 Tips 滑入动画播完（完全显示）后再执行解散/退出，避免玩家消失、菜单弹出抢在提示之前
            await UniTask.Delay(200, true);
            RoomManager.Instance.LeaveRoom();
        }
    }
}
