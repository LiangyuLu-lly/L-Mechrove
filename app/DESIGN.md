# L-Mechrevo · Tauri Web Design Contract

> Web mapping of root `DESIGN.md` + `UiVisualStyle` for the Tauri 2 webview.
> **Not a new visual language.** Deep Console only. Dated snapshot: `docs/superpowers/specs/2026-09-20-tauri-web-DESIGN.md`.
> No React in this file. Components consume these tokens; they do not invent hex, px, or footer keys.

**Source of truth:** root `DESIGN.md` (IA + taste) · `src/MechrevoLiteWin/UI/UiVisualStyle.cs` (Night/Day RGB).
**Shell:** width **420** logical px (`SettingsWidth`). Single column. No tabs. No card grid.

---

## 1. Atmosphere & Identity

Deep Console: native night cockpit, not a light UI inverted. Three tonal planes (Window / Surface / SurfaceRaised) carry hierarchy. Structure is 1px Border, never shadow or glass. One Accent (`#4DA3FF` night / `#1F6FE0` day) for the current control only.

Signature: a 420px instrument strip — row = function, Consolas telemetry, footer six linear keys.

Anti-slop (hard bans, same as root §1):

- no emoji icons (linear SVG only: Lucide/Phosphor matching `UiGlyph`)
- no AI purple, no gradient text, no glow borders, no glass / `backdrop-filter`
- no italic UI text, no Inter/Roboto/Space Grotesk, no webfonts
- no shadow stacks (depth = face + 1px stroke)
- no centered hero titles, no bento, no purple-on-white

---

## 2. Color

CSS variables. Hex from `UiVisualStyle` NightPalette / DayPalette. Never raw hex in components.

| Role | CSS | Night | Day | Usage |
|---|---|---|---|---|
| Window | `--window` | `#0B1220` | `#F2F5F9` | shell / row ground |
| Surface | `--surface` | `#111A2B` | `#FFFFFF` | title bar, footer, input chrome |
| SurfaceRaised | `--surface-raised` | `#16223A` | `#E9EFF6` | hover fill, raised input |
| Input | `--input` | `#0D1526` | `#FFFFFF` | segmented track |
| Border | `--border` | `#24344F` | `#C9D5E3` | only 1px structure stroke |
| Text | `--text` | `#E4ECF7` | `#16202E` | primary copy |
| Muted | `--muted` | `#8FA3BF` | `#5A6B80` | secondary; ≥4.5:1 on Surface |
| Accent | `--accent` | `#4DA3FF` | `#1F6FE0` | selected segment, slider fill, overlay-on |
| AccentText | `--accent-text` | `#08121F` | `#FFFFFF` | text on Accent |
| AccentHover | `--accent-hover` | `#6FB4FF` | `#1A5FC4` | primary press hover |
| AccentPressed | `--accent-pressed` | `#3A8CE6` | `#14509F` | primary press |
| AccentBlue | `--accent-blue` | `#74A8E8` | `#0066CC` | reserved; do not decorate |
| Ok | `--ok` | `#35C46B` | `#16783F` | status only |
| Warn | `--warn` | `#E8B339` | `#8B5F0F` | status only |
| Danger | `--danger` | `#F0584E` | `#BE1E2D` | status / destructive only |
| Track | `--track` | `#22304A` | `#D7E0EB` | slider rail |

```css
:root { color-scheme: dark; }
:root[data-theme="night"] { --window: #0B1220; --surface: #111A2B; --text: #E4ECF7; --muted: #8FA3BF; --accent: #4DA3FF; --border: #24344F; --danger: #F0584E; }
:root[data-theme="day"]   { --window: #F2F5F9; --surface: #FFFFFF; --text: #16202E; --muted: #5A6B80; --accent: #1F6FE0; --border: #C9D5E3; --danger: #BE1E2D; }
```

Rules: at most one Accent region visible (selected segment **or** primary button). Ok/Warn/Danger never as chrome. Theme from host `ui_theme_mode` (`night` / `day`); default night.

---

## 3. Typography

pt → CSS (96dpi, 1pt ≈ 1.333px). Root `html { font-size: 16px }`. Tokens only.

