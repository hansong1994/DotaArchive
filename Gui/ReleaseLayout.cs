using System;
using System.Drawing;
using System.Windows.Forms;

namespace DotaArchive
{
    public sealed partial class PrototypeForm
    {
        private readonly Label queryHint = new Label();
#if !DOTA_ARCHIVE_RELEASE
        private static object[] DescribeLayout(Control parent)
        {
            var result = new System.Collections.Generic.List<object>();
            foreach(Control child in parent.Controls) result.Add(new { Type = child.GetType().Name, Label = child is TextBox ? "Text field" : child is Label || child is Button ? child.Text : "", Bounds = child.Bounds.ToString(), Client = child.ClientRectangle.ToString(), Margin = child.Margin.ToString(), Padding = child.Padding.ToString(), Font = child.Font.SizeInPoints, Children = DescribeLayout(child) });
            return result.ToArray();
        }
#endif

        private void BuildLayout()
        {
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 4, BackColor = ArchivePalette.Background };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            Controls.Add(shell);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            var brand = new SurfacePanel { BackColor = ArchivePalette.Header, BorderColor = ArchivePalette.Header, CornerRadius = 14, Padding = Padding.Empty, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 12, 18) };
            brand.Controls.Add(new Label { Text = "DA", Font = new Font("Segoe UI", 17F, FontStyle.Bold), ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
            header.Controls.Add(brand, 0, 0);
            var title = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 12) };
            title.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); title.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            title.Controls.Add(new Label { Text = "Dota Archive", Font = new Font("Segoe UI", 22F, FontStyle.Bold), ForeColor = ArchivePalette.Ink, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            title.Controls.Add(new Label { Text = "Dota 2 历史客户端  /  1.0.8", ForeColor = ArchivePalette.Muted, Font = new Font(Font.FontFamily, 9.5F), Dock = DockStyle.Fill }, 0, 1);
            header.Controls.Add(title, 1, 0);
            ButtonStyle(resume, "恢复已有下载"); resume.Margin = new Padding(6, 12, 6, 20); header.Controls.Add(resume, 2, 0);
            ButtonStyle(updateCatalog, "更新版本资料"); updateCatalog.Margin = new Padding(6, 12, 0, 20);
            updateCatalog.AccessibleName = "更新版本资料";
            updateCatalog.AccessibleDescription = "登录 SteamDB 后增量同步最新公开更新记录，只更新本地资料，不下载游戏。";
            header.Controls.Add(updateCatalog, 3, 0);
            shell.Controls.Add(header, 0, 0);

            var catalog = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 4), Margin = new Padding(0, 0, 0, 10), CornerRadius = 8, BackColor = Color.FromArgb(231, 238, 245), BorderColor = Color.FromArgb(231, 238, 245) };
            catalogStatus.Dock = DockStyle.Fill; catalogStatus.TextAlign = ContentAlignment.MiddleLeft; catalogStatus.AutoEllipsis = true;
            catalogStatus.Font = new Font(Font.FontFamily, 9.5F); catalogStatus.ForeColor = Color.FromArgb(57, 78, 101);
            catalogStatus.Text = "本地历史库已就绪  ·  查询优先使用本地资料，联网更新由你决定。";
            catalogStatus.AccessibleName = "版本资料同步状态"; catalog.Controls.Add(catalogStatus); shell.Controls.Add(catalog, 0, 1);

            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330)); content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var sidebar = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 0, 18, 0), BackColor = ArchivePalette.Background };
            var sidebarContent = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            sidebarContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebarContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 282)); sidebarContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 268));
            sidebar.Controls.Add(sidebarContent);
            sidebarContent.Controls.Add(BuildSearchCard(), 0, 0);
            sidebarContent.Controls.Add(BuildStorageCard(), 0, 1);
            content.Controls.Add(sidebar, 0, 0);

            var workspace = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 14), Margin = Padding.Empty };
            var workLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            workLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); workLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            inspector.Dock = DockStyle.Fill; inspector.Font = new Font(Font.FontFamily, 10F); inspector.Margin = Padding.Empty;
            inspector.TabPages.AddRange(new[] { planTab, browserTab, qrTab, logTab });
            foreach(TabPage tab in inspector.TabPages) { tab.BackColor = Color.White; tab.Padding = new Padding(16); }
            ReadOnlyBox(planText);
            planText.Font = new Font(Font.FontFamily, 11F);
            planText.Text = "找回一个过去的版本\r\n\r\n选择北京时间或补丁号，即可还原对应版本的完整内容包。\r\n\r\n时间节点\r\n选择“此前最近版本”或“此后最近版本”，不包含与输入时刻相等的记录。\r\n\r\n补丁号 · 全程本地查询\r\n以内置补丁发布日期为基准，选择“此前最近版本”或“此后最近版本”，严格排除与基准相等的记录。全程在本地匹配，不会自动打开 SteamDB。日期依据和精度会显示在结果中；日期边界不等于精确发布时间，结果可能属于相邻补丁。\r\n\r\n先查看结果\r\n点击“仅查找”核对版本，再决定是否下载。\r\n下载游戏时仍需 Steam 授权。";
            planTab.Controls.Add(planText);
            ReadOnlyBox(log); log.Font = new Font(Font.FontFamily, 10F); logTab.Controls.Add(log);
            BuildBrowserWorkspace(); BuildSteamWorkspace();
            workLayout.Controls.Add(inspector, 0, 0);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 10, 0, 0) };
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for(int i = 0; i < 4; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            ButtonStyle(download, "下载 / 继续", true); ButtonStyle(verify, "完整文件校验"); ButtonStyle(launch, "启动本地测试"); ButtonStyle(open, "打开版本目录");
            actions.Controls.Add(download, 0, 0); actions.Controls.Add(verify, 1, 0); actions.Controls.Add(launch, 2, 0); actions.Controls.Add(open, 3, 0);
            workLayout.Controls.Add(actions, 0, 1); workspace.Controls.Add(workLayout); content.Controls.Add(workspace, 1, 0); shell.Controls.Add(content, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0, 14, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 5));
            status.Text = "准备就绪"; status.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold); status.ForeColor = ArchivePalette.Ink; status.Dock = DockStyle.Fill;
            detail.Dock = DockStyle.Fill; detail.AutoEllipsis = true; detail.Font = new Font(Font.FontFamily, 9.5F); detail.ForeColor = ArchivePalette.Muted;
            detail.Text = "先选择版本。每个客户端独立保存，下载后可校验文件并进行本地测试。";
            ButtonStyle(cancel, "停止任务"); ((ModernButton)cancel).Danger = true; cancel.Margin = new Padding(8, 1, 0, 7);
            footer.Controls.Add(status, 0, 0); footer.Controls.Add(detail, 0, 1); footer.Controls.Add(cancel, 1, 0); footer.SetRowSpan(cancel, 2);
            progress.Dock = DockStyle.Fill; progress.Margin = Padding.Empty; footer.Controls.Add(progress, 0, 2); footer.SetColumnSpan(progress, 2);
            shell.Controls.Add(footer, 0, 3);
        }

        private Control BuildSearchCard()
        {
            var card = new SurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14), Padding = new Padding(18, 14, 18, 16) };
            var layout = VerticalLayout(29, 26, 38, 46, 48, 44);
            layout.Controls.Add(SectionTitle("查找历史版本"), 0, 0);
            layout.Controls.Add(HintLabel("从一个时间，回到一场比赛。"), 0, 1);
            queryMode.DropDownStyle = ComboBoxStyle.DropDownList; queryMode.FlatStyle = FlatStyle.Flat;
            queryMode.Items.AddRange(new object[] { "按北京时间", "按补丁号" }); queryMode.SelectedIndex = 0;
            queryMode.AccessibleName = "版本查询方式"; queryMode.AccessibleDescription = "时间节点及补丁号查询均可选择此前或此后最近版本；补丁号转换为内置发布日期基准，全程在本地匹配。";
            queryMode.Dock = DockStyle.Fill; queryMode.Margin = new Padding(0, 2, 8, 5); queryMode.BackColor = Color.White; queryMode.ForeColor = ArchivePalette.Ink;
            timeDirection.DropDownStyle = ComboBoxStyle.DropDownList; timeDirection.FlatStyle = FlatStyle.Flat;
            timeDirection.Items.AddRange(new object[] { "此前最近版本", "此后最近版本" }); timeDirection.SelectedIndex = 0;
            timeDirection.AccessibleName = "时间匹配方向"; timeDirection.AccessibleDescription = "时间节点及补丁号查询均适用；以输入时刻或内置补丁日期为基准，选择严格早于或晚于该基准的最近公开版本，相等排除。";
            timeDirection.Dock = DockStyle.Fill; timeDirection.Margin = new Padding(0, 2, 0, 5); timeDirection.BackColor = Color.White; timeDirection.ForeColor = ArchivePalette.Ink;
            var modeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46)); modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54)); modeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            modeRow.Controls.Add(queryMode, 0, 0); modeRow.Controls.Add(timeDirection, 1, 0); layout.Controls.Add(modeRow, 0, 2);
            queryValue.Text = "2019-06-19 12:00:00"; queryValue.AccessibleName = "时间节点或补丁号";
            layout.Controls.Add(InputField(queryValue), 0, 3);
            queryHint.Dock = DockStyle.Fill; queryHint.ForeColor = ArchivePalette.Muted; queryHint.Font = new Font(Font.FontFamily, 9F); queryHint.Padding = new Padding(0, 4, 0, 2); UpdateQueryHint(); layout.Controls.Add(queryHint, 0, 4);
            var commands = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            commands.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62)); commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            ButtonStyle(run, "查找并下载", true); ButtonStyle(resolveOnly, "仅查找"); run.Margin = new Padding(0, 0, 8, 0); resolveOnly.Margin = Padding.Empty;
            commands.Controls.Add(run, 0, 0); commands.Controls.Add(resolveOnly, 1, 0); layout.Controls.Add(commands, 0, 5);
            card.Controls.Add(layout); return card;
        }

        private Control BuildStorageCard()
        {
            var card = new SurfacePanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18, 14, 18, 14) };
            var layout = VerticalLayout(30, 25, 43, 28, 40, 31, 29);
            layout.Controls.Add(SectionTitle("下载设置"), 0, 0); layout.Controls.Add(HintLabel("版本保存位置"), 0, 1);
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            pathRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            rootDirectory.Text = DefaultDownloadRoot(); rootDirectory.AccessibleName = "版本保存根目录"; rootDirectory.Font = new Font(Font.FontFamily, 9.5F);
            pathRow.Controls.Add(InputField(rootDirectory), 0, 0); ButtonStyle(selectDirectory, "更改"); selectDirectory.Margin = new Padding(6, 0, 0, 4); pathRow.Controls.Add(selectDirectory, 1, 0); layout.Controls.Add(pathRow, 0, 2);
            layout.Controls.Add(HintLabel("Steam 账户名 · 可选"), 0, 3);
            account.AccessibleName = "Steam 账户名，可留空扫码"; layout.Controls.Add(InputField(account), 0, 4);
            directSteam.Text = "Steam 直连"; directSteam.Checked = true; directSteam.Dock = DockStyle.Fill; directSteam.ForeColor = ArchivePalette.Ink; directSteam.Margin = new Padding(0, 3, 0, 0); layout.Controls.Add(directSteam, 0, 5);
            layout.Controls.Add(HintLabel("保存路径使用英文或数字；账户可留空扫码。", 8.5F), 0, 6);
            card.Controls.Add(layout); return card;
        }

        private void BuildBrowserWorkspace()
        {
            browserTab.Padding = new Padding(10);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ButtonStyle(login, "登录 SteamDB"); ButtonStyle(continueBrowser, "已完成验证，继续", true); toolbar.Controls.Add(login, 0, 0); toolbar.Controls.Add(continueBrowser, 1, 0);
            layout.Controls.Add(toolbar, 0, 0); browser.View.Dock = DockStyle.Fill; layout.Controls.Add(browser.View, 0, 1);
            browserStatus.Dock = DockStyle.Fill; browserStatus.AutoEllipsis = true; browserStatus.Font = new Font(Font.FontFamily, 9.5F); browserStatus.ForeColor = ArchivePalette.Muted; browserStatus.Padding = new Padding(2, 8, 2, 0);
            browserStatus.Text = "本地资料不足或更新版本资料时，才需要使用此浏览器。登录和网站验证由你手动完成。"; layout.Controls.Add(browserStatus, 0, 2); browserTab.Controls.Add(layout);
        }

        private void BuildSteamWorkspace()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(24, 10, 24, 6) };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            qr.Dock = DockStyle.Fill; qr.SizeMode = PictureBoxSizeMode.Zoom; qr.BackColor = Color.White;
            qrCaption.Text = "需要 Steam 下载授权时，二维码会显示在这里。\r\n使用手机 Steam App 扫码即可，无需输入密码。";
            qrCaption.Dock = DockStyle.Fill; qrCaption.TextAlign = ContentAlignment.MiddleCenter; qrCaption.ForeColor = ArchivePalette.Muted;
            ButtonStyle(refreshSteamQr, "刷新登录二维码"); refreshSteamQr.AccessibleName = "刷新 Steam 登录二维码";
            layout.Controls.Add(qr, 0, 0); layout.Controls.Add(qrCaption, 0, 1); layout.Controls.Add(refreshSteamQr, 0, 2); qrTab.Controls.Add(layout);
        }

        private void UpdateQueryHint() { queryHint.Text = queryMode.SelectedIndex == 1 ? "例如 7.00、7.22b\r\n本地匹配严格" + (timeDirection.SelectedIndex == 1 ? "晚于" : "早于") + "该补丁日期基准的版本。" : "格式：2019-06-19 12:00:00\r\n匹配严格" + (timeDirection.SelectedIndex == 1 ? "晚于" : "早于") + "该时刻的最近版本。"; }
        private static TableLayoutPanel VerticalLayout(params int[] heights)
        {
            var result = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length, Margin = Padding.Empty, Padding = Padding.Empty };
            result.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); foreach(int height in heights) result.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); return result;
        }
        private Label SectionTitle(string text) { return new Label { Text = text, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), ForeColor = ArchivePalette.Ink, Dock = DockStyle.Fill, Margin = Padding.Empty }; }
        private Label HintLabel(string text, float size = 9.5F) { return new Label { Text = text, Font = new Font(Font.FontFamily, size), ForeColor = ArchivePalette.Muted, Dock = DockStyle.Fill, AutoEllipsis = true, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft }; }
        private Control InputField(TextBox box)
        {
            var frame = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 7), Margin = new Padding(0, 0, 0, 4), CornerRadius = 7 };
            box.BorderStyle = BorderStyle.None; box.Dock = DockStyle.Fill; box.BackColor = Color.White; box.ForeColor = ArchivePalette.Ink; box.Margin = Padding.Empty;
            box.Enter += delegate { frame.BorderColor = ArchivePalette.Accent; frame.Invalidate(); }; box.Leave += delegate { frame.BorderColor = ArchivePalette.Border; frame.Invalidate(); };
            frame.Controls.Add(box); return frame;
        }
        private static void ButtonStyle(Button button, string text, bool primary = false)
        {
            button.Text = text; button.Dock = DockStyle.Fill; button.Height = 42; button.Margin = new Padding(4, 0, 4, 0); button.Cursor = Cursors.Hand;
            var modern = button as ModernButton; if(modern != null) modern.Primary = primary;
        }
        private static void ReadOnlyBox(TextBox box)
        {
            box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Vertical; box.Dock = DockStyle.Fill;
            box.BackColor = Color.White; box.ForeColor = ArchivePalette.Ink; box.BorderStyle = BorderStyle.None; box.Margin = Padding.Empty;
        }
    }
}
