using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 更新窗口「要关两次」修复（run5 final）。
///
/// 根因：<c>buttonUpdates.Click</c> 在 <c>SettingsForm</c> 构造函数里被绑定**两次**
/// （<c>Settings.cs:2097</c> + <c>Settings.V2.cs:918</c>）。一次点击会顺序执行两个处理器：
/// 第一个 <c>ShowDialog</c> 阻塞到窗口关闭，返回后第二个处理器又弹一个——用户必须关两次。
///
/// 修复：删除 V2 footer 里的重复绑定，只保留构造函数中的一处。
/// 本测试锁定「更新键只允许一个 Click 处理器」，这是单次关闭的直接回归护栏。
/// </summary>
public class UpdateWindowSingleCloseTests
{
    static int ClickHandlerCount(Control control)
    {
        var eventsProp = typeof(Component).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var events = (EventHandlerList)eventsProp.GetValue(control)!;
        // 事件键字段名随运行时不同：.NET Framework 是 EventClick，现代 .NET 是 s_clickEvent。
        FieldInfo? keyField = typeof(Control).GetField("s_clickEvent", BindingFlags.Static | BindingFlags.NonPublic)
            ?? typeof(Control).GetField("EventClick", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(keyField);
        object clickKey = keyField!.GetValue(null)!;
        return (events[clickKey] as Delegate)?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void UpdatesButton_HasExactlyOneClickHandler()
    {
        bool previousAudit = Program.UiAuditMode;
        Program.UiAuditMode = true;
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var button = form.Controls.Find("buttonUpdates", true).OfType<RButton>().Single();

            Assert.Equal(1, ClickHandlerCount(button));
        }
        finally { Program.UiAuditMode = previousAudit; }
    }

    /// <summary>空闲时一次 Close 请求必须让更新窗口消失（不会静默无操作）。</summary>
    [Fact]
    public void UpdateForm_SingleCloseRequest_DismissesWhileIdle()
    {
        bool previousAudit = Program.UiAuditMode;
        Program.UiAuditMode = true;
        try
        {
            using var form = new MechrevoLite.Update.UpdateForm(new MechrevoLite.Update.UpdateInfo(
                CurrentVersion: "0.0.0-test", LatestVersion: "9.9.9-test", Channel: "test", ChannelFallback: false,
                UpdateAvailable: false, ReleaseDate: null, Notes: null, FileName: null, Size: null,
                Sha256: null, DownloadUrl: null, DownloadPage: null));
            form.Show();
            Application.DoEvents();
            Assert.True(form.Visible);

            form.Close();
            Application.DoEvents();

            Assert.True(form.IsDisposed || !form.Visible, "一次关闭请求后窗口必须消失。");
        }
        finally { Program.UiAuditMode = previousAudit; }
    }
}
