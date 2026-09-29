using MechrevoLite.Properties;
using MechrevoLite.UI;

namespace MechrevoLite;

/// <summary>赞助窗口：展示微信收款码，扫码支持 L-Mechrevo。</summary>
public class DonateForm : RForm
{
    public DonateForm()
    {
        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        Text = Strings.DonateTitle;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // 宽对齐主窗。最小宽必须低于主窗，否则 MinimumSize 会把客户区撑回去。
        ClientSize = new Size(SettingsForm.CompactDashboardLogicalClientSize.Width, 360);
        MinimumSize = new Size(320, 240);
        InitTheme(true);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24, 20, 24, 24),
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = Strings.DonateThanks,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Display, FontStyle.Bold),
            ForeColor = UiVisualStyle.Text,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(title, 0, 0);

        var sub = new Label
        {
            Text = Strings.DonateScan,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Subtitle),
            ForeColor = UiVisualStyle.Muted,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 16),
        };
        root.Controls.Add(sub, 0, 1);

        var qrGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor,
        };
        qrGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        qrGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        qrGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(qrGrid, 0, 2);

        // 两枚码并排塞进 420：左右 padding 24×2 + 中间缝 20，剩余平分。Zoom 保持可扫，不裁切。
        int qr = (SettingsForm.CompactDashboardLogicalClientSize.Width - 48 - 20) / 2;
        PictureBox MakeQr(string path, bool left)
        {
            var pb = new PictureBox
            {
                Dock = DockStyle.Top,
                Size = new Size(qr, qr),
                Margin = left ? new Padding(0, 0, 10, 0) : new Padding(10, 0, 0, 0),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = UiVisualStyle.SurfaceRaised,
                BorderStyle = BorderStyle.None,
            };
            bool loaded = false;
            try
            {
                var full = Path.Combine(AppContext.BaseDirectory, "Resources", path);
                if (File.Exists(full))
                {
                    using var img = Image.FromFile(full);
                    pb.Image = new Bitmap(img);   // 复制：释放文件句柄
                    loaded = true;
                }
            }
            catch (Exception ex) { Logger.WriteLine("Donate qr load fail: " + ex.Message); }
            if (!loaded)
            {
                // QR 缺失空态（Empty States）：不留空 SurfaceRaised 色块，叠 Muted 居中文案。
                var missing = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = Strings.DonateQrMissing,
                    ForeColor = UiVisualStyle.Muted,
                    BackColor = UiVisualStyle.SurfaceRaised,
                };
                pb.Controls.Add(missing);
            }
            return pb;
        }

        qrGrid.Controls.Add(MakeQr("qrcode1.jpg", true), 0, 0);
        qrGrid.Controls.Add(MakeQr("qrcode2.jpg", false), 1, 0);
        UiVisualStyle.ApplyWindow(this);
        root.BackColor = UiVisualStyle.Window;
        qrGrid.BackColor = UiVisualStyle.Window;
        UiVisualStyle.ApplyTitle(title, UiVisualStyle.TypeScale.Display);
        ResponsiveLayout.PerformLayoutTree(this);
        ResponsiveLayout.ScaleFrom96(this, this);
        int width = ResponsiveLayout.LogicalToDevice(this, SettingsForm.CompactDashboardLogicalClientSize.Width);
        ClientSize = new Size(width, ClientSize.Height);
        ResponsiveLayout.PerformLayoutTree(this);
        // 高取内容在该宽度下的首选高（含边距），不是布局前的 root.Height——后者在缩放取整后
        // 会少 3–4px（审计 1280x720-100pct：需要 297、客户区 293）。内容万一更高也能滚动，不会被裁。
        AutoScroll = true;
        int height = Math.Max(root.Height, root.GetPreferredSize(new Size(width, 0)).Height);
        ClientSize = new Size(width, height);
    }
}
