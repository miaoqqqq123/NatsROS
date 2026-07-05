using NatsROS.Messages.Security;
using System;
using System.Collections.Generic;
using System.Windows;

namespace NatsROS.Dashboard.Security
{
    // ==========================================
    // 1. 全局会话存储 (Token 与 权限缓存)
    // ==========================================
    public static class GlobalSecurityContext
    {
        public static UserInfo? CurrentUser { get; private set; }
        public static string Token { get; private set; } = "";

        // 使用 HashSet 提升查询权限的速度 (O(1))
        private static HashSet<string> _permissions = new();

        public static void Login(UserInfo user, string token, List<string>? perms)
        {
            CurrentUser = user;
            Token = token;
            _permissions = new HashSet<string>(perms ?? new List<string>());

            // 触发事件，通知所有 UI 重新计算自己的状态
            OnUserChanged?.Invoke(null, EventArgs.Empty);
        }

        public static bool HasPermission(string permCode) => _permissions.Contains(permCode);

        public static event EventHandler? OnUserChanged;
    }

    // ==========================================
    // 2. WPF 黑魔法：权限附加属性拦截器
    // ==========================================
    public static class PermissionGuard
    {
        // 注册一个名为 "Require" 的附加属性
        public static readonly DependencyProperty RequireProperty =
            DependencyProperty.RegisterAttached("Require", typeof(string), typeof(PermissionGuard), new PropertyMetadata(null, OnRequireChanged));

        public static void SetRequire(UIElement element, string value) => element.SetValue(RequireProperty, value);
        public static string GetRequire(UIElement element) => (string)element.GetValue(RequireProperty);

        private static void OnRequireChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                // 当 XAML 解析到这个属性时，立刻进行权限评估
                EvaluateElement(element, e.NewValue as string);

                // 订阅全局用户切换事件（如果运行时换了账号，按钮瞬间自动变灰/亮起）
                GlobalSecurityContext.OnUserChanged += (s, args) =>
                {
                    element.Dispatcher.InvokeAsync(() => EvaluateElement(element, GetRequire(element)));
                };
            }
        }

        private static void EvaluateElement(UIElement element, string? requiredPermission)
        {
            if (string.IsNullOrEmpty(requiredPermission)) return;

            // 核心鉴权逻辑：没有权限，直接禁用控件 (变灰)
            // 高级玩法：您也可以设置为 element.Visibility = Visibility.Collapsed 让它彻底消失
            element.IsEnabled = GlobalSecurityContext.HasPermission(requiredPermission);
        }
    }
}