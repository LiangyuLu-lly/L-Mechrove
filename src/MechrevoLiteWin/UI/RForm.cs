using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace MechrevoLite.UI
{
    public class RForm : Form
    {

        protected RForm()
        {
            // Programmatic forms use a 96-DPI logical baseline. Designer forms may
            // override AutoScaleDimensions in InitializeComponent.
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body);
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        public static Color colorEco = Color.FromArgb(255, 6, 180, 138);
        public static Color colorStandard = Color.FromArgb(255, 58, 174, 239);
        public static Color colorTurbo = Color.FromArgb(255, 255, 32, 32);
        public static Color colorCustom = Color.FromArgb(255, 255, 128, 0);
        public static Color colorGray = Color.FromArgb(255, 168, 168, 168);


        public static Color buttonMain;
        public static Color buttonSecond;

        public static Color formBack;
        public static Color foreMain;
        public static Color borderMain;
        public static Color borderSecond;
        public static Color chartMain;
        public static Color chartGrid;

        public static bool flatTheme = false;

        [DllImport("UXTheme.dll", SetLastError = true, EntryPoint = "#138")]
        public static extern bool CheckSystemDarkModeStatus();

        [DllImport("UXTheme.dll", SetLastError = true, EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int preferredAppMode);

        [DllImport("UXTheme.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(nint hWnd, string pszSubAppName, string? pszSubIdList);

        [DllImport("DwmApi")] //System.Runtime.InteropServices
        private static extern int DwmSetWindowAttribute(nint hwnd, int attr, int[] attrValue, int attrSize);

        public bool darkTheme = false;
        internal float AuditLayoutScale { get; set; }
        protected float EffectiveLayoutScale => AuditLayoutScale > 0
            ? AuditLayoutScale
            : UiDpi.LayoutScale(this);
        private bool themeInitialized = false;
        protected override CreateParams CreateParams
        {
            get
            {
                var parms = base.CreateParams;
                // Keep parent repaints out of native child-control rectangles. This
                // prevents unrelated mode buttons flashing while a slider repaints.
                parms.Style |= 0x02000000;   // WS_CLIPCHILDREN
                parms.ClassStyle &= ~0x00020000;
                return parms;
            }
        }
        public static void InitColors(bool darkTheme)
        {
            flatTheme = AppConfig.GetString("theme")?.ToLower() == "flat";

            if (darkTheme)
            {
                buttonMain = UiVisualStyle.SurfaceRaised;
                buttonSecond = UiVisualStyle.Surface;

                formBack = UiVisualStyle.Window;
                foreMain = UiVisualStyle.Text;
                borderMain = UiVisualStyle.Border;
                borderSecond = Color.FromArgb(44, 50, 56);

                chartMain = UiVisualStyle.Surface;
                chartGrid = Color.FromArgb(58, 66, 74);
            }
            else
            {
                buttonMain = UiVisualStyle.SurfaceRaised;
                buttonSecond = UiVisualStyle.Surface;

                formBack = UiVisualStyle.Window;
                foreMain = UiVisualStyle.Text;
                borderMain = UiVisualStyle.Border;
                borderSecond = Color.FromArgb(196, 206, 216);

                chartMain = UiVisualStyle.Surface;
                chartGrid = Color.FromArgb(202, 212, 221);
            }
        }

        private static bool IsDarkTheme()
        {
            return UiVisualStyle.IsNightMode;
        }

        public bool InitTheme(bool setDPI = false)
        {
            bool newDarkTheme = IsDarkTheme();
            bool changed = darkTheme != newDarkTheme;
            bool firstInit = !themeInitialized;
            darkTheme = newDarkTheme;
            themeInitialized = true;

            InitColors(darkTheme);

            if (setDPI)
                ControlHelper.Resize(this);

            if (changed || firstInit)
            {
                DwmSetWindowAttribute(Handle, 20, new[] { darkTheme ? 1 : 0 }, 4);
                SetPreferredAppMode(darkTheme ? 1 : 0); 
                SetWindowTheme(Handle, darkTheme ? "DarkMode_Explorer" : "Explorer", null);
                ControlHelper.Adjust(this, changed);
                this.Invalidate();
            }


            return changed;

        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyResponsiveBounds();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(CreateResponsiveBoundsCallback(this));
        }

        internal static MethodInvoker CreateResponsiveBoundsCallback(RForm form) =>
            () => form.ApplyResponsiveBounds();

        public virtual void ApplyResponsiveBounds(Rectangle? workingArea = null)
            => ResponsiveLayout.ConstrainToWorkingArea(this, workingArea);

    }
}
