using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CadArchSpec.Installer
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
        }
    }

    internal sealed class InstallerForm : Form
    {
        private const string BundleName = "CadArchSpecEditor.bundle";
        private readonly Label _status;
        private readonly TextBox _detectedProducts;
        private readonly Button _installButton;
        private readonly Button _uninstallButton;

        public InstallerForm()
        {
            Text = "建筑设计说明助手安装程序 v0.3";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(650, 405);
            MinimumSize = new Size(560, 400);
            AutoScroll = true;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;
            MaximizeBox = false;

            var title = new Label
            {
                Text = "建筑设计说明助手",
                Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 61, 76),
                AutoSize = true,
                Location = new Point(28, 24)
            };
            var subtitle = new Label
            {
                Text = "AutoCAD 建筑专业设计说明编制与审图前自检工具",
                ForeColor = Color.FromArgb(103, 116, 130),
                AutoSize = true,
                Location = new Point(31, 63)
            };
            var versionSupport = new Label
            {
                Text = "本测试安装包支持：AutoCAD 2022、AutoCAD 2026",
                ForeColor = Color.FromArgb(55, 83, 96),
                AutoSize = true,
                Location = new Point(31, 94)
            };
            var detectedLabel = new Label
            {
                Text = "本机检测结果",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(31, 127)
            };
            _detectedProducts = new TextBox
            {
                ReadOnly = true,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(248, 249, 250),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(31, 151),
                Size = new Size(586, 82)
            };
            var note = new Label
            {
                Text = "安装后重新启动 CAD，插件会自动加载。输入 JZSM 打开“建筑设计说明助手”。\r\n" +
                       "安装不会修改 DWG，也不会删除已保存的 .jzsmproj 项目文件。",
                ForeColor = Color.FromArgb(83, 95, 108),
                Location = new Point(31, 248),
                Size = new Size(586, 46)
            };
            _status = new Label
            {
                Text = GetInstallStatus(),
                ForeColor = Color.FromArgb(45, 105, 77),
                Location = new Point(31, 305),
                Size = new Size(586, 24)
            };
            _installButton = CreateButton("安装 / 更新", 313, true);
            _installButton.Click += (_, __) => Install();
            _uninstallButton = CreateButton("卸载", 421, false);
            _uninstallButton.Click += (_, __) => Uninstall();
            var closeButton = CreateButton("关闭", 529, false);
            closeButton.Click += (_, __) => Close();

            Controls.AddRange(new Control[]
            {
                title, subtitle, versionSupport, detectedLabel, _detectedProducts,
                note, _status, _installButton, _uninstallButton, closeButton
            });

            Shown += (_, __) => RefreshState();
        }

        private Button CreateButton(string text, int left, bool primary)
        {
            return new Button
            {
                Text = text,
                Location = new Point(left, 344),
                Size = new Size(96, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(45, 101, 120) : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(48, 64, 78),
                UseVisualStyleBackColor = false
            };
        }

        private void RefreshState()
        {
            var products = DetectProducts();
            _detectedProducts.Text = products.Count == 0
                ? "没有检测到受支持的 AutoCAD 2022 或 AutoCAD 2026。"
                : string.Join(Environment.NewLine, products.Select(item => "✓ " + item));
            _status.Text = GetInstallStatus();
            _uninstallButton.Enabled = Directory.Exists(GetBundlePath());
        }

        private void Install()
        {
            try
            {
                _installButton.Enabled = false;
                _status.Text = "正在安装，请稍候…";
                Application.DoEvents();

                var target = GetBundlePath();
                var parent = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(parent))
                {
                    throw new InvalidOperationException("无法确定 Autodesk 插件目录。");
                }
                Directory.CreateDirectory(parent);

                var staging = Path.Combine(
                    Path.GetTempPath(),
                    "CadArchSpecEditor.Install." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    ExtractEmbeddedBundle(staging);
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, true);
                    }
                    Directory.Move(staging, target);
                    staging = string.Empty;
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(staging) && Directory.Exists(staging))
                    {
                        Directory.Delete(staging, true);
                    }
                }

                _status.Text = "安装完成。请重新启动 CAD，然后输入 JZSM。";
                MessageBox.Show(
                    this,
                    "建筑设计说明助手安装完成。\r\n\r\n请重新启动 AutoCAD，然后输入命令 JZSM。",
                    "安装完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                _status.Text = "安装失败：" + exception.Message;
                MessageBox.Show(
                    this,
                    "安装失败：\r\n" + exception.Message +
                    "\r\n\r\n如果 CAD 正在使用旧版插件，请先关闭对应 CAD 后重试。",
                    "建筑设计说明助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                _installButton.Enabled = true;
                RefreshState();
            }
        }

        private void Uninstall()
        {
            if (MessageBox.Show(
                    this,
                    "确定卸载建筑设计说明助手吗？\r\n已保存的项目文件不会被删除。",
                    "确认卸载",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                var target = GetBundlePath();
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }
                _status.Text = "插件已卸载，项目文件仍然保留。";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "卸载失败：\r\n" + exception.Message + "\r\n\r\n请关闭 CAD 后重试。",
                    "建筑设计说明助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            RefreshState();
        }

        private static void ExtractEmbeddedBundle(string destination)
        {
            using (var stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("CadArchSpecEditor.bundle.zip"))
            {
                if (stream == null)
                {
                    throw new InvalidDataException("安装程序中缺少插件负载。");
                }
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        var output = Path.GetFullPath(Path.Combine(destination, relative));
                        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
                        if (!output.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("安装包中存在非法路径。");
                        }
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(output);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        entry.ExtractToFile(output, true);
                    }
                }
            }
        }

        private static List<string> DetectProducts()
        {
            var results = new List<string>();
            DetectProduct("AutoCAD 2022", "R24.1", results);
            DetectProduct("AutoCAD 2026", "R25.1", results);
            return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void DetectProduct(string displayName, string release, ICollection<string> results)
        {
            var registryRoots = new[]
            {
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD\" + release),
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Autodesk\AutoCAD\" + release)
            };
            foreach (var root in registryRoots)
            {
                using (root)
                {
                    if (root == null) continue;
                    foreach (var childName in root.GetSubKeyNames())
                    {
                        using (var child = root.OpenSubKey(childName))
                        {
                            var location = Convert.ToString(child?.GetValue("AcadLocation"));
                            if (!string.IsNullOrWhiteSpace(location) &&
                                File.Exists(Path.Combine(location, "acad.exe")))
                            {
                                AddDetectedProduct(displayName, location, results);
                            }
                        }
                    }
                }
            }

            var commonLocations = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", displayName),
                Path.Combine(@"D:\Program Files\Autodesk", displayName)
            };
            foreach (var location in commonLocations.Where(path => File.Exists(Path.Combine(path, "acad.exe"))))
            {
                AddDetectedProduct(displayName, location, results);
            }
        }

        private static void AddDetectedProduct(
            string displayName,
            string location,
            ICollection<string> results)
        {
            var normalized = Path.GetFullPath(location).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var value = displayName + "  ·  " + normalized;
            if (!results.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(value);
            }
        }

        private static string GetBundlePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk",
                "ApplicationPlugins",
                BundleName);
        }

        private static string GetInstallStatus()
        {
            return Directory.Exists(GetBundlePath())
                ? "当前状态：已安装，可点击“安装 / 更新”覆盖为本版本。"
                : "当前状态：尚未安装。";
        }
    }
}
