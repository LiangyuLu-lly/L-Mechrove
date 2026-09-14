using MechrevoLite.Hardware;
using MechrevoLite.Update;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace MechrevoLite.UI;

internal static class UiAuditRunner
{
    private sealed record Viewport(int Width, int Height, int Dpi)
    {
        public string Id => $"{Width}x{Height}-{Dpi * 100 / 96}pct";
        public Rectangle WorkingArea => new(0, 0, Width, Height - (int)Math.Round(48 * Dpi / 96f));
    }

    private sealed record AuditIssue(string Form, string Viewport, string Kind, string Control, string Detail);

    private static readonly Viewport[] Viewports =
    {
        new(1280, 720, 96),
        new(1280, 720, 144),
        new(1280, 720, 192),
        new(1366, 768, 120),
        new(1366, 768, 192),
        new(1600, 900, 120),
        new(1600, 900, 144),
        new(1920, 1080, 96),
        new(1920, 1080, 120),
        new(1920, 1080, 144),
        new(1920, 1080, 168),
        new(1920, 1080, 192),
        new(2560, 1440, 144),
        new(2560, 1440, 192),
        new(3840, 2160, 168),
        new(3840, 2160, 192),
    };

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Program.UiAuditMode = true;

        int hostDpi = ReadHostDpi();
        Logger.WriteLine($"UI audit host DPI = {hostDpi} (viewport DPI is pinned via UiDpi.AuditDpi).");

