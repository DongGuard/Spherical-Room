using UnityEngine;
using UnityEngine.UI;
using TEngine;
using TMPro;

namespace GameLogic
{
    [Window(UILayer.UI)]
    class JoinUI : UIWindow
    {
        #region 脚本工具生成的代码
        private TMP_Text _mtmp_textRoomName;
        private Button _btnCreate;
        private RectTransform _rectEnterPass;
        private TMP_InputField _mtmp_InputFieldpass;
        protected override void ScriptGenerator()
        {
            _mtmp_textRoomName = FindChildComponent<TMP_Text>("Back/Scroll View/Viewport/Content/Room/mtmp_textRoomName");
            _btnCreate = FindChildComponent<Button>("Back/m_btnCreate");
            _rectEnterPass = FindChildComponent<RectTransform>("Back/m_rectEnterPass");
            _mtmp_InputFieldpass = FindChildComponent<TMP_InputField>("Back/m_rectEnterPass/Image/mtmp_InputFieldpass");
            _btnCreate.onClick.AddListener(OnClickCreateBtn);
        }
        #endregion

        #region 事件
        private void OnClickCreateBtn()
        {
        }
        #endregion

    }
}