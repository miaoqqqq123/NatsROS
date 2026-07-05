using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Hexiv.BehaviorTree.Core;

namespace Hexiv.BehaviorTree.Builders
{
    /// <summary>
    /// 行为树 XML 动态解析与属性注入工厂 (V3.0 终极版)
    /// 完全对标企业级对象树反序列化，彻底告别硬编码。
    /// </summary>
    public class BehaviorTreeFactory
    {
        // 类型注册表：缓存所有可用的行为树节点类型
        private readonly Dictionary<string, Type> _nodeTypes = new();

        public BehaviorTreeFactory()
        {
            // 在工厂出生时，进行一次全局扫盘，把所有继承了 BehaviorTreeNode 的非抽象类全部找出来
            ScanAvailableNodes();
        }

        private void ScanAvailableNodes()
        {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .Where(t => typeof(BehaviorTreeNode).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var t in types)
            {
                // 同时注册 "全名" 和 "短名"，方便 XML 编写
                if (!string.IsNullOrEmpty(t.FullName)) _nodeTypes[t.FullName] = t;
                _nodeTypes[t.Name] = t;
            }
        }

        /// <summary>
        /// 解析 XML 字符串并生成整棵树
        /// </summary>
        /// <param name="xmlText"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public BehaviorTreeNode CreateTreeFromXml(string xmlText)
        {
            var doc = XDocument.Parse(xmlText);

            // 【核心修复】：寻找 BehaviorTree 标签，无视外层可能包裹的 root
            var btElement = doc.Descendants("BehaviorTree").FirstOrDefault() ?? doc.Root;

            if (btElement == null) throw new Exception("XML 格式不合法，缺少行为树根节点");

            // 寻找顶层的 RootNode
            var firstNode = btElement.Elements("Node").FirstOrDefault();
            if (firstNode == null) throw new Exception("树中没有任何有效的 Node 节点");

            return ParseNode(firstNode);
        }

        // 递归解析与动态实例化
        private BehaviorTreeNode ParseNode(XElement element)
        {
            // 提取类名、ID和显示名字
            string typeName = element.Attribute("type")?.Value ?? throw new Exception("节点缺少 type 属性");
            string id = element.Attribute("Id")?.Value ?? Guid.NewGuid().ToString();
            string name = element.Element("Name")?.Value ?? typeName.Split('.').Last();

            if (!_nodeTypes.TryGetValue(typeName, out var csharpType))
                throw new Exception($"未在内存中找到节点类型: {typeName}。请确保依赖的 DLL 已放置在执行目录下！");

            // 1. 瞬间实例化 C# 对象
            var nodeInstance = (BehaviorTreeNode)Activator.CreateInstance(csharpType, name)!;

            // 2. 强行覆写还原它的 GUID 灵魂！(保证 UI 连线的引用准确性)
            nodeInstance.Id = id;

            // 3. 动态注入业务属性 (过滤掉内置的 Name 和 Id)
            var props = csharpType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite);
            foreach (var prop in props)
            {
                if (prop.Name == "Name" || prop.Name == "Id") continue;

                var propElement = element.Element(prop.Name);
                if (propElement != null)
                {
                    try
                    {
                        // 智能类型转换 (如把 XML 里的 "1.5" 转成 double)
                        var safeValue = Convert.ChangeType(propElement.Value, prop.PropertyType);
                        prop.SetValue(nodeInstance, safeValue);
                    }
                    catch { /* 忽略转换失败的属性，避免中断整棵树的加载 */ }
                }
            }

            // 4. 递归处理 Children
            var childrenElement = element.Element("Children");
            if (childrenElement != null)
            {
                foreach (var childXml in childrenElement.Elements("Node"))
                {
                    var childNode = ParseNode(childXml);

                    if (nodeInstance is ControlNode controlNode)
                        controlNode.AddChild(childNode);
                    else if (nodeInstance is RetryNode retryNode)
                        retryNode.SetChild(childNode);
                }
            }

            return nodeInstance;
        }

        // ==========================================
        // 万能属性注入器 (Property Injector)
        // ==========================================
        private void InjectProperties(BehaviorTreeNode node, XElement element)
        {
            var type = node.GetType();

            // 遍历 XML 里的所有属性 (Attributes)
            foreach (var attr in element.Attributes())
            {
                // 跳过引擎的内置保留字
                if (attr.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                    attr.Name.LocalName.Equals("ID", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 在 C# 类中寻找同名的属性 (忽略大小写)
                var prop = type.GetProperty(attr.Name.LocalName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

                if (prop != null && prop.CanWrite)
                {
                    try
                    {
                        // 智能类型转换 (比如把 XML里的 "10.5" 字符串转成 C# 里的 double)
                        var safeValue = Convert.ChangeType(attr.Value, prop.PropertyType);
                        prop.SetValue(node, safeValue);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"节点 [{node.Name}] 的属性[{attr.Name.LocalName}] 赋值失败！无法将 '{attr.Value}' 转换为 {prop.PropertyType.Name}。原因: {ex.Message}");
                    }
                }
            }
        }
    }
}