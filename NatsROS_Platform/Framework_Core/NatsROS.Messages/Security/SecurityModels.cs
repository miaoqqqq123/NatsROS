using MessagePack;
using NatsROS.Core;
using System;
using System.Collections.Generic;

// 告诉大管家，这个 DLL 里藏了系统权限的定义！
[assembly: NatsROS.Core.Attributes.ContainsNatsRosPermissions]

namespace NatsROS.Messages.Security;

// ==========================================
// 1. 权限自动发现的底层特性标签 (黑魔法核心)
// ==========================================
[AttributeUsage(AttributeTargets.Field)]
public class PermissionDefinitionAttribute : Attribute
{
    public string Category { get; set; } = "未分类";
    public string Description { get; set; } = "";
}

// ==========================================
// 2. 系统的核心内置权限池 (新节点可以在自己的DLL里仿照这个类来写)
// ==========================================
public static class SystemPermissions
{
    [PermissionDefinition(Category = "配方管理 (RMS)", Description = "允许查看配方列表与详情")]
    public const string RECIPE_VIEW = "Recipe.View";

    [PermissionDefinition(Category = "配方管理 (RMS)", Description = "允许新建、修改草稿配方")]
    public const string RECIPE_EDIT = "Recipe.Edit";

    [PermissionDefinition(Category = "配方管理 (RMS)", Description = "允许审核、批准、作废配方 (极高危)")]
    public const string RECIPE_APPROVE = "Recipe.Approve";

    [PermissionDefinition(Category = "报警管理 (AEM)", Description = "允许确认(Ack)并复位系统报警")]
    public const string ALARM_ACK = "Alarm.Ack";

    [PermissionDefinition(Category = "机器控制 (Control)", Description = "允许在3D孪生界面手动发送轴插补与点胶指令")]
    public const string SYSTEM_MANUAL_CONTROL = "System.ManualControl";

    [PermissionDefinition(Category = "系统安全 (Security)", Description = "允许管理用户和角色权限分配 (超管)")]
    public const string SECURITY_MANAGE = "Security.Manage";
}

// ==========================================
// 3. 数据实体模型 (DTO)
// ==========================================
[MessagePackObject]
public record PermissionNode(
    [property: Key(0)] string Code,
    [property: Key(1)] string Category,
    [property: Key(2)] string Description
);

[MessagePackObject]
public record UserInfo(
    [property: Key(0)] string Username,
    [property: Key(1)] string Role,
    [property: Key(2)] string DisplayName
);

[MessagePackObject]
public record RoleInfo(
    [property: Key(0)] string RoleName,
    [property: Key(1)] List<string> GrantedPermissions
);

// ==========================================
// 4. RPC 交互契约
// ==========================================

// A. 登录校验：返回 Token、用户信息、以及属于该角色的所有原子权限！
[MessagePackObject]
public record LoginReq(
    [property: Key(0)] string Username, 
    [property: Key(1)] string Password
) : IRosRequest<LoginRes>;

[MessagePackObject]
public record LoginRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message,
    [property: Key(2)] string Token,
    [property: Key(3)] UserInfo? User,
    [property: Key(4)] List<string>? GrantedPermissions
) : IRosMessage;

// B. 获取全网所有权限全景清单 (大屏分配权限时用)
[MessagePackObject]
public record GetManifestReq() : IRosRequest<GetManifestRes>;

[MessagePackObject]
public record GetManifestRes(
    [property: Key(0)] List<PermissionNode> Manifest
) : IRosMessage;

// ==========================================
// 5. 用户与角色管理 RPC 契约 (IAM Admin API)
// ==========================================

[MessagePackObject]
public record GetUsersReq() : IRosRequest<GetUsersRes>;

[MessagePackObject]
public record GetUsersRes(
    [property: Key(0)] List<UserInfo> Users
) : IRosMessage;

[MessagePackObject]
public record SaveUserReq(
    [property: Key(0)] string Username,
    [property: Key(1)] string Role,
    [property: Key(2)] string DisplayName,
    [property: Key(3)] string NewPassword // 如果为空，表示不修改密码
) : IRosRequest<SaveUserRes>;

[MessagePackObject]
public record SaveUserRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
) : IRosMessage;

[MessagePackObject]
public record DeleteUserReq(
    [property: Key(0)] string Username
) : IRosRequest<DeleteUserRes>;

[MessagePackObject]
public record DeleteUserRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
) : IRosMessage;

[MessagePackObject]
public record GetRolesReq() : IRosRequest<GetRolesRes>;

[MessagePackObject]
public record GetRolesRes(
    [property: Key(0)] List<RoleInfo> Roles
) : IRosMessage;

[MessagePackObject]
public record SaveRoleReq(
    [property: Key(0)] RoleInfo Role
) : IRosRequest<SaveRoleRes>;

[MessagePackObject]
public record SaveRoleRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
) : IRosMessage;

[MessagePackObject]
public record DeleteRoleReq(
    [property: Key(0)] string RoleName
) : IRosRequest<DeleteRoleRes>;

[MessagePackObject]
public record DeleteRoleRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
) : IRosMessage;