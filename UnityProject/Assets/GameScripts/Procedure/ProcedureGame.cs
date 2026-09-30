using Cysharp.Threading.Tasks;
using Launcher;
using TEngine;

namespace Procedure
{
    public class ProcedureGame : ProcedureBase
    {
        public override bool UseNativeDialog { get; }

        protected override void OnEnter(IFsm<IProcedureModule> procedureOwner)
        {
            base.OnEnter(procedureOwner);
            GameEvent.AddEventListener(Constant.GameEvent.CloseLoading,CloseUILoading);
            StartGame().Forget();
        }

        private async UniTaskVoid StartGame()
        {
            await UniTask.Yield();
        }

        private void CloseUILoading()
        {
            LauncherMgr.Hide(UIDefine.UILoading);
        }
    }
}