| Token | C# pt | px | rem | Weight | Use |
|---|---|---|---|---|---|
| `--type-caption` | 8.5 | **11** | `0.6875rem` | 400 | footer labels, dense assist |
| `--type-body` | 9 | **12** | `0.75rem` | 400 | default controls |
| `--type-subtitle` | 9.5 | **13** | `0.8125rem` | 600 | row titles |
| `--type-title` | 12 | **16** | `1rem` | 600 | dialog / window title |
| `--type-display` | 15 | **20** | `1.25rem` | 600 | first-run only |

- Body: `"Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif`
- Mono (temp / % / version / segment ticks): `"Consolas", "Cascadia Mono", ui-monospace, monospace` + `--muted`
- Segmented: CJK labels (静音模式 / 平衡模式 / 狂暴 / 自定义, 集显 / 标准 / 直连) use `--font-body` (`.segmented--body`). Numeric ticks (60 / 165) stay `--font-mono`.
- No italic. CJK + ASCII: half-width space (盘古之白).
- Exception (root §3): toast countdown 36px / `2.25rem` — not a scale step.

---

## 4. Spacing & Layout

Base 4px. `UiVisualStyle.Space` unchanged.

| Token | px | rem | Use |
|---|---|---|---|
| `--space-xs` | 4 | `0.25rem` | row gap |
| `--space-sm` | 8 | `0.5rem` | compact inline |
| `--space-md` | 12 | `0.75rem` | collapse group gap + footer pad-x |
| `--space-lg` | 16 | `1rem` | page inset-x |
| `--space-xl` | 24 | `1.5rem` | collapse body indent |
| `--fan-curve-height` | 160 | `10rem` | fan curve plot height |
| `--donate-height` | 420 | `26.25rem` | DonateDialog panel height |

- Shell: `width: 420px` (fixed). Height follows content; inner scroll. Max height = work area − 40px.
- No product breakpoints. This is a desktop instrument, not a responsive site.
- Row: title 26px, controls 32px, no card stroke, transparent on `--window`.
- Collapse groups: 1px `--border` full-width; default **collapsed** (液冷 / 灯光 / 更多开关); remember last.
- Segmented radius 8px; 3px inset; selected = `--accent` + `--accent-text` + 600.
- Slider: 4px `--track`, `--slider-fill` (`--accent`) from left to thumb via `--slider-progress` (0–100%), 14px thumb (white + 3px accent ring).
- `--row-value-min`: `4ch` — right-aligned live % so 70% and 100% share a column.
- Telemetry row indent: `--space-xl` (24px) — name column, not 42px.
- Fan curve plot: height `--fan-curve-height` (`10rem` / 160px).
- Switch: 38×20 track (`--input` + `--border`); on = `--accent` + white thumb.
- Footer: `--surface` + top 1px `--border`; height 51px; ghost keys 46×36; hover `--surface-raised`. Version label caps at `--footer-version-max` (96px) so the full release label (`0.289.0-beta18`) fits without ellipsis.
- Density 7/10. Values right-aligned, Consolas, `--muted`.

---

## 5. Components (contract only — do not implement here)

### Row

- Structure: 16px linear icon + subtitle name + muted status | control row
- States: default, disabled (capability off → **omit the row**, do not grey it)
- A11y: label associated with control; status is text, not color-only
- Motion: none on telemetry text updates
- Layout: stack; no independent scroll

### Segmented

- Variants: 2–4 segments
- States: default, hover (track only), selected (Accent), disabled, focus-visible 1px Accent
- Motion: 120ms ease-out fill; `prefers-reduced-motion`: instant
- One Accent fill at a time

### Slider / Switch

- States: default, hover, dragging (follow pointer, no spring), disabled, focus-visible
- Motion: 100ms hover; drag is 0ms

### FanCurve

- Structure: SVG polyline of 16 T0–T15 duty points; plot face `--surface-raised` + 1px `--border`; stroke `--accent`; numeric T0–T15 inputs stay the type-in path (same duties state)
- States: default, dragging (vertical only, follow pointer, 0ms), focus-visible 1px `--accent`
- A11y: each point `role="slider"` `aria-label="{CPU|GPU} T{n} 转速"` `aria-valuemin="0"` `aria-valuemax="100"` `aria-valuenow`; ArrowUp/Right +1, ArrowDown/Left −1, clamped 0–100
- Motion: none other than drag follow (0ms). No gradient, glow, or fill under the polyline
- Layout: full row width, height `--fan-curve-height`; axis ticks Consolas `--muted` `--type-caption`