        var factories = new (string Name, Func<Form> Create, MechrevoDeviceCapabilities? Capabilities)[]
        {
            ("Settings", () => new SettingsForm(), null),
            ("Settings-NoIGpu", () => new SettingsForm(), new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                TurboMode = true,
                TurboSubMode = false,
                CpuPerformanceTuning = true,
                DgpuDirect = true,
                IgpuOnly = false,
            }),
            ("Settings-HotSwap", () => new SettingsForm(), new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                TurboMode = true,
                CpuPerformanceTuning = true,
                DgpuDirect = true,
                IgpuOnly = true,
                GpuHotSwap = true,
            }),
            ("Settings-CustomMode", () => new SettingsForm(), new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                TurboMode = true,
                CpuPerformanceTuning = true,
                FanSettings = true,
                LiquidCooling = true,
                DgpuDirect = true,
                IgpuOnly = true,
            }),
            ("Settings-Minimal", () => new SettingsForm(), new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                TurboMode = false,
                TurboSubMode = false,
                DgpuDirect = false,
                IgpuOnly = false,
            }),
            ("CustomMode", () => new CustomModeForm(), new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                CpuPerformanceTuning = true,
                OverclockSettings = true,
            }),
            ("FanCurve", () => new FanCurveForm(), new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                FanSettings = true,
            }),
            // ColorCalibration 表单已删（2026-09-14：P3/AdobeRGB 不生效，弹窗改为屏幕行头内联下拉）。
            ("FirstRunGuide", () => new FirstRunGuideForm(), null),
            ("KeyboardRgb", () => new RgbForm(new KeyboardRgb()), null),
            ("Lightbar", () => new LightForm("HidLightbar/Ctrl", "灯条灯效", LightForm.LightbarEffects), null),
            ("LogoLight", () => new LightForm("HidLightbar_Logo/Ctrl", "Logo灯效", LightForm.LogoEffects), null),
            ("Donate", () => new DonateForm(), null),
            ("ColorPicker", () => new RColorPicker(Color.FromArgb(50, 219, 190), true), null),
            // 设置弹窗此前漏采：宿主面板在主窗里是游离（未挂树）控件，只有 ⚙ 弹窗托管时才参与布局，
            // 因此它的「控制台」右缘裁切从未进入审计。这里复刻 Settings.cs 的构建：
            // BuildThemeModePanel / BuildOfficialConsolePanel 的面板结构 + 两个可选控件。
            ("SettingsDialog", () =>
            {
                // 与主窗构建一致：面板按宿主 DPI 定尺寸（设备像素），随后由审计的相对 scaling 归一到视口。
                int D(int value) => (int)Math.Round(value * hostDpi / 96f);
                var themePanel = new BufferedPanel
                {
                    Name = "panelThemeMode",
                    CardStyle = true,
                    Height = D(54),
                    Padding = new Padding(D(12), D(8), D(12), D(8)),
                    BackColor = UiVisualStyle.Surface,
                };
                var themeLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 3,
                    RowCount = 1,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty,
                    BackColor = themePanel.BackColor,
                };
                themeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                themeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                themeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                themeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                themeLayout.Controls.Add(new Label
                {
                    Name = "labelThemeModeTitle",
                    Text = "界面外观",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = Padding.Empty,
                    AutoEllipsis = true,
                }, 0, 0);
                themeLayout.Controls.Add(new Button
                {
                    Name = "buttonDayMode",
                    Text = "日间",
                    Dock = DockStyle.Fill,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                }, 1, 0);
                themeLayout.Controls.Add(new Button
                {
                    Name = "buttonNightMode",
                    Text = "夜间",
                    Dock = DockStyle.Fill,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand,
                }, 2, 0);
                themePanel.Controls.Add(themeLayout);

                var officialPanel = new BufferedPanel
                {
                    Name = "panelOfficialConsole",
                    CardStyle = true,
                    Height = D(48),
                    Padding = new Padding(D(12), D(8), D(12), D(8)),
                    BackColor = UiVisualStyle.Surface,
                };
                var officialLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 3,
                    RowCount = 1,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty,
                    BackColor = officialPanel.BackColor,
                };
                officialLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(126)));
                officialLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                officialLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(150)));
                officialLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                officialLayout.Controls.Add(new Label
                {
                    Name = "labelOfficialConsoleTitle",
                    Text = "官方控制台",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = Padding.Empty,
                    AutoEllipsis = true,
                }, 0, 0);
                officialLayout.Controls.Add(new Label
                {
                    Name = "labelOfficialConsoleStatus",
                    Text = "正在检测...",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleRight,
                    Margin = Padding.Empty,
                    AutoEllipsis = true,
                }, 1, 0);
                officialLayout.Controls.Add(new Button
                {
                    Name = "buttonOfficialConsole",
                    Text = "打开官方控制台",
                    Dock = DockStyle.Fill,
                    Margin = new Padding(D(8), 0, 0, 0),
                    Cursor = Cursors.Hand,
                }, 2, 0);
                officialPanel.Controls.Add(officialLayout);

                var overdrive = new RCheckBox
                {
                    Name = "checkLcdOverdrive",
                    Text = "响应加速",
                    AutoSize = true,
                    Visible = true,
                    ForeColor = UiVisualStyle.Text,
                };
                // 屏幕校色按钮已删（改为屏幕行头内联下拉，主窗审计已覆盖该行）。
                return new SettingsDialog(themePanel, officialPanel, overdrive, displayGroupAvailable: true);
            }, null),
            // 更新窗口同样漏采；注入合成更新信息（网盘发布态：无直链/哈希，不走自替换检查），
            // 保证渲染不触网（Shown 不再刷新）。
            ("UpdateForm", () => new UpdateForm(new UpdateInfo(
                CurrentVersion: "0.0.0-audit",
                LatestVersion: "9.9.9-audit",
                Channel: "audit",
                ChannelFallback: false,
                UpdateAvailable: true,
                ReleaseDate: "2026-09-13",
                Notes: "审计合成数据（离线，不触网）。",
                FileName: null,
                Size: null,
                Sha256: null,
                DownloadUrl: null,
                DownloadPage: "https://example.invalid/audit")), null),
        };

        var issues = new List<AuditIssue>();
        var screenshots = new List<string>();
        string activeFactory = "unknown";
        string activeViewport = "unknown";
        ThreadExceptionEventHandler threadExceptionHandler = (_, args) =>
        {
            issues.Add(new AuditIssue(activeFactory, activeViewport, "ui-thread-exception", activeFactory,
                args.Exception.ToString()));
            Logger.WriteLine($"UI audit thread exception ({activeFactory}/{activeViewport}): {args.Exception}");
        };
        Application.ThreadException += threadExceptionHandler;

        foreach (var viewport in Viewports)
        {
            foreach (var factory in factories)
            {
                UiVisualStyle.SetAuditNightMode(true);
                activeFactory = factory.Name;
                activeViewport = viewport.Id;
                Form? form = null;
                MechrevoHw? auditHardware = null;
                try
                {
                    // 固定绘制用 DPI。只影响自绘控件的圆角与描边计算（UiDpi.Paint），
                    // 不影响布局（UiDpi.Layout 始终读真实宿主 DPI）——布局的目标尺寸是
                    // 靠下面的相对 form.Scale 达成的，若两处一起改会把同一比例乘两遍。
                    UiDpi.AuditDpi = viewport.Dpi;
                    Program.UiAuditUseReportedCapabilities = factory.Capabilities is not null;
                    if (factory.Capabilities is not null)
                    {
                        auditHardware = new MechrevoHw(null, factory.Capabilities);
                        if (factory.Name == "CustomMode")
                        {
                            auditHardware.HandleMessage("Fan/Status", """
                                {"GPU_CoreClockOffsetMinimumHWOC":-500,"GPU_CoreClockOffsetMaximumHWOC":500,
                                 "GPU_MemoryClockOffsetMinimumHWOC":-500,"GPU_MemoryClockOffsetMaximumHWOC":500,
                                 "GPU_CoreClockOffsetOC":500,"GPU_MemoryClockOffsetOC":-500,"OverClockingSwitch":1}
                                """);
                            auditHardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
                        }
                        if (factory.Name == "FanCurve")
                            auditHardware.HandleMessage("Fan/Status", "{\"FanControlRespective\":true}");
                        Program.hw = auditHardware;
                    }
                    form = factory.Create();
                    form.Name = string.IsNullOrWhiteSpace(form.Name) ? factory.Name : form.Name;
                    if (form is SettingsForm)
                    {
                        // Exercise the maximum hardware-capability state. These controls are
                        // hidden until service telemetry confirms support on the real machine.
                        foreach (string controlName in Program.UiAuditUseReportedCapabilities
                            ? Array.Empty<string>()
                            : new[] { "checkLcdOverdrive" })
                        {
                            Control? capability = form.Controls.Find(controlName, true).FirstOrDefault();
                            if (capability is not null) capability.Visible = true;
                        }
                        if (form.Controls.Find("buttonBatteryMode0", true).FirstOrDefault() is Button selectedMode)
                        {
                            selectedMode.FlatAppearance.BorderColor = Color.FromArgb(0, 200, 80);
                            selectedMode.FlatAppearance.BorderSize = 2;
                        }
                        if (factory.Name == "Settings-CustomMode")
                            ((SettingsForm)form).ShowMode(MechrevoService.ToVisualMode(MechrevoService.ModeCustom));
                    }
                    form.ShowInTaskbar = false;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Opacity = 0.01;
                    form.Location = new Point(20, 20);
                    form.Show();
                    Application.DoEvents();

                    // form.Scale 是相对变换，起点是窗体已按宿主 DPI 自动缩放后的状态，
                    // 所以这里读真实宿主 DPI（UiDpi.Layout 不受审计覆盖影响）。
                    int currentDpi = UiDpi.Layout(form);
                    float ratio = viewport.Dpi / (float)currentDpi;
                    if (Math.Abs(ratio - 1f) > 0.01f)
                    {
                        form.Scale(new SizeF(ratio, ratio));
                        ScaleFonts(form, ratio);
                    }

                    if (form is RForm responsive)
                    {
                        responsive.AuditLayoutScale = viewport.Dpi / 96F;
                        responsive.ApplyResponsiveBounds(viewport.WorkingArea);
                    }
                    else
                        ResponsiveLayout.ConstrainToWorkingArea(form, viewport.WorkingArea);
                    ResponsiveLayout.PerformLayoutTree(form);
                    Application.DoEvents();

                    if (form is SettingsForm settings)
                    {
                        nint originalHandle = settings.Handle;
                        settings.Hide();
                        settings.ShowAll(viewport.WorkingArea);
                        Application.DoEvents();
                        settings.Hide();
                        settings.ShowAll(viewport.WorkingArea);
                        Application.DoEvents();
                        if (!settings.Visible || settings.Opacity < 0.99 || settings.Handle != originalHandle)
                        {
                            issues.Add(new AuditIssue("Settings-TrayRestore", viewport.Id, "restore-state", settings.Name,
                                $"Visible={settings.Visible}, Opacity={settings.Opacity}, HandleRecreated={settings.Handle != originalHandle}."));
                        }
                        for (int page = 0; page < settings.DashboardPageCount; page++)
                        {
                            settings.SelectDashboardPageForAudit(page);
                            ResponsiveLayout.PerformLayoutTree(settings);
                            Application.DoEvents();
                            string pageName = factory.Name + "-" + settings.DashboardPageAuditName(page);
                            foreach (Control descendant in AllControls(settings))
                            {
                                if (descendant is not PictureBox { Name: "picturePerf" or "pictureBattery" } box) continue;
                                Logger.WriteLine($"[diag] at-capture form={settings.GetHashCode()} {box.Name}#{box.GetHashCode()} vis={box.Visible} tag={box.Tag}");
                            }
                            {
                                Control? brightPanel = AllControls(settings).FirstOrDefault(c => c.Name == "panelCPUTitle");
                                if (brightPanel is Control br && Program.UiAuditMode && viewport.Dpi == 96)
                                {
                                    Control? perf = AllControls(settings).FirstOrDefault(c => c.Name == "panelPerformance");
                                    Logger.WriteLine($"[diag] cpuperf panel={perf?.Width}x{perf?.Height} title={br.Width}x{br.Height}@{br.Location} children={string.Join(";", br.Controls.Cast<Control>().Select(c => $"{c.Name}:{c.Width}x{c.Height}@{c.Location.X},{c.Location.Y} vis={c.Visible}"))}");
                                }
                            {
                                Control? screenPanel = AllControls(settings).FirstOrDefault(c2 => c2.Name == "panelBrightness");
                                    if (screenPanel is Control spc && viewport.Dpi == 96)
                                        Logger.WriteLine($"[diag] screen: bounds={screenPanel.Bounds} vis={screenPanel.Visible} children={string.Join(";", screenPanel.Controls.Cast<Control>().Select(c3 => $"{c3.Name}:{c3.Width}x{c3.Height}@{c3.Location.X},{c3.Location.Y} vis={c3.Visible}"))}");
                                    foreach (string probe in new[] { "panelVersion", "panelStartup", "checkStartup" })
                                    {
                                        Control? hit = AllControls(settings).FirstOrDefault(c => c.Name == probe);
                                        if (hit is Control h)
                                            Logger.WriteLine($"[diag] deferred {hit.Name}: bounds={hit.Bounds} vis={hit.Visible} parent={hit.Parent?.Name ?? "null"} parentVis={hit.Parent?.Visible}");
                                    }
                                }
                            }
                            AuditForm(pageName, viewport, settings, issues);
                            foreach (string screenshot in CapturePages(outputDirectory, pageName, viewport, settings))
                                screenshots.Add(screenshot);
                        }
                        settings.ApplyThemeMode(false);
                        settings.SelectDashboardPageForAudit(2);
                        ResponsiveLayout.PerformLayoutTree(settings);
                        Application.DoEvents();
                        AuditForm(factory.Name + "-Day-System", viewport, settings, issues);
                        foreach (string screenshot in CapturePages(outputDirectory, factory.Name + "-Day-System", viewport, settings))
                            screenshots.Add(screenshot);
                        settings.ApplyThemeMode(true);
                        settings.SelectDashboardPageForAudit(0);
                        ResponsiveLayout.PerformLayoutTree(settings);
                        Application.DoEvents();
                        AuditForm(factory.Name + "-Night-Common", viewport, settings, issues);
                        foreach (string screenshot in CapturePages(outputDirectory, factory.Name + "-Night-Common", viewport, settings))
                            screenshots.Add(screenshot);
                        AuditBrightnessIsolation(viewport, settings, issues);
                    }
                    else
                    {
                        AuditForm(factory.Name, viewport, form, issues);
                        foreach (string screenshot in CapturePages(outputDirectory, factory.Name, viewport, form))
                            screenshots.Add(screenshot);
                    }
                }
                catch (Exception ex)
                {
                    issues.Add(new AuditIssue(factory.Name, viewport.Id, "render-error", factory.Name, ex.ToString()));
                }
                finally
                {
                    form?.Dispose();
                    UiDpi.AuditDpi = 0;   // 归还给真实 DPI，避免泄漏到下一个视口或正常运行
                    UiVisualStyle.SetAuditNightMode(null);
                    if (auditHardware is not null)
                    {
                        Program.hw = null!;
                        auditHardware.Dispose();
                    }
                    Program.UiAuditUseReportedCapabilities = false;
                    Application.DoEvents();
                }
            }
        }
        Application.ThreadException -= threadExceptionHandler;

        WriteJson(outputDirectory, screenshots.Count, issues, hostDpi);
        WriteMarkdown(outputDirectory, screenshots, issues);
        return issues.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// 运行审计的宿主显示器 DPI。自绘几何已由 UiDpi.Paint 固定到 viewport.Dpi，但窗体
    /// 布局仍要经过一次相对 form.Scale，所以把宿主 DPI 记进报告：两次审计结果如果对不上，
    /// 先看这个值是否相同，避免把宿主环境差异误判成 UI 回归。
    /// </summary>
    private static int ReadHostDpi()
    {
        try
        {
            using var probe = new Form();
            return UiDpi.Layout(probe);
        }
        catch
        {
            return UiDpi.Baseline;
        }
    }

    private static void WriteJson(string outputDirectory, int screenshotCount, IReadOnlyCollection<AuditIssue> issues, int hostDpi)
    {
        // Explicit JSON keeps the packaged audit independent of reflection metadata removed by obfuscation.
        using FileStream stream = File.Create(Path.Combine(outputDirectory, "ui-audit.json"));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("GeneratedAt", DateTimeOffset.Now);
        writer.WriteNumber("HostDpi", hostDpi);
        writer.WritePropertyName("Viewports");
        writer.WriteStartArray();
        foreach (var viewport in Viewports)
        {
            writer.WriteStartObject();
            writer.WriteNumber("Width", viewport.Width);
            writer.WriteNumber("Height", viewport.Height);
            writer.WriteNumber("Dpi", viewport.Dpi);
            writer.WriteString("Id", viewport.Id);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber("ScreenshotCount", screenshotCount);
        writer.WriteNumber("IssueCount", issues.Count);
        writer.WritePropertyName("Issues");
        writer.WriteStartArray();
        foreach (var issue in issues)
        {
            writer.WriteStartObject();
            writer.WriteString("Form", issue.Form);
            writer.WriteString("Viewport", issue.Viewport);
            writer.WriteString("Kind", issue.Kind);
            writer.WriteString("Control", issue.Control);
            writer.WriteString("Detail", issue.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void ScaleFonts(Control root, float ratio)
    {
        var fonts = Flatten(root).Select(control => (Control: control, Font: control.Font)).ToArray();
        foreach (var item in fonts)
        {
            Font old = item.Font;
            float size = Math.Max(1f, old.SizeInPoints * ratio);
            item.Control.Font = new Font(old.FontFamily, size, old.Style, GraphicsUnit.Point, old.GdiCharSet, old.GdiVerticalFont);
        }
    }

    private static void AuditForm(string formName, Viewport viewport, Form form, List<AuditIssue> issues)
    {
        if (form.Width > viewport.WorkingArea.Width || form.Height > viewport.WorkingArea.Height)
        {
            issues.Add(new AuditIssue(formName, viewport.Id, "window-overflow", form.Name,
                $"Window {form.Size} exceeds working area {viewport.WorkingArea.Size}."));
        }

        foreach (Control control in Flatten(form).Where(control => control.Visible))
        {
            if (control is ScrollableControl scrollable && scrollable.HorizontalScroll.Visible)
            {
                issues.Add(new AuditIssue(formName, viewport.Id, "horizontal-scroll", ControlPath(control),
                    $"Horizontal scrollbar is visible; display {scrollable.DisplayRectangle.Size}, client {scrollable.ClientSize}."));
            }

            if (control.Width <= 0 || control.Height <= 0)
            {
                issues.Add(new AuditIssue(formName, viewport.Id, "zero-size", ControlPath(control), control.Size.ToString()));
                continue;
            }

            Control? parent = control.Parent;
            // dashboardPageHost 是主面板的自绘滚动视口（AutoScroll 关闭后内容偏移是设计行为），
            // 与 AutoScroll 宿主同样豁免包含性检查。
            if (parent is not null && parent is not ScrollableControl { AutoScroll: true } && parent.Name != "dashboardPageHost")
            {
                Rectangle client = parent.ClientRectangle;
                if (control.Left < client.Left - 1 || control.Top < client.Top - 1 ||
                    control.Right > client.Right + 1 || control.Bottom > client.Bottom + 1)
                {
                    issues.Add(new AuditIssue(formName, viewport.Id, "parent-overflow", ControlPath(control),
                        $"Bounds {control.Bounds} outside parent client {client}."));
                }
            }

            string? clipping = GetTextClipping(control);
            if (clipping is not null)
                issues.Add(new AuditIssue(formName, viewport.Id, "text-clipping", ControlPath(control), clipping));
        }

        // 裁切检查独立于上面的豁免（AutoScroll 宿主 / TableLayoutPanel 父级），
        // 设置弹窗的「控制台」裁切正是被这两类豁免漏掉的。
        foreach (var finding in CheckClipping(form))
            issues.Add(new AuditIssue(formName, viewport.Id, finding.Kind, finding.Control, finding.Detail));

        foreach (Control parent in Flatten(form).Where(control => control.Visible && control is not TableLayoutPanel && control is not FlowLayoutPanel))
        {
            Control[] leaves = parent.Controls.Cast<Control>()
                .Where(control => control.Visible && control.Controls.Count == 0 && control.Width > 2 && control.Height > 2)
                .ToArray();
            for (int i = 0; i < leaves.Length; i++)
            {
                for (int j = i + 1; j < leaves.Length; j++)
                {
                    Rectangle overlap = Rectangle.Intersect(leaves[i].Bounds, leaves[j].Bounds);
                    if (overlap.Width > 3 && overlap.Height > 3)
                    {
                        issues.Add(new AuditIssue(formName, viewport.Id, "sibling-overlap", ControlPath(parent),
                            $"{DisplayName(leaves[i])} {leaves[i].Bounds} overlaps {DisplayName(leaves[j])} {leaves[j].Bounds}."));
                    }
                }
            }
        }
    }

    private static string? GetTextClipping(Control control)
    {
        if (string.IsNullOrWhiteSpace(control.Text)) return null;
        if (control is Label { AutoSize: true } || control is CheckBox { AutoSize: true } || control is RadioButton { AutoSize: true })
            return null;
        if (control is not Label && control is not ButtonBase) return null;

        int reserve = control is CheckBox or RadioButton ? SystemInformation.MenuCheckSize.Width + 8 : 8;
        int availableWidth = Math.Max(1, control.ClientSize.Width - control.Padding.Horizontal - reserve);
        int availableHeight = Math.Max(1, control.ClientSize.Height - control.Padding.Vertical - 4);
        TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        if (control is Label label && !label.AutoSize)
            flags |= TextFormatFlags.WordBreak;
        else
            flags |= TextFormatFlags.SingleLine;
        Size measured = TextRenderer.MeasureText(control.Text, control.Font, new Size(availableWidth, int.MaxValue), flags);
        return measured.Width > availableWidth + 2 || measured.Height > availableHeight + 2
            ? $"Text '{control.Text.Replace(Environment.NewLine, " / ")}' needs {measured}, available {availableWidth}x{availableHeight}."
            : null;
    }

    private static void AuditBrightnessIsolation(Viewport viewport, SettingsForm form, List<AuditIssue> issues)
    {
        form.SelectDashboardPageForAudit(0);
        Control? performance = form.Controls.Find("panelPerformance", true).FirstOrDefault();
        Control? gpu = form.Controls.Find("panelGPU", true).FirstOrDefault();
        TrackBar? brightness = form.Controls.Find("sliderScreenBrightness", true).FirstOrDefault() as TrackBar;
        if (performance is null || gpu is null || brightness is null) return;

        // Finish the page-selection paint before using it as the isolation baseline.
        Application.DoEvents();
        form.Update();
        performance.Update();
        gpu.Update();
        byte[] performanceBefore = RenderSignature(performance);
        byte[] gpuBefore = RenderSignature(gpu);
        int original = brightness.Value;
        foreach (int value in new[] { 20, 80, 35, 70, original })
        {
            brightness.Value = Math.Clamp(value, brightness.Minimum, brightness.Maximum);
            Application.DoEvents();
        }

        if (!performanceBefore.SequenceEqual(RenderSignature(performance)))
            issues.Add(new AuditIssue("Settings-Common", viewport.Id, "cross-control-repaint",
                ControlPath(performance), "Performance controls changed while only brightness was adjusted."));
        if (!gpuBefore.SequenceEqual(RenderSignature(gpu)))
            issues.Add(new AuditIssue("Settings-Common", viewport.Id, "cross-control-repaint",
                ControlPath(gpu), "GPU controls changed while only brightness was adjusted."));
    }

    private static byte[] RenderSignature(Control control)
    {
        using var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height), PixelFormat.Format32bppArgb);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static IEnumerable<string> CapturePages(string outputDirectory, string formName, Viewport viewport, Form form)
    {
        var scrollable = Flatten(form)
            .OfType<ScrollableControl>()
            .FirstOrDefault(control => control.Visible && control.AutoScroll && control.DisplayRectangle.Height > control.ClientSize.Height + 4);
        var pages = scrollable is null
            ? new[] { (Name: "full", Offset: 0) }
            : new[]
            {
                (Name: "top", Offset: 0),
                (Name: "middle", Offset: Math.Max(0, (scrollable.DisplayRectangle.Height - scrollable.ClientSize.Height) / 2)),
                (Name: "bottom", Offset: Math.Max(0, scrollable.DisplayRectangle.Height - scrollable.ClientSize.Height)),
            };

        foreach (var page in pages.Distinct())
        {
            if (scrollable is not null)
            {
                form.ActiveControl = null;
                scrollable.AutoScrollPosition = new Point(0, page.Offset);
                scrollable.PerformLayout();
                Application.DoEvents();
            }

            int width = Math.Max(1, form.Width);
            int height = Math.Max(1, form.Height);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            string fileName = $"{formName}-{viewport.Id}-{page.Name}.png";
            string path = Path.Combine(outputDirectory, fileName);
            bitmap.Save(path, ImageFormat.Png);
            yield return fileName;
        }
    }

    private static IEnumerable<Control> Flatten(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control descendant in Flatten(child))
                yield return descendant;
    }

    private static string DisplayName(Control control)
        => string.IsNullOrWhiteSpace(control.Name) ? control.GetType().Name : control.Name;

    /// <summary>
    /// 裁切类几何缺陷（独立于既有的 AutoScroll / TableLayoutPanel 豁免，不能被它们掩盖）：
    /// (a) 窗体根子控件 Dock=Top/Fill 且内容所需尺寸超出客户区，又没有可用滚动条；
    /// (b) TableLayoutPanel 的绝对列宽之和超出其内容区。
    /// 返回 (Kind, ControlPath, Detail)。
    /// </summary>
    internal static List<(string Kind, string Control, string Detail)> CheckClipping(Form form)
    {
        var findings = new List<(string Kind, string Control, string Detail)>();

        // (a) 停靠根溢出：只算不可压缩方向的内容需求（百分比行/列是有意的压缩通道，
        // 其内容由内部滚动视口兜底），超出客户区又没有可用滚动条才算裁切。
        foreach (Control root in form.Controls.Cast<Control>()
            .Where(control => control.Visible && (control.Dock == DockStyle.Top || control.Dock == DockStyle.Fill)))
        {
            (int requiredWidth, int requiredHeight) = RequiredContentSize(root);
            bool vertical = requiredHeight > form.ClientSize.Height + 2 && !HasUsableVerticalScroll(form, root);
            bool horizontal = requiredWidth > form.ClientSize.Width + 2 && !HasUsableHorizontalScroll(form, root);
            if (!vertical && !horizontal) continue;
            findings.Add(("docked-root-overflow", ControlPath(root),
                $"{DisplayName(root)} requires {requiredWidth}x{requiredHeight}, client {form.ClientSize.Width}x{form.ClientSize.Height} without a usable scrollbar."));
        }

        // (b) 绝对列溢出：绝对列之和超过内容区时，右侧列（如「控制台」按钮）被右缘裁掉。
        foreach (Control control in Flatten(form)
            .Where(control => control.Visible && control is TableLayoutPanel { Width: > 0 } tlp && tlp.ClientSize.Width > 0))
        {
            var tlp = (TableLayoutPanel)control;
            int absolute = 0;
            foreach (ColumnStyle style in tlp.ColumnStyles)
                if (style.SizeType == SizeType.Absolute) absolute += (int)Math.Ceiling(style.Width);
            int inner = tlp.ClientSize.Width - tlp.Padding.Horizontal;
            if (inner > 0 && absolute > inner)
                findings.Add(("absolute-column-overflow", ControlPath(tlp),
                    $"Absolute columns sum {absolute}px > inner width {inner}px."));
        }

        return findings;
    }

    /// <summary>根控件在不可压缩方向上的所需尺寸：TableLayoutPanel 跳过百分比行/列
    /// （那是有意的压缩/内部滚动通道），其余行/列按布局后的实际尺寸求和；非 TLP 用 PreferredSize。</summary>
    private static (int Width, int Height) RequiredContentSize(Control root)
    {
        if (root is not TableLayoutPanel tlp)
            return (root.PreferredSize.Width, root.PreferredSize.Height);

        int width = tlp.Padding.Horizontal;
        int height = tlp.Padding.Vertical;
        int[] columnWidths = tlp.GetColumnWidths();
        int[] rowHeights = tlp.GetRowHeights();
        for (int i = 0; i < columnWidths.Length; i++)
            if (i >= tlp.ColumnStyles.Count || tlp.ColumnStyles[i].SizeType != SizeType.Percent)
                width += columnWidths[i];
        for (int i = 0; i < rowHeights.Length; i++)
            if (i >= tlp.RowStyles.Count || tlp.RowStyles[i].SizeType != SizeType.Percent)
                height += rowHeights[i];

        // 没有显式行/列样式时 GetRowHeights 不可靠（隐式 AutoSize 行），退回 PreferredSize。
        if (tlp.ColumnStyles.Count == 0) width = root.PreferredSize.Width;
        if (tlp.RowStyles.Count == 0) height = root.PreferredSize.Height;
        return (width, height);
    }

    private static bool HasUsableVerticalScroll(Form form, Control root)
    {
        if (form.VerticalScroll.Visible) return true;
        if (root is ScrollableControl { AutoScroll: true } rootScroll && rootScroll.VerticalScroll.Visible) return true;
        // dashboardPageHost 等自绘滚动视口：RScrollBar 可见即内容可达（与既有豁免同一设计）。
        return Flatten(form).OfType<RScrollBar>().Any(bar => bar.Visible);
    }

    private static bool HasUsableHorizontalScroll(Form form, Control root)
    {
        if (form.HorizontalScroll.Visible) return true;
        return root is ScrollableControl { AutoScroll: true } rootScroll && rootScroll.HorizontalScroll.Visible;
    }

    private static string ControlPath(Control control)
    {
        var parts = new Stack<string>();
        for (Control? current = control; current is not null; current = current.Parent)
            parts.Push(DisplayName(current));
        return string.Join("/", parts);
    }

    private static void WriteMarkdown(string outputDirectory, IReadOnlyCollection<string> screenshots, IReadOnlyCollection<AuditIssue> issues)
    {
        var text = new StringBuilder()
            .AppendLine("# L-Mechrevo UI audit")
            .AppendLine()
            .AppendLine($"- Screenshots: {screenshots.Count}")
            .AppendLine($"- Geometry issues: {issues.Count}")
            .AppendLine($"- Matrix: {string.Join(", ", Viewports.Select(viewport => viewport.Id))}")
            .AppendLine();
        foreach (var issue in issues.Take(500))
            text.AppendLine($"- [{issue.Kind}] {issue.Form}/{issue.Viewport} `{issue.Control}`: {issue.Detail}");
        File.WriteAllText(Path.Combine(outputDirectory, "ui-audit.md"), text.ToString(), Encoding.UTF8);
    }

    static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in AllControls(child)) yield return nested;
        }
    }
}
