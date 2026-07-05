using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Messages.Security;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace NatsROS.Dashboard.Security
{
    public partial class LoginWindow : DevExpress.Xpf.Core.ThemedWindow
    {
        private readonly RosServiceClient<LoginReq, LoginRes> _loginClient;

        public LoginWindow(INatsClient nats)
        {
            DevExpress.Xpf.Core.ApplicationThemeHelper.ApplicationThemeName = "Win11Dark";
            InitializeComponent();

            // 指向 SecurityManagerNode 提供的大管家鉴权路由
            _loginClient = new RosServiceClient<LoginReq, LoginRes>(nats, "auth.login");
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            BtnLogin.IsEnabled = false;
            LblError.Text = "⏳ 正在校验身份...";

            try
            {
                var req = new LoginReq(TxtUsername.Text.Trim(), TxtPassword.Password);
                var res = await _loginClient.CallAsync(req, TimeSpan.FromSeconds(3));

                if (res != null && res.Success && res.User != null)
                {
                    // 【核心】：登录成功！将信息注入全局上下文
                    GlobalSecurityContext.Login(res.User, res.Token, res.GrantedPermissions);

                    this.DialogResult = true; // 放行，关闭登录框
                    this.Close();
                }
                else
                {
                    LblError.Text = res?.Message ?? "⚠️ 连接大管家节点超时！";
                }
            }
            catch (Exception ex)
            {
                LblError.Text = "⚠️ 网络异常: " + ex.Message;
            }
            finally
            {
                BtnLogin.IsEnabled = true;
            }
        }
    }
}