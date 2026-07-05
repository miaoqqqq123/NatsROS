using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Messages.Security;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace NatsROS.Dashboard.Plugins.SecurityStudio
{
    // 用于给 Grid 绑定的包装类 (支持打钩联动)
    public class PermissionCheckItem : INotifyPropertyChanged
    {
        private bool _isGranted;
        public string Code { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsGranted { get => _isGranted; set { _isGranted = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class SecurityStudioView : UserControl
    {
        private readonly RosServiceClient<GetUsersReq, GetUsersRes> _getUsersClient;
        private readonly RosServiceClient<SaveUserReq, SaveUserRes> _saveUserClient;
        private readonly RosServiceClient<DeleteUserReq, DeleteUserRes> _delUserClient;

        private readonly RosServiceClient<GetRolesReq, GetRolesRes> _getRolesClient;
        private readonly RosServiceClient<SaveRoleReq, SaveRoleRes> _saveRoleClient;
        private readonly RosServiceClient<DeleteRoleReq, DeleteRoleRes> _delRoleClient;
        private readonly RosServiceClient<GetManifestReq, GetManifestRes> _manifestClient;

        private ObservableCollection<UserInfo> _users = new();
        private ObservableCollection<PermissionCheckItem> _permissionItems = new();
        private List<PermissionNode> _manifest = new();
        private List<RoleInfo> _roles = new();

        public SecurityStudioView(INatsClient nats)
        {
            InitializeComponent();
            _getUsersClient = new(nats, "sec.get_users");
            _saveUserClient = new(nats, "sec.save_user");
            _delUserClient = new(nats, "sec.delete_user");

            _getRolesClient = new(nats, "sec.get_roles");
            _saveRoleClient = new(nats, "sec.save_role");
            _delRoleClient = new(nats, "sec.delete_role");
            _manifestClient = new(nats, "sec.get_manifest");

            GridUsers.ItemsSource = _users;
            GridPermissions.ItemsSource = _permissionItems;

            Loaded += async (s, e) => await LoadAllDataAsync();
        }

        private async System.Threading.Tasks.Task LoadAllDataAsync()
        {
            try
            {
                // 1. 获取全景权限清单 (字典)
                var manifestRes = await _manifestClient.CallAsync(new GetManifestReq(), TimeSpan.FromSeconds(2));
                if (manifestRes != null) _manifest = manifestRes.Manifest;

                // 2. 获取角色列表
                var rolesRes = await _getRolesClient.CallAsync(new GetRolesReq(), TimeSpan.FromSeconds(2));
                if (rolesRes != null)
                {
                    _roles = rolesRes.Roles;
                    CboRoleSelect.ItemsSource = _roles.Select(r => r.RoleName).ToList();
                    CboUserRole.ItemsSource = _roles.Select(r => r.RoleName).ToList();
                }

                // 3. 获取用户列表
                var usersRes = await _getUsersClient.CallAsync(new GetUsersReq(), TimeSpan.FromSeconds(2));
                if (usersRes != null)
                {
                    _users.Clear();
                    foreach (var u in usersRes.Users) _users.Add(u);
                }
            }
            catch (Exception ex) { MessageBox.Show("加载数据失败: " + ex.Message); }
        }

        // ==========================================
        // 左侧：用户管理
        // ==========================================
        private void GridUsers_SelectedItemChanged(object sender, DevExpress.Xpf.Grid.SelectedItemChangedEventArgs e)
        {
            if (e.NewItem is UserInfo user)
            {
                TxtUsername.Text = user.Username; TxtUsername.IsReadOnly = true;
                TxtDisplayName.Text = user.DisplayName;
                CboUserRole.EditValue = user.Role;
                TxtNewPassword.Password = "";
            }
        }

        private void BtnNewUser_Click(object sender, RoutedEventArgs e)
        {
            GridUsers.SelectedItem = null;
            TxtUsername.Text = ""; TxtUsername.IsReadOnly = false;
            TxtDisplayName.Text = ""; CboUserRole.EditValue = null; TxtNewPassword.Password = "";
        }

        private async void BtnSaveUser_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtUsername.Text) || string.IsNullOrWhiteSpace(CboUserRole.Text)) return;
            try
            {
                await _saveUserClient.CallAsync(new SaveUserReq(TxtUsername.Text, CboUserRole.Text, TxtDisplayName.Text, TxtNewPassword.Password), TimeSpan.FromSeconds(2));
                await LoadAllDataAsync();
                MessageBox.Show("用户保存成功");
            }
            catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message); }
        }

        private async void BtnDelUser_Click(object sender, RoutedEventArgs e)
        {
            if (GridUsers.SelectedItem is UserInfo user)
            {
                if (MessageBox.Show($"确定删除用户 {user.Username} 吗？", "警告", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    var res = await _delUserClient.CallAsync(new DeleteUserReq(user.Username));
                    if (res != null && !res.Success) MessageBox.Show(res.Message);
                    await LoadAllDataAsync();
                }
            }
        }

        // ==========================================
        // 右侧：角色与权限矩阵管理
        // ==========================================
        private void CboRoleSelect_SelectedIndexChanged(object sender, RoutedEventArgs e)
        {
            string roleName = CboRoleSelect.Text;
            var role = _roles.FirstOrDefault(r => r.RoleName == roleName);
            var granted = role?.GrantedPermissions ?? new List<string>();

            // 重绘权限矩阵打钩状态
            _permissionItems.Clear();
            foreach (var node in _manifest)
            {
                _permissionItems.Add(new PermissionCheckItem
                {
                    Code = node.Code,
                    Category = node.Category,
                    Description = node.Description,
                    IsGranted = granted.Contains(node.Code) // 如果这角色有这权限，自动打钩
                });
            }
        }

        private async void BtnSaveRole_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CboRoleSelect.Text)) return;
            try
            {
                // 提取所有被打钩的特征码
                var newPerms = _permissionItems.Where(p => p.IsGranted).Select(p => p.Code).ToList();
                var newRole = new RoleInfo(CboRoleSelect.Text, newPerms);

                await _saveRoleClient.CallAsync(new SaveRoleReq(newRole), TimeSpan.FromSeconds(2));
                await LoadAllDataAsync();
                MessageBox.Show("角色权限保存成功！");
            }
            catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message); }
        }

        private async void BtnDelRole_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CboRoleSelect.Text)) return;
            var res = await _delRoleClient.CallAsync(new DeleteRoleReq(CboRoleSelect.Text));
            if (res != null && !res.Success) MessageBox.Show(res.Message);
            await LoadAllDataAsync();
            CboRoleSelect.Text = ""; _permissionItems.Clear();
        }
    }
}