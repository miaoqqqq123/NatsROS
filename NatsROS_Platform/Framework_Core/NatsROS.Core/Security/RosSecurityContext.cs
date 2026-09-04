using System;
using System.Collections.Generic;

namespace NatsROS.Core.Security
{
    /// <summary>
    /// 全网公共的跨插件安全上下文 (存放于 Core 核心库)
    /// 允许所有的外部业务插件安全地读取当前登录的操作员身份
    /// </summary>
    public static class RosSecurityContext
    {
        public static string Username { get; private set; } = "";
        public static string DisplayName { get; private set; } = "未登录";
        public static string Role { get; private set; } = "";
        public static string Token { get; private set; } = "";

        private static HashSet<string> _permissions = new();

        public static void Login(string username, string displayName, string role, string token, IEnumerable<string>? perms)
        {
            Username = username;
            DisplayName = displayName;
            Role = role;
            Token = token;
            _permissions = new HashSet<string>(perms ?? Array.Empty<string>());
        }

        public static void Logout()
        {
            Username = ""; DisplayName = "未登录"; Role = ""; Token = "";
            _permissions.Clear();
        }

        public static bool HasPermission(string permCode) => _permissions.Contains(permCode);
    }
}