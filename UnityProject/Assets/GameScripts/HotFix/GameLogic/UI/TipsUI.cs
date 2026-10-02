using Cysharp.Threading.Tasks;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using TEngine;
using Ease = DG.Tweening.Ease;

namespace GameLogic
{
    /// <summary>
    /// 提示窗口
    /// </summary>
    [Window(UILayer.Tips)]
    class TipsUI : UIWindow
    {
        #region 脚本工具生成的代码
        private Image _imgBackager;
        private TMP_Text _mtmp_textTips;
        protected override void ScriptGenerator()
        {
            _imgBackager = FindChildComponent<Image>("m_imgBackager");
            _mtmp_textTips = FindChildComponent<TMP_Text>("mtmp_textTips");
        }
        #endregion

        private const float InDuration = 0.15f;
        private const float OutDuration = 0.35f;
        private const int ShowMs = 2600;

        private Vector2 _restPosition;
        private float _imageAlpha = 1f;
        private Tween _tween;
        private int _showing;

        private static UniTaskCompletionSource _shownSource;

        /// <summary>
        /// 等待当前提示完全显示（背景与文字淡入到位）。
        /// </summary>
        public static UniTask WaitShownAsync()
        {
            _shownSource ??= new UniTaskCompletionSource();
            return _shownSource.Task;
        }

        protected override void OnCreate()
        {
            _restPosition = rectTransform.anchoredPosition;
            if (_imgBackager != null)
            {
                _imageAlpha = _imgBackager.color.a;
                // 提示背景不拦截点击
                _imgBackager.raycastTarget = false;
            }
        }

        protected override void OnRefresh()
        {
            if (UserData is string message && !string.IsNullOrEmpty(message))
            {
                PlayAsync(message).Forget();
            }
        }

        protected override void OnDestroy()
        {
            _tween?.Kill(true);
            _shownSource?.TrySetResult();
            _shownSource = null;
        }

        private async UniTaskVoid PlayAsync(string message)
        {
            int generation = ++_showing;
            _tween?.Kill(true);

            _mtmp_textTips.text = message;
            SetAlpha(0f);
            rectTransform.anchoredPosition = _restPosition + new Vector2(0f, -24f);

            Sequence sequence = DOTween.Sequence();
            if (_imgBackager != null)
            {
                sequence.Join(_imgBackager.DOFade(_imageAlpha, InDuration));
            }

            sequence.Join(_mtmp_textTips.DOFade(1f, InDuration))
                .Join(rectTransform.DOAnchorPos(_restPosition, InDuration).SetEase(Ease.OutCubic));
            _tween = sequence;
            await _tween.AsyncWaitForCompletion();
            _shownSource?.TrySetResult();
            _shownSource = null;
            if (generation != _showing || IsDestroyed)
            {
                return;
            }

            await UniTask.Delay(ShowMs, true);
            if (generation != _showing || IsDestroyed)
            {
                return;
            }

            Sequence outSequence = DOTween.Sequence();
            if (_imgBackager != null)
            {
                outSequence.Join(_imgBackager.DOFade(0f, OutDuration));
            }

            outSequence.Join(_mtmp_textTips.DOFade(0f, OutDuration));
            _tween = outSequence;
            await _tween.AsyncWaitForCompletion();
            if (generation != _showing || IsDestroyed)
            {
                return;
            }

            Close();
        }

        private void SetAlpha(float alpha)
        {
            if (_imgBackager != null)
            {
                Color color = _imgBackager.color;
                color.a = alpha * _imageAlpha;
                _imgBackager.color = color;
            }

            _mtmp_textTips.alpha = alpha;
        }
    }
}
