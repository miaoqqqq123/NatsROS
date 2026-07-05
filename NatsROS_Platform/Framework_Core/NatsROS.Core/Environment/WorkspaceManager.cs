using System;
using System.IO;

namespace NatsROS.Core.Environment
{
    /// <summary>
    /// 全局工程路径管理器 (对标高级工业软件的沙盒隔离设计)
    /// </summary>
    public static class WorkspaceManager
    {
        // 获取系统的运行根目录 (比如 D:\NatsROS_Platform\)
        // 这里需要往上退一级，因为当前运行的 EXE 是在 Bin 目录里的！
        private static string GetSystemRoot()
        {
            string binPath = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(binPath, ".."));
        }

        // ==========================================
        // 1. 本机专属数据区 (绝对不随工程切换而改变)
        // 存放：FDA 审计数据库、黑匣子快照、UI 布局习惯
        // ==========================================
        public static string LocalDataPath
        {
            get
            {
                string path = Path.Combine(GetSystemRoot(), "LocalData");
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }

        // 快捷获取：本机数据库文件
        public static string GetLocalDatabasePath(string dbFileName) => Path.Combine(LocalDataPath, dbFileName);

        // 快捷获取：崩溃快照目录
        public static string GetCrashDumpsPath()
        {
            string path = Path.Combine(LocalDataPath, "CrashDumps");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        // ==========================================
        // 2. 当前工程工作区 (随 .natsros 解压而整体热替换)
        // 存放：配方库、报警字典、行为树 XML、权限配置
        // ==========================================
        public static string CurrentWorkspacePath
        {
            get
            {
                string path = Path.Combine(GetSystemRoot(), "CurrentWorkspace");
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }

        // 快捷获取：系统配置类文件 (alarms.json, users.json 等)
        public static string GetConfigPath(string fileName)
        {
            string configDir = Path.Combine(CurrentWorkspacePath, "Config");
            if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
            return Path.Combine(configDir, fileName);
        }

        // 快捷获取：配方库文件 (rms_recipes.json)
        public static string GetRecipeDbPath()
        {
            string recipeDir = Path.Combine(CurrentWorkspacePath, "Recipes");
            if (!Directory.Exists(recipeDir)) Directory.CreateDirectory(recipeDir);
            return Path.Combine(recipeDir, "rms_recipes.json");
        }

        // 快捷获取：行为树根目录 (供子树解析用)
        public static string GetBehaviorTreesPath()
        {
            string btDir = Path.Combine(CurrentWorkspacePath, "BehaviorTrees");
            if (!Directory.Exists(btDir)) Directory.CreateDirectory(btDir);
            return btDir;
        }

        // ==========================================
        // 3. 生产追溯记录区 (海量本地数据湖)
        // ==========================================
        public static string GetProductionDataPath(string projectName)
        {
            string path = Path.Combine(GetSystemRoot(), "ProductionData", projectName, DateTime.Now.ToString("yyyyMMdd"));
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }
    }
}