### CollapseGroup

- Structure: ▸/▾ + name + muted summary; body indent `--space-xl`
- States: collapsed, expanded, hidden (capability false → unmount)
- Motion: **none** (root §7). Instant. Reduced-motion already satisfied.

### Footer (6 keys)

Left: version `--type-caption` `--muted`. Right, equal columns, locked order:

| # | Label | Glyph (linear, not emoji) | Command |
|---|---|---|---|
| 1 | 悬浮窗 | Overlay (circle-dot) | `overlay_set` — active = `--accent` icon+text |
| 2 | 设置 | Gear | settings dialog (`--settings-width` 420px, ≤ shell) |
| 3 | 更新 | Refresh | `updates_check` **or** snapshot `updateAvailable` — 有新版本 badge (accent pip, text in DOM, does not grow the 46×36 key) |
| 4 | **诊断** | Package | `diagnostics_export` — accessible name 导出诊断包 |
| 5 | 赞助 | Heart (stroke, not emoji) | donate — opens DonateDialog (title 赞助支持). No network. |
| 6 | 退出 | Close | quit / tray |

WinForms `BuildFooterV2` TLP currently places 赞助 before 诊断. **Tauri follows this table** (诊断 is 4th key). Ghost buttons: no border, ImageAboveText, `--type-caption`, hover `--surface-raised`.

Host snapshot event `hw_snapshot` is the live DTO. UI-read hub fields: `batteryHealth` / `chargeStatus` / `chargeFullOffered`, `overdrive` / `localDimming`, `customProfileOffered`, `lcConnection` (`none`\|`direct`\|`gcu`), `fanCurveTableName` (`M4T1`…), `updateAvailable`. Host errors surface on Toast via `onHostError`; never swallow.

### SettingsDialog

- Width `--settings-width` **420px** (≤ shell). `max-width: 100%`; overflow-y auto, never clip. Zones: 外观 / 悬浮窗. Overlay toggle lives only on footer 悬浮窗.
- 外观 maps C# 界面: theme **日间 / 夜间 only** (no 跟随系统). Language: **中文** is the working option. **English** stays visible but disabled — 暂不可用：尚无字符串表. Do not write `en`. Host key `lmechrevo.language` is left untouched.
- Official-console isolation is not on this surface. The official console must be uninstalled; the port does not isolate it.

### DonateDialog

- Overlay chrome = SettingsDialog (`--surface` + 1px `--border`, width `--settings-width`).
- Panel height `--donate-height` (420px). Title **赞助支持**. Body: 感谢支持 L-Mechrevo / 扫码赞助，支持持续开发.
- Two local QR images (`src/assets/qrcode1.jpg`, `qrcode2.jpg`). No QR fetch, no network.
- Missing file / `onError`: plain text **二维码资源缺失** on `--surface-raised`. Never a broken image.
- States: open, close (scrim click / 关闭赞助). Motion: none.

### UpdateDialog

- Overlay chrome = SettingsDialog. Title **检查更新**.
- Release notes block when the host supplies `releaseNotes` (`--input` face, 1px `--border`, `--type-body`).
- Determinate progress (`role="progressbar"`) while installing; value from host `updates_progress` event (0–100). Track `--track`, fill `--accent`, height `--slider-track-height`.
- Actions: 下载并安装 / 打开下载页 / 稍后 / 反馈. Install **disabled** unless a valid sha256 is cached (`hasValidSha256`). Never claim Authenticode.

### FirstRun

- **稍后**: dismiss only.
- **前往系统页**: dismiss + open SettingsDialog (not the same as 稍后).

---

## 6. Motion & Interaction

| Type | Duration | Easing | Use |
|---|---|---|---|
| Micro | 100ms | ease-out | hover |
| Select | 120ms | ease-out | segment / switch |
| Collapse | 0ms | — | expand/collapse |
| Drag | 0ms | — | slider thumb |

Animate only `transform` / `opacity` / `filter`. No layout animation. `prefers-reduced-motion: reduce` → all durations 0. Overlay-on is a state color change, not a pulse.

---

## 7. Depth & Surface

**Strategy: borders-only + tonal planes.** No shadows.

