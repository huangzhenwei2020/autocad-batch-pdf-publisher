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
        private static bool _mnuRequested;

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
                _menu = FindMenu(menus);
                if (_menu == null)
                    _menu = menus.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, menus, new object[] { MenuName });
                RemoveMenuFromBar(menus, _menu);
                var menuCount = (int)_menu.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, _menu, null);
                if (menuCount == 0)
                {
                    AddItem("打开面板", "BPP"); AddItem("创建图框", "TKK"); AddItem("插入目录", "ML1");
                }
                var bar = acad.GetType().InvokeMember("MenuBar", BindingFlags.GetProperty, null, acad, null);
                var count = (int)bar.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, bar, null);
                menus.GetType().InvokeMember("InsertMenuInMenuBar", BindingFlags.InvokeMethod, null, menus, new object[] { _menu, count + 1 });
            }
            catch (Exception exception) { Trace(exception); _menu = null; _items.Clear(); }
            LoadPartialMenu();
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

        private static void LoadPartialMenu()
        {
            if (_mnuRequested) return;
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BPP_批量打印.mnu");
                var content = "***MENUGROUP=BPP\r\n***POP16\r\nBPP_批量打印\r\n[打开面板（BPP）]^C^C_BPP \r\n[创建图框（TKK）]^C^C_TKK \r\n[插入目录（ML1）]^C^C_ML1 \r\n";
                // MNU is an ANSI file; AutoCAD on Chinese installations expects the
                // system GBK code page rather than UTF-8/default .NET encoding.
                System.IO.File.WriteAllText(path, content, System.Text.Encoding.GetEncoding(936));
                var document = Application.DocumentManager.MdiActiveDocument;
                if (document == null) return;
                var escaped = path.Replace("\\", "/").Replace("\"", "\\\"");
                // -MENULOAD is the command-line variant.  Supplying the menu group
                // explicitly is important: AutoCAD otherwise leaves the command at
                // the "menu group name" prompt and the following menucmd expression
                // is never executed (which is why the Ribbon appeared but the classic
                // menu did not).
                var macro = "_.-MENULOAD\n\"" + escaped + "\"\nBPP\n(menucmd \"P16=+BPP.POP16\")\n";
                document.SendStringToExecute(macro, true, false, false);
                _mnuRequested = true;
            }
            catch (Exception exception) { Trace(exception); }
        }

        private static void AddItem(string label, string command)
        {
            var count = (int)_menu.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, _menu, null);
            // AutoCAD's ActiveX AddMenuItem signature is (Index, Label, Macro).
            // The previous implementation supplied an extra identifier argument,
            // causing TargetParameterCountException before insertion into MenuBar.
            var item = _menu.GetType().InvokeMember("AddMenuItem", BindingFlags.InvokeMethod, null, _menu, new object[] { count + 1, label, "^C^C_" + command + " " });
            _items.Add(item);
        }

        private static object FindMenu(object menus)
        {
            try
            {
                var count = (int)menus.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, menus, null);
                for (var i = 0; i < count; i++)
                {
                    object item;
                    try { item = menus.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, menus, new object[] { i }); }
                    catch { item = menus.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, menus, new object[] { i }); }
                    var name = item?.GetType().GetProperty("Name")?.GetValue(item, null) as string;
                    if (string.Equals(name, MenuName, StringComparison.OrdinalIgnoreCase)) return item;
                }
            }
            catch { }
            return null;
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
