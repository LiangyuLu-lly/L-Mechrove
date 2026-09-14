using MechrevoLite.UI;
using System.Diagnostics;

namespace MechrevoLite.Helpers
{
    public class DonateControl
    {
        private readonly SettingsForm _settings;
        private readonly RBadgeButton _button;
        private CustomContextMenu? _contextMenu;

        public DonateControl(SettingsForm settings, RBadgeButton button)
        {
            _settings = settings;
            _button = button;
        }

        public void Init()
        {
            _button.Click += Button_Click;
            _button.MouseUp += Button_MouseUp;

            if (!AppConfig.Is("donate_dismissed"))
            {
                int click = AppConfig.Get("donate_click");
                int startCount = AppConfig.Get("start_count");
                if (startCount >= ((click < 20) ? 20 : click + 50))
                {
                    // run5 收尾：徽章提醒描边改主题 token（原遗留静态 RForm.colorTurbo 不随日夜主题）。
                    _button.BorderColor = UiVisualStyle.Accent;
                    _button.Badge = Math.Clamp((startCount - click) / 50, 1, 5);
                }
            }
        }

        public void ApplyTheme()
        {
            if (_contextMenu is not null)
            {
                _contextMenu.BackColor = _settings.BackColor;
                _contextMenu.ForeColor = _settings.ForeColor;
            }
        }

        private void Button_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            _contextMenu ??= BuildContextMenu();

            ApplyTheme();
            _contextMenu.Show(_button, new Point(e.X, e.Y));
        }

        // 提取为独立方法以便测试断言（无死菜单项规则，docs/run4-p2p3-design-spec.md §4.5）。
        private CustomContextMenu BuildContextMenu()
        {
            var padding = new Padding(5, 5, 5, 5);

            var menuAlreadyDonated = new ToolStripMenuItem("已赞助 ❤") { Margin = padding };
            menuAlreadyDonated.Click += (s, ev) =>
            {
                AppConfig.Set("donate_dismissed", 1);
                SetThankYou();
            };

            var menuNotInterested = new ToolStripMenuItem("不再提醒") { Margin = padding };
            menuNotInterested.Click += (s, ev) =>
            {
                AppConfig.Set("donate_dismissed", 1);
                _button.Badge = 0;
            };

            // 原「Cancel」项没有任何 Click 处理器（死菜单项）：菜单外点击本身就是取消，
            // 无需菜单项，直接删除。
            var menu = new CustomContextMenu();
            menu.ShowImageMargin = false;
            menu.Items.Add(menuAlreadyDonated);
            menu.Items.Add(menuNotInterested);
            menu.Renderer = new CustomMenuRenderer();
            return menu;
        }

        private DonateForm? _donateForm;

        private void Button_Click(object? sender, EventArgs e)
        {
            AppConfig.Set("donate_click", AppConfig.Get("start_count"));
            SetThankYou();
            if (_donateForm is null || _donateForm.IsDisposed)
            {
                _donateForm = new DonateForm();
                _settings.AddOwnedForm(_donateForm);
            }
            if (_donateForm.Visible) _donateForm.Close();
            else _donateForm.Show();
        }

        private void SetThankYou()
        {
            _button.Badge = 0;
            _button.Text = Properties.Strings.ThankYou;
        }
    }
}
