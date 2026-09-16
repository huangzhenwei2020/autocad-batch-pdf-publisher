using System;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 一条用户可见功能的定义。刻意做成零依赖的纯数据类：功能区、经典菜单和快捷键
    /// 三处都只读它，离线测试也能直接构造，不必挂上 AutoCAD。
    /// </summary>
    public sealed class FeatureDefinition
    {
        public FeatureDefinition(string id, string name, string command, string defaultShortcut, string group, string icon, string description, string nativeCommand)
            : this(id, name, command, defaultShortcut, group, icon, description, nativeCommand, null, null)
        {
        }

        public FeatureDefinition(string id, string name, string command, string defaultShortcut, string group, string icon, string description, string nativeCommand, string lispInvocation)
            : this(id, name, command, defaultShortcut, group, icon, description, nativeCommand, lispInvocation, null)
        {
        }

        public FeatureDefinition(string id, string name, string command, string defaultShortcut, string group, string icon, string description, string nativeCommand, string lispInvocation, string shortName)
        {
            Id = id; Name = name; Command = command; DefaultShortcut = defaultShortcut;
            Group = group; Icon = icon; Description = description;
            NativeCommand = nativeCommand;
            LispInvocation = lispInvocation;
            ShortName = shortName;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        /// <summary>
        /// 功能区按钮上的四字简称。功能区是网格排版，名字长短不一就不齐整，
        /// 所以按钮统一用四字；菜单栏、悬停提示和快捷键设置页仍用完整名称
        /// <see cref="Name"/>，信息不丢。
        /// </summary>
        public string ShortName { get; private set; }
        public string Command { get; private set; }
        public string DefaultShortcut { get; private set; }
        public string Group { get; private set; }
        public string Icon { get; private set; }
        public string Description { get; private set; }
        /// <summary>外置组件真正注册的命令。快捷键与它相同时直接使用，不生成 AutoLISP 包装，避免递归。</summary>
        public string NativeCommand { get; private set; }
        /// <summary>
        /// 该快捷键对应的 AutoLISP 表达式。非空时用它替代默认的 (command "内部命令")，
        /// 让同一批命令可以带参数（图层直达命令即用此机制传递目标图层）。
        /// </summary>
        public string LispInvocation { get; private set; }
    }
}