| Type | Value | Use |
|---|---|---|
| Default | `1px solid var(--border)` | footer top, collapse rules, segmented track |
| Face | `--window` / `--surface` / `--surface-raised` | three-step lift |

No glass, no blur, no glow.

---

## 8. Accessibility Constraints & Accepted Debt

### Constraints

- WCAG 2.2 AA. Body/caption contrast ≥4.5:1 (`--muted` on `--surface` is the floor).
- Visible `:focus-visible` on every key and control (1px `--accent`, not a thick ring).
- Full keyboard: 6 footer keys, segments, sliders, collapse headers.
- Caption 11px is small text → 4.5:1 required (not 3:1).
- Icon-only is forbidden; every footer key has a visible CJK label.
- `prefers-reduced-motion` respected (Section 6).

### Personas (operating layer)

- Primary: Mechrevo owner, Chinese UI, 420px panel beside a game.
- Situational: 150%/200% DPI; night room; one-hand. Do not hide footer keys to “clean up.”

### Accepted Debt

| Item | Location | Why | Exit |
|---|---|---|---|
| Body 12px / Caption 11px below generic 14px floor | type scale | Locked map from WinForms 9pt / 8.5pt; dense instrument | keep; contrast tests |
| Footer 6 keys in 420px | footer | Live product; 诊断 is required | clip with ellipsis, never drop a key |
| Collapse has no animation | CollapseGroup | Root §7 WinForms jitter; web stays instant for Plan A | optional 120ms later, not Plan A |

---

## Lighting visibility (fail-closed — G16)

UI consumes host `LightingVisibility` DTO. **Never MQTT Seen. Never topic presence. Empty MQTT is not hardware.**

| Channel | ItemSupport / FeatureMatrix bit | Missing / 0 | MQTT `HidLightbar/#` or `HidLightbar_Logo/#` |
|---|---|---|---|
| 灯条 | `LightbarSupport` (aliases LightBarSupport, HidLightbarSupport, IsLightbarSupport) | **hide row** | ignored |
| RGB 灯条 | `RGBLightbarSupport` | **hide row** | ignored |
| Logo | `LogoLightSupport` (aliases LogoLightbarSupport, HidLightbarLogoSupport, …) | **hide row** | ignored |
| 键盘 | `KeyboardSupport` / `KeyboardType > 0` (vendor-constant-on in FeatureMatrix) | hide keyboard row | N/A |

Fail-closed: absent registry value = unsupported. Do not OR with `LightbarStatusSeen` / `LogoLightStatusSeen` (that C# `SupportsLogoLight` OR is the G16 bug; web must not port it).

G16: ItemSupport Lightbar/RGB/Logo unset or false → 灯条 and Logo rows **absent**, even if the fake broker publishes nonempty HidLightbar status.

Collapse group 「灯光」: unmount if every remaining channel is false. Hinge / sync lightbars stay deleted.

Capability-hidden groups (液冷, 灯光, 更多开关) follow `RefreshDeviceCapabilities` / host `capabilities` event — same fail-closed rule.

---

## Shell IA (unchanged from root §0)

```
┌ L-Mechrevo ─ 420px ─────────────────── ─ □ ✕ ┐
│ 性能模式  [静音|办公|狂暴|自定义]              │
│ 风扇      CPU … · GPU …                       │
│ 显卡模式  [直连|混合|集显]                     │
│ 屏幕      [60|240|300]  亮度                   │
│ 电池      限充                                 │
│ ▸ 液冷    (ItemSupport LiquidCoolingSupport)   │
│ ▸ 灯光    (LightingVisibility, not MQTT)       │
│ ▸ 更多开关                                     │
│ ──────────────────────────────────────────── │
│ ver  悬浮窗  设置  更新  诊断  赞助  退出        │
└──────────────────────────────────────────────┘
```

---

## Do / Don't (web)

| Do | Don't |
|---|---|
| `var(--window)` / `var(--space-md)` / `var(--type-body)` | `#0B1220` or `13px` in components |
| 6 footer keys including 诊断 | 5-key footer; drop 诊断 to fit |
| Gate lights on ItemSupport | `if (mqtt.seen)` / empty payload ⇒ hardware |
| Linear SVG 16px `--muted` | emoji, filled cute icons, purple lucide defaults |
| 420px column | fluid marketing layout, Inter, glass cards |
