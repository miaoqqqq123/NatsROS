using MessagePack;
using NatsROS.Core;
using System.Collections.Generic;

namespace NatsROS.Messages.RMS;

// 配方生命周期状态
public enum RecipeState : byte
{
    Draft = 0,      // 草稿 (可随便改)
    Approved = 1,   // 已批准 (锁定！严禁修改，只能下发生产或另存新版本)
    Obsolete = 2    // 已废弃 (不再使用)
}

// ==========================================
// 1. ISA-88 标准控制配方模型 (Control Recipe)
// ==========================================
[MessagePackObject]
public record RecipeModel(
    [property: Key(0)] string RecipeId,           // 全局唯一ID (如 RECIPE_APPLE_001)
    [property: Key(1)] string RecipeName,         // 产品名称 (如 "苹果 iPhone 15 散热板")
    [property: Key(2)] string Version,            // 版本号 (如 "V1.0")
    [property: Key(3)] RecipeState State,         // 当前状态
    [property: Key(5)] Dictionary<string, string> Formula, // 工艺参数配方表 (速度、坐标文件、安全高度等)
    [property: Key(6)] string LastModifiedBy,     // 最后修改人
    [property: Key(7)] long LastModifiedTime      // 最后修改时间戳
) : IRosMessage;

// ==========================================
// 2. 审计追踪记录模型 (Audit Trail Record)
// ==========================================
[MessagePackObject]
public record AuditLogRecord(
    [property: Key(0)] long Timestamp,
    [property: Key(1)] string Operator,           // 操作人 (如 "工艺员_张三")
    [property: Key(2)] string RecipeId,           // 关联配方
    [property: Key(3)] string Action,             // 操作类型 ("Create", "Update", "Approve")
    [property: Key(4)] string Details             // 详细变更 (如 "将 Velocity 从 30 改为 50")
) : IRosMessage;

// ==========================================
// 3. RMS 交互 RPC 接口
// ==========================================
[MessagePackObject]
public record GetRecipesReq() : IRosRequest<GetRecipesRes>;

[MessagePackObject]
public record GetRecipesRes(
    [property: Key(0)] List<RecipeModel> Recipes
) : IRosMessage;

[MessagePackObject]
public record SaveRecipeReq(
    [property: Key(0)] RecipeModel Recipe,
    [property: Key(1)] string OperatorName,
    [property: Key(2)] string ChangeReason
) : IRosRequest<SaveRecipeRes>;

[MessagePackObject]
public record SaveRecipeRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
) : IRosMessage;

[MessagePackObject]
public record ChangeRecipeStateReq(
    [property: Key(0)] string RecipeId,
    [property: Key(1)] RecipeState TargetState,
    [property: Key(2)] string OperatorName
) : IRosRequest<ChangeRecipeStateRes>;

[MessagePackObject]
public record ChangeRecipeStateRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
) : IRosMessage;

[MessagePackObject]
public record GetAuditLogsReq(
    [property: Key(0)] string RecipeId
) : IRosRequest<GetAuditLogsRes>;

[MessagePackObject]
public record GetAuditLogsRes(
    [property: Key(0)] List<AuditLogRecord> Logs
) : IRosMessage;


// ==========================================
// 4. 当前激活配方 (Active Recipe) 相关的 RPC 与广播
// ==========================================
[MessagePackObject]
public record ActivateRecipeReq(
    [property: Key(0)] string RecipeId
) : IRosRequest<ActivateRecipeRes>;

[MessagePackObject]
public record ActivateRecipeRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message
) : IRosMessage;

[MessagePackObject]
public record GetActiveRecipeReq() : IRosRequest<GetActiveRecipeRes>;

[MessagePackObject]
public record GetActiveRecipeRes(
    [property: Key(0)] RecipeModel? ActiveRecipe
) : IRosMessage;

// 当配方被激活时，向全网广播！
[MessagePackObject]
public record RecipeActivatedEvent(
    [property: Key(0)] RecipeModel Recipe,
    [property: Key(1)] long TimestampTick
) : IRosMessage;


// 当配方被修改、新建或改变状态时，向全网广播！
[MessagePackObject]
public record RecipeUpdatedEvent(
    [property: Key(0)] string RecipeId,
    [property: Key(1)] long TimestampTick
) : IRosMessage;