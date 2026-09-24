# 登录页设计验收记录

## Source of truth

- 参考图：`C:\Users\wager\AppData\Local\Temp\codex-clipboard-7600241b-4855-4336-8176-e279eb9ae6ba.png`
- 实现页面：`src\SCFA.ContentCenter\Views\LoginWindow.xaml`
- 正式背景：`src\SCFA.ContentCenter\Assets\command_deck_background.png`
- 背景差异说明：保留项目中用户已确认的星球/机甲正式背景，只复刻参考图的布局、层级、控件和玻璃卡片风格，没有重绘或修改背景图片内容。

## Rendering evidence

| Evidence | Viewport | Rendered pixels | Density | State |
|---|---:|---:|---:|---|
| `artifacts\ui-dev33\login-1040x660.png` | 1040 × 660 DIP | 1040 × 660 px | 96 DPI / 1.0x | 登录模式；账号和密码已填；记住账号开启 |
| `artifacts\ui-dev33\login-900x600.png` | 900 × 600 DIP | 900 × 600 px | 96 DPI / 1.0x | 登录模式；账号和密码已填；记住账号开启 |
| `C:\Users\wager\Documents\Codex\2026-08-21\tiao\login-design-qa\login-comparison.png` | 1040 × 660 each | 2080 × 700 px | 96 DPI / 1.0x | 参考图与实际 WPF 渲染并排比较 |

## Comparison history

1. 第一次实机截图发现 1040 × 660 下卡片出现纵向滚动条，窗口按钮和密码可见图标字体不正确。
   - 修复：收紧卡片间距，窗口控制和密码图标统一改用 Segoe MDL2 Assets。
2. 第二次检查发现 900 × 600 下三个登录选项换行并导致滚动条。
   - 修复：三个选项改为等宽三列布局，并进一步收紧垂直间距。
3. 最终 RenderTargetBitmap 真实 WPF 渲染：1040 × 660 与 900 × 600 均无滚动条、重叠、裁切或文字拥挤。

## Fidelity review

- Typography：通过。品牌、页面标题、字段标签、辅助文字的层级和字重与参考图一致，中文未截断。
- Spacing and layout：通过。左右分栏、右侧登录卡片、输入区、操作按钮和底部注册提示的比例匹配；最小窗口仍完整显示。
- Colors and tokens：通过。使用现有设计系统资源与克制的蓝色主操作色；玻璃卡片对比度稳定。
- Image quality：通过。正式 PNG 使用 `UniformToFill`，未平铺、拉伸变形、模糊或重绘。
- Copy：通过。客户可见文案简洁，未暴露开发者接口或内部术语。
- Interaction coverage：通过。账号下拉、多账号记忆、密码显示、记住账号、记住密码、自动登录、回车登录、离线使用、注册切换及窗口控制均保留。
- Focused regions：账号/密码输入、三项登录选项、主次按钮和底部注册链接在两种尺寸下逐项检查，无 P0/P1/P2 视觉缺陷。

## Intentional differences

- 参考图只有“自动登录”，实际产品保留已存在且用户要求的“记住账号 / 记住密码 / 自动登录”三项功能。
- 未添加没有后端支持的“忘记密码”入口，避免展示不可用操作。
- 背景使用用户此前确认并纳入项目的正式星球/机甲图，而不是参考图中的战术线路底图。

final result: passed
