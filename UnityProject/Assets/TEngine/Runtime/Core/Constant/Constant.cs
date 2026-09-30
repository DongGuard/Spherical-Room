namespace TEngine
{
    /// <summary>
    /// 常用设置相关常量。
    /// </summary>
    public static partial class Constant
    {
        public static class Setting
        {
            public const string Language = "Setting.Language";
            public const string SoundGroupMuted = "Setting.{0}Muted";
            public const string SoundGroupVolume = "Setting.{0}Volume";
            public const string MusicMuted = "Setting.MusicMuted";
            public const string MusicVolume = "Setting.MusicVolume";
            public const string SoundMuted = "Setting.SoundMuted";
            public const string SoundVolume = "Setting.SoundVolume";
            public const string UISoundMuted = "Setting.UISoundMuted";
            public const string UISoundVolume = "Setting.UISoundVolume";
        }
        
        /// <summary>
        /// UI事件
        /// </summary>
        public static class GameEvent
        {
            public const string CloseLoading = "关闭加载UI";
            public const string ReFreshLocalization = "刷新多语言";
        }
    }
}