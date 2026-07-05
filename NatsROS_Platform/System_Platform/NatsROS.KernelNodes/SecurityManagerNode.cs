using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;
using NatsROS.Messages.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NatsROS.KernelNodes
{
    [RosNode(DisplayName = "身份与安全大管家 (IAM)", Category = "系统核心 (System Core)", Description = "提供RBAC动态权限管理、Token签发及全网权限点自动发现")]
    public class SecurityManagerNode(INatsClient nats, string nodeName, ILogger<SecurityManagerNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        // 存储结构
        private readonly List<PermissionNode> _permissionsManifest = new();
        private Dictionary<string, RoleInfo> _roles = new();

        /// <summary>
        /// 内部用的用户存储结构 (包含密码哈希，绝不直接传给网络)
        /// </summary>
        private class UserEntity
        {
            public string Username { get; set; } = "";
            public string PasswordHash { get; set; } = "";
            public string Role { get; set; } = "";
            public string DisplayName { get; set; } = "";
        }
        private Dictionary<string, UserEntity> _users = new();

        private string _rolesPath = "";
        private string _usersPath = "";
        private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 从工作区的 Config 目录下要！
            _rolesPath = NatsROS.Core.Environment.WorkspaceManager.GetConfigPath("security_roles.json");
            _usersPath = NatsROS.Core.Environment.WorkspaceManager.GetConfigPath("security_users.json");

            // 1. 【核心黑魔法】：地毯式反射扫描全网权限点！
            ScanPermissionsManifest();

            // 2. 加载或初始化安全数据库
            LoadOrInitializeDatabases();

            return Task.CompletedTask;
        }

        private void ScanPermissionsManifest()
        {
            _permissionsManifest.Clear();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var asm in assemblies)
            {
                string asmName = asm.GetName().Name ?? "";


                // 只有名字以 ".Messages" 结尾的业务契约库，或者平台自带的基础契约库，才允许入内！
                if (!asmName.EndsWith(".Messages") && asmName != "NatsROS.Core" && asmName != "NatsROS.Messages")
                {
                    continue;
                }

                // 读取 DLL 门口的元数据牌子，如果不包含权限声明，直接跳过！
                if (!asm.IsDefined(typeof(ContainsNatsRosPermissionsAttribute), false))
                {
                    continue;
                }

                // 真正安全的深层反射提取
                // 走到这里的 DLL，必定是极其轻量、绝对安全的纯契约库！
                Type[] types;
                try
                {
                    // 尝试获取该 DLL 里的所有类
                    types = asm.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException ex)
                {
                    // 【核心修复 2：残缺提取黑魔法】
                    // 即使部分类由于缺少依赖报错，我们也能把加载成功的类提取出来继续扫！
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }
                catch
                {
                    // 遇到其他奇葩崩溃，直接跳过这个 DLL，保全大局
                    continue;
                }

                // 接下来就是安全的反射扫描了
                foreach (var type in types)
                {
                    var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    foreach (var field in fields)
                    {
                        if (field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
                        {
                            var attr = field.GetCustomAttributes(typeof(PermissionDefinitionAttribute), false).FirstOrDefault() as PermissionDefinitionAttribute;
                            if (attr != null)
                            {
                                string code = (string)field.GetRawConstantValue()!;
                                _permissionsManifest.Add(new PermissionNode(code, attr.Category, attr.Description));
                            }
                        }
                    }
                }
            }

            Logger.LogInformation("🔍 权限全景扫描完成！共在全网 DLL 中发现 {Count} 个原子权限点。", _permissionsManifest.Count);
        }

        private void LoadOrInitializeDatabases()
        {
            _roles.Clear(); // 强制清空
            _users.Clear(); // 强制清空

            // --- 角色库 ---
            if (File.Exists(_rolesPath))
            {
                try 
                {
                    _roles = JsonSerializer.Deserialize<Dictionary<string, RoleInfo>>(File.ReadAllText(_rolesPath)) ?? new(); 
                }
                catch 
                { 
                }
            }

            if (_roles.Count == 0)
            {
                // 初始化默认三大金刚角色
                _roles["Admin"] = new RoleInfo("Admin", _permissionsManifest.Select(p => p.Code).ToList()); // 超管默认拥有刚扫描出来的所有权限！
                _roles["Engineer"] = new RoleInfo("Engineer", new List<string>
                { 
                    SystemPermissions.RECIPE_VIEW, 
                    SystemPermissions.RECIPE_EDIT, 
                    SystemPermissions.SYSTEM_MANUAL_CONTROL 
                });

                _roles["Operator"] = new RoleInfo("Operator", new List<string> 
                {
                    SystemPermissions.RECIPE_VIEW,
                    SystemPermissions.ALARM_ACK 
                });

                File.WriteAllText(_rolesPath, JsonSerializer.Serialize(_roles, _jsonOpts));
            }

            // --- 用户库 ---
            if (File.Exists(_usersPath))
            {
                try 
                {
                    _users = JsonSerializer.Deserialize<Dictionary<string, UserEntity>>(File.ReadAllText(_usersPath)) ?? new(); 
                }
                catch 
                {
                }
            }

            if (_users.Count == 0)
            {
                // 初始化默认用户 (密码全是 123456)
                string defaultHash = ComputeSha256("123456");
                _users["admin"] = new UserEntity { 
                    Username = "admin",
                    PasswordHash = defaultHash, 
                    Role = "Admin", 
                    DisplayName = "超级管理员" 
                };

                _users["eng"] = new UserEntity 
                { 
                    Username = "eng",
                    PasswordHash = defaultHash, 
                    Role = "Engineer", 
                    DisplayName = "工艺工程师" 
                };

                _users["op"] = new UserEntity 
                { 
                    Username = "op",
                    PasswordHash = defaultHash, 
                    Role = "Operator",
                    DisplayName = "现场操作员" 
                };

                File.WriteAllText(_usersPath, JsonSerializer.Serialize(_users, _jsonOpts));
            }

            Logger.LogInformation("✅ 安全数据库加载完毕。");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🔐 安全大管家已激活，正在接管全网登录与鉴权验证...");

            // 1. 登录验证服务
            var loginSrv = CreateServer<LoginReq, LoginRes>("auth.login");
            _ = loginSrv.ServeAsync(req =>
            {
                Logger.LogInformation("尝试登录: {User}", req.Username);

                if (_users.TryGetValue(req.Username, out var user))
                {
                    string inputHash = ComputeSha256(req.Password);
                    if (user.PasswordHash == inputHash)
                    {
                        // 登录成功！查出这个角色对应的所有权限
                        var grantedPerms = _roles.TryGetValue(user.Role, out var roleInfo) ? roleInfo.GrantedPermissions : new List<string>();

                        string token = Guid.NewGuid().ToString("N"); // 极其简易的 Token
                        Logger.LogInformation("✅ 用户 [{User}] 登录成功！赋予 {Count} 个权限节点。", user.DisplayName, grantedPerms.Count);

                        return Task.FromResult(new LoginRes(true, "登录成功", token, new UserInfo(user.Username, user.Role, user.DisplayName), grantedPerms));
                    }
                }

                Logger.LogWarning("❌ 用户 [{User}] 登录失败，密码错误或账号不存在。", req.Username);
                return Task.FromResult(new LoginRes(false, "用户名或密码错误", "", null, null));
            }, stoppingToken);

            // 2. 索要权限全景清单 (供 Dashboard UI 分配权限时使用)
            var manifestSrv = CreateServer<GetManifestReq, GetManifestRes>("sec.get_manifest");
            _ = manifestSrv.ServeAsync(req => Task.FromResult(new GetManifestRes(_permissionsManifest)), stoppingToken);

            // 3. 用户管理接口
            var getUsersSrv = CreateServer<GetUsersReq, GetUsersRes>("sec.get_users");
            _ = getUsersSrv.ServeAsync(req => Task.FromResult(new GetUsersRes(_users.Values.Select(u => new UserInfo(u.Username, u.Role, u.DisplayName)).ToList())), stoppingToken);

            var saveUserSrv = CreateServer<SaveUserReq, SaveUserRes>("sec.save_user");
            _ = saveUserSrv.ServeAsync(req =>
            {
                if (_users.TryGetValue(req.Username, out var user))
                {
                    user.DisplayName = req.DisplayName;
                    user.Role = req.Role;
                    if (!string.IsNullOrEmpty(req.NewPassword)) user.PasswordHash = ComputeSha256(req.NewPassword);
                }
                else
                {
                    string pwd = string.IsNullOrEmpty(req.NewPassword) ? "123456" : req.NewPassword;
                    _users[req.Username] = new UserEntity { Username = req.Username, DisplayName = req.DisplayName, Role = req.Role, PasswordHash = ComputeSha256(pwd) };
                }
                SaveToDisk();
                Logger.LogInformation("🛠️ 用户信息已更新: {User}", req.Username);
                return Task.FromResult(new SaveUserRes(true, "用户保存成功"));
            }, stoppingToken);

            var delUserSrv = CreateServer<DeleteUserReq, DeleteUserRes>("sec.delete_user");
            _ = delUserSrv.ServeAsync(req =>
            {
                if (req.Username == "admin") return Task.FromResult(new DeleteUserRes(false, "超级管理员不可删除"));
                _users.Remove(req.Username);
                SaveToDisk();
                return Task.FromResult(new DeleteUserRes(true, "删除成功"));
            }, stoppingToken);

            // 4. 角色管理接口
            var getRolesSrv = CreateServer<GetRolesReq, GetRolesRes>("sec.get_roles");
            _ = getRolesSrv.ServeAsync(req => Task.FromResult(new GetRolesRes(_roles.Values.ToList())), stoppingToken);

            var saveRoleSrv = CreateServer<SaveRoleReq, SaveRoleRes>("sec.save_role");
            _ = saveRoleSrv.ServeAsync(req =>
            {
                _roles[req.Role.RoleName] = req.Role;
                SaveToDisk();
                Logger.LogInformation("🛠️ 角色权限已更新: {Role}", req.Role.RoleName);
                return Task.FromResult(new SaveRoleRes(true, "角色保存成功"));
            }, stoppingToken);

            var delRoleSrv = CreateServer<DeleteRoleReq, DeleteRoleRes>("sec.delete_role");
            _ = delRoleSrv.ServeAsync(req =>
            {
                if (req.RoleName == "Admin") return Task.FromResult(new DeleteRoleRes(false, "内置管理员角色不可删除"));
                _roles.Remove(req.RoleName);
                SaveToDisk();
                return Task.FromResult(new DeleteRoleRes(true, "角色删除成功"));
            }, stoppingToken);

            // 5.监听工程热切换
            var workspaceSub = CreateSubscriber<WorkspaceChangedEvent>("sys.workspace.changed");
            _ = Task.Run(async () =>
            {
                await foreach (var msg in workspaceSub.SubscribeAsync(stoppingToken))
                {
                    Logger.LogWarning("🔄 收到工程 [{Project}] 切换广播！正在热重载 IAM 用户与角色矩阵...", msg.NewProjectName);
                    // 重新扫盘获取权限字典，并重新加载用户配置
                    ScanPermissionsManifest();
                    LoadOrInitializeDatabases();
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        // ==========================================
        // 统一异步存盘方法
        // ==========================================
        private void SaveToDisk()
        {
            Task.Run(() =>
            {
                File.WriteAllText(_usersPath, JsonSerializer.Serialize(_users, _jsonOpts));
                File.WriteAllText(_rolesPath, JsonSerializer.Serialize(_roles, _jsonOpts));
            });
        }

        // ==========================================
        // 辅助算法：工业级不可逆密码哈希
        // ==========================================
        private static string ComputeSha256(string rawData)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
                return builder.ToString();
            }
        }
    }
}