using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace BatchPdfPublisher.Services
{
    /// <summary>Creates a small pull-down menu through AutoCAD's ActiveX menu API.</summary>
    public static class MenuService
    {
        private const string MenuName = "BPP_批量打印";
        private static object _menu;
        private static readonly List<object> _items = new List<object>();
        private static EventHandler _idle;

        public static void InstallWhenReady()
        {
            if (_idle != null) return;
            _idle = (s, e) =>
            {
                if (!HasMenu()) Install();
            };
            Application.Idle += _idle;
            Install();
        }

        public static void Install()
        {
            try
            {
                var acad = typeof(Application).GetProperty("AcadApplication", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                if (acad == null) return;
                var groups = acad.GetType().InvokeMember("MenuGroups", BindingFlags.GetProperty, null, acad, null);
                var group = groups.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, groups, new object[] { 0 });
                var menus = group.GetType().InvokeMember("Menus", BindingFlags.GetProperty, null, group, null);
                try { _menu = menus.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, menus, new object[] { MenuName }); } catch { _menu = null; }
                if (_menu != null) RemoveMenuFromBar(menus, _menu);
                _menu = menus.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, menus, new object[] { MenuName });
                AddItem("打开面板", "BPP"); AddItem("创建图框", "TKK"); AddItem("插入目录", "ML1");
                var bar = acad.GetType().InvokeMember("MenuBar", BindingFlags.GetProperty, null, acad, null);
                var count = (int)bar.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, bar, null);
                menus.GetType().InvokeMember("InsertMenuInMenuBar", BindingFlags.InvokeMethod, null, menus, new object[] { _menu, count + 1 });
            }
            catch (Exception exception) { Trace(exception); _menu = null; _items.Clear(); }
        }

        public static void Remove()
        {
            if (_idle != null) { Application.Idle -= _idle; _idle = null; }
            try
            {
                if (_menu == null) return;
                var menus = _menu.GetType().GetProperty("Parent")?.GetValue(_menu, null);
                RemoveMenuFromBar(menus, _menu);
                menus?.GetType().InvokeMember("Remove", BindingFlags.InvokeMethod, null, menus, new[] { _menu });
            }
            catch { }
            finally { _menu = null; _items.Clear(); }
        }

        private static void AddItem(string label, string command)
        {
            var count = (int)_menu.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, _menu, null);
            var item = _menu.GetType().InvokeMember("AddMenuItem", BindingFlags.InvokeMethod, null, _menu, new object[] { count + 1, label, "BPP_" + command, "^C^C_" + command + " " });
            _items.Add(item);
        }

        private static bool HasMenu()
        {
            try
            {
                var acad = typeof(Application).GetProperty("AcadApplication", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                if (acad == null) return false;
                var bar = acad.GetType().InvokeMember("MenuBar", BindingFlags.GetProperty, null, acad, null);
                var count = (int)bar.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, bar, null);
                for (var i = 0; i < count; i++)
                {
                    var item = bar.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, bar, new object[] { i });
                    var name = item?.GetType().GetProperty("Name")?.GetValue(item, null) as string;
                    if (string.Equals(name, MenuName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static void RemoveMenuFromBar(object menus, object menu)
        {
            try { menus?.GetType().InvokeMember("RemoveMenuFromMenuBar", BindingFlags.InvokeMethod, null, menus, new[] { menu }); } catch { }
        }

        private static void Trace(Exception exception)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatchPdfPublisher.ui.log"), DateTime.Now.ToString("O") + " Menu: " + exception + Environment.NewLine); } catch { }
        }
    }
}
