using System;
using System.Linq;
using System.Windows;
using Application = System.Windows.Application;

namespace NatsROS.Dashboard.Localization
{
    public static class LocalizationManager
    {
        private static bool _isEnglish = false;

        public static void ToggleLanguage()
        {
            _isEnglish = !_isEnglish;
            string dictPath = _isEnglish ? "Localization/StringResources.en-US.xaml" : "Localization/StringResources.xaml";

            var dictionary = new ResourceDictionary { Source = new Uri(dictPath, UriKind.Relative) };

            // 移除旧字典，加入新字典，WPF 的 DynamicResource 会瞬间自动刷新全屏幕文字！
            var appResources = Application.Current.Resources;
            var oldDict = appResources.MergedDictionaries.FirstOrDefault(d => d.Source.OriginalString.Contains("StringResources"));

            if (oldDict != null) appResources.MergedDictionaries.Remove(oldDict);
            appResources.MergedDictionaries.Add(dictionary);
        }
    }
}