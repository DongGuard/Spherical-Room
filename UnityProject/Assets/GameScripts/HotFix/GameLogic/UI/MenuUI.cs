using UnityEngine;
using UnityEngine.UI;
using TEngine;

namespace GameLogic
{
    [Window(UILayer.UI)]
    class MenuUI : UIWindow
    {
        #region 脚本工具生成的代码
        private Button _btnCreate;
        private Button _btnJoin;
        protected override void ScriptGenerator()
        {
            _btnCreate = FindChildComponent<Button>("m_btnCreate");
            _btnJoin = FindChildComponent<Button>("m_btnJoin");
            _btnCreate.onClick.AddListener(OnClickCreateBtn);
            _btnJoin.onClick.AddListener(OnClickJoinBtn);
        }
        #endregion

        #region 事件
        private void OnClickCreateBtn()
        {
            GameModule.UI.ShowUI<CreateRoom>();
        }
        private void OnClickJoinBtn()
        {
        }
        #endregion

    }
}