using MechrevoLite.UI;

namespace MechrevoLite;

/// <summary>赞助窗口：展示微信收款码，扫码支持 L-Mechrevo。</summary>
public class DonateForm : RForm
{
    public DonateForm()
    {
        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        Text = "赞助支持";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 500);
        MinimumSize = new Size(520, 300);
        InitTheme(true);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24, 20, 24, 24),
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "感谢支持 L-Mechrevo",
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Display, FontStyle.Bold),
            ForeColor = UiVisualStyle.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(title, 0, 0);

        var sub = new Label
        {
            Text = "扫码赞助，支持持续开发",
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Subtitle),
            ForeColor = UiVisualStyle.Muted,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16),
        };
        root.Controls.Add(sub, 0, 1);

        var qrGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor,
        };
        qrGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        qrGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        qrGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(qrGrid, 0, 2);

        PictureBox MakeQr(string path, bool left)
        {
            var pb = new PictureBox
            {
                Dock = DockStyle.Fill,
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
                    Text = "二维码资源缺失",
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
        ResponsiveLayout.ScaleFrom96(this, this);
    }
}
