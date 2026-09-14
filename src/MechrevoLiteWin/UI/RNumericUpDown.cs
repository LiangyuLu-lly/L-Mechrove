using System.Runtime.InteropServices;

namespace MechrevoLite.UI
{
    public class RNumericUpDown : NumericUpDown
    {
        [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

        public RNumericUpDown()
        {
            BorderStyle = BorderStyle.None;
        }

        /// <summary>
        /// 隐藏系统绘制的 UpDownButtons 子窗口（UpDownBase 的第一个子控件），
        /// 由调用方在旁边放置自绘 −/+ 键；数值语义（UpButton/DownButton/Increment）不变。
        /// </summary>
        public void HideNativeSpinButtons()
        {
            if (NativeSpinButtonsHidden) return;
            if (Controls.Count > 0)
                Controls[0].Visible = false;
            NativeSpinButtonsHidden = true;
        }

        /// <summary>测试缝：原生微调按钮子窗口是否已被隐藏。</summary>
        internal bool NativeSpinButtonsHidden { get; private set; }

        // UpDownBase 是 Opaque 样式且 BorderStyle.None 时不填背景：隐藏微调按钮后，
        // 系统为箭头预留的右侧条带会露出窗口类默认白底（真机实测），这里用 BackColor 补齐。
        protected override void OnPaint(PaintEventArgs e)
        {
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(brush, ClientRectangle);
            base.OnPaint(e);
        }

        public void ApplyTheme(bool dark)
        {
            BackColor = dark ? RForm.buttonMain : SystemColors.Window;
            ForeColor = dark ? RForm.foreMain : SystemColors.WindowText;

            string theme = dark ? "DarkMode_Explorer" : "Explorer";
            SetWindowTheme(Handle, theme, null);
            foreach (Control child in Controls)
                SetWindowTheme(child.Handle, theme, null);
        }
    }
}
