using UnityEngine.UI;
using TEngine;
using TMPro;

namespace GameLogic
{
    /// <summary>
    /// Steam 好友邀请确认窗口：文本为"邀请人昵称 邀请你加入房间..."，
    /// 确认走 RoomManager.AcceptPendingInvite（离开当前房间并加入），拒绝清掉暂存邀请。
    /// </summary>
    [Window(UILayer.UI)]
    class InvitationUI : UIWindow
    {
        #region 脚本工具生成的代码
        private Image _imgBackager;
        private TMP_Text _mtmp_textValue;
        private Button _btnConfirm;
        private Button _btnCancel;
        protected override void ScriptGenerator()
        {
            _imgBackager = FindChildComponent<Image>("m_imgBackager");
            _mtmp_textValue = FindChildComponent<TMP_Text>("Image/mtmp_textValue");
            _btnConfirm = FindChildComponent<Button>("Image/m_btnConfirm");
            _btnCancel = FindChildComponent<Button>("Image/m_btnCancel");
        }
        #endregion

        protected override void OnCreate()
        {
            _btnConfirm.onClick.AddListener(OnClickConfirmBtn);
            _btnCancel.onClick.AddListener(OnClickCancelBtn);
        }

        protected override void OnDestroy()
        {
            _btnConfirm.onClick.RemoveListener(OnClickConfirmBtn);
            _btnCancel.onClick.RemoveListener(OnClickCancelBtn);
        }

        protected override void OnRefresh()
        {
            if (UserData is string message && !string.IsNullOrEmpty(message))
            {
                _mtmp_textValue.text = message;
            }
        }

        #region 事件
        private void OnClickConfirmBtn()
        {
            Close();
            RoomManager.Instance.AcceptPendingInvite();
        }

        private void OnClickCancelBtn()
        {
            Close();
            RoomManager.Instance.DeclinePendingInvite();
        }
        #endregion
    }
}