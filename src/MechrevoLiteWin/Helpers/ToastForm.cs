using System.Diagnostics;
using System.Drawing.Drawing2D;
using MechrevoLite.UI;

namespace MechrevoLite.Helpers
{

    static class Drawing
    {

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            Size size = new Size(diameter, diameter);
            Rectangle arc = new Rectangle(bounds.Location, size);
            GraphicsPath path = new GraphicsPath();

            if (radius == 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int cornerRadius)
        {
            using (GraphicsPath path = RoundedRect(bounds, cornerRadius))
            {
                graphics.FillPath(brush, path);
            }
        }
    }

    public enum ToastIcon
    {
        BrightnessUp,
        BrightnessDown,
        BacklightUp,
        BacklightDown,
        Touchpad,
        Microphone,
        MicrophoneMute,
        FnLock,
        Battery,
        Charger,
        Controller
    }

    public class ToastForm : OSDNativeForm
    {

        protected static string toastText = "Balanced";
        protected static ToastIcon? toastIcon = null;

        protected static System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public ToastForm()
        {
            timer.Tick += timer_Tick;
            timer.Enabled = false;
            timer.Interval = 2000;
        }

        // 例外：Toast 倒计时数字是独立视觉层级（Pixel 单位的大号数字），不进 TypeScale 刻度。
        // 例外白名单见 docs/ui-consistency-pass.md §2.1。
        private static readonly Font _toastFont = UiVisualStyle.Font(36f, FontStyle.Bold, GraphicsUnit.Pixel);
        private static readonly SolidBrush _toastBrush = new SolidBrush(Color.FromArgb(150, Color.Black));
        private static readonly SolidBrush _toastTextBrush = new SolidBrush(Color.White);
        private static readonly StringFormat _toastFormat = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Center
        };

        protected override void PerformPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillRoundedRectangle(_toastBrush, Bound, 10);

            using Bitmap? icon = toastIcon switch
            {
                ToastIcon.BrightnessUp   => Properties.Resources.brightness_up,
                ToastIcon.BrightnessDown => Properties.Resources.brightness_down,
                ToastIcon.BacklightUp    => Properties.Resources.backlight_up,
                ToastIcon.BacklightDown  => Properties.Resources.backlight_down,
                ToastIcon.Microphone     => Properties.Resources.icons8_microphone_96,
                ToastIcon.MicrophoneMute => Properties.Resources.icons8_mute_unmute_96,
                ToastIcon.Touchpad       => Properties.Resources.icons8_touchpad_96,
                ToastIcon.FnLock         => Properties.Resources.icons8_function,
                ToastIcon.Battery        => Properties.Resources.icons8_charged_battery_96,
                ToastIcon.Charger        => Properties.Resources.icons8_charging_battery_96,
                ToastIcon.Controller     => Properties.Resources.icons8_controller_96,
                _                        => null
            };

            int shiftX = 0;

            if (icon is not null)
            {
                e.Graphics.DrawImage(icon, 18, 18, 64, 64);
                shiftX = 40;
            }

            e.Graphics.DrawString(toastText, _toastFont, _toastTextBrush,
                new PointF(Bound.Width / 2 + shiftX, Bound.Height / 2), _toastFormat);
        }

        /// <summary>测试接缝：失败 Toast 在调用 <see cref="RunToast"/> 之前先通知这里，避免测试构造 WinForms OSD。</summary>
        internal static Action<string>? FailureCallback;

        /// <summary>测试接缝：非失败提示（例如回读不能证明充电已受控）。不走 <see cref="FailureCallback"/>。</summary>
        internal static Action<string>? NoticeCallback;

        internal static void ShowFailure(string message)
        {
            FailureCallback?.Invoke(message);
            try { Program.toast?.RunToast(message); }
            catch (Exception tex) { Logger.WriteLine("Failure toast failed: " + tex.Message); }
        }

        internal static void ShowNotice(string message)
        {
            NoticeCallback?.Invoke(message);
            try
            {
                SettingsForm? form = Program.settingsForm;
                if (form is null || form.IsDisposed || Program.toast is null) return;
                void Show()
                {
                    try { Program.toast?.RunToast(message); }
                    catch (Exception tex) { Logger.WriteLine("Notice toast failed: " + tex.Message); }
                }
                if (form.InvokeRequired) form.BeginInvoke(Show);
                else Show();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Notice toast failed: " + ex.Message);
            }
        }

        public void RunToast(string text, ToastIcon? icon = null)
        {

            Program.settingsForm.Invoke(delegate
            {
                //Hide();
                timer.Stop();

                toastText = text;
                toastIcon = icon;

                nint dpiContext = User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

                Screen screen1 = Screen.FromHandle(Handle);

                Width = Math.Max(300, 100 + toastText.Length * 22);
                Height = 100;
                X = (screen1.Bounds.Width - Width) / 2;
                Y = screen1.Bounds.Height - 300 - Height;

                Show();
                User32.SetThreadDpiAwarenessContext(dpiContext);

                timer.Start();

                Program.settingsForm.AccessibilityObject.RaiseAutomationNotification(
                    System.Windows.Forms.Automation.AutomationNotificationKind.ActionCompleted,
                    System.Windows.Forms.Automation.AutomationNotificationProcessing.MostRecent,
                    text);

            });

        }

        private void timer_Tick(object? sender, EventArgs e)
        {
            //Debug.WriteLine("Toast end");
            Hide();
            timer.Stop();
        }
    }
}
