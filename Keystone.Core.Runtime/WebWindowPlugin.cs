// WebWindowPlugin — built-in window plugin for config-declared web windows.
// titleBarStyle "toolkit": borderless, GPU title bar (close/minimize/float/tabs) + optional toolbar + web content.
// titleBarStyle "toolkit-native": titled window with native controls + GPU title bar (tabs/float only) + web content.
// titleBarStyle "hidden" (default): web content fills full window; native traffic lights from macOS.
// titleBarStyle "none": web content fills full window; no native chrome at all.
// Created automatically from keystone.json windows[] entries.

using Keystone.Core;
using Keystone.Core.Plugins;
using Keystone.Core.Rendering;
using Keystone.Core.UI;
using Keystone.Toolkit;

namespace Keystone.Core.Runtime;

public class WebWindowPlugin : WindowPluginBase
{
    private readonly WindowConfig _cfg;
    private readonly ButtonRegistry _buttons = new();

    public override string WindowType => _cfg.Component;
    public override string WindowTitle => _cfg.Title ?? _cfg.Component;
    public override (float Width, float Height) DefaultSize => (_cfg.Width, _cfg.Height);
    public override PluginRenderPolicy RenderPolicy => PluginRenderPolicy.OnEvent;

    public WebWindowPlugin(WindowConfig config)
    {
        _cfg = config;
    }

    public override void Render(RenderContext ctx) { }

    public override SceneNode? BuildScene(FrameState state)
    {
        if (_cfg.Renderless) return null;

        _buttons.Clear();
        var w = state.Width;
        var h = state.Height;

        var children = new List<SceneNode>();
        float contentY = 0;

        // GPU title bar only for "toolkit" mode (opt-in)
        if (_cfg.TitleBarStyle is "toolkit" or "toolkit-native")
        {
            var titleBar = TitleBar.Build(state, w);
            children.Add(new FlexGroupNode
            {
                Id = 10,
                Root = titleBar,
                X = 0, Y = 0, W = w, H = TitleBar.Height,
                Buttons = _buttons
            });
            contentY = TitleBar.Height;

            // Optional toolbar from config (toolkit mode only)
            if (_cfg.Toolbar?.Items.Count > 0)
            {
                var toolbar = BuildToolbar(state, w);
                children.Add(new FlexGroupNode
                {
                    Id = 20,
                    Root = toolbar,
                    X = 0, Y = contentY, W = w, H = Theme.StripHeight,
                    Buttons = _buttons
                });
                contentY += Theme.StripHeight;
            }
        }

        // Content area — web component filling remaining space
        var content = Flex.Column();
        content.FlexGrow = 1;
        content.BgColor = Theme.BgSurface;
        content.Child(Flex.Web(_cfg.Component));

        children.Add(new FlexGroupNode
        {
            Id = 30,
            Root = content,
            X = 0, Y = contentY, W = w, H = h - contentY,
            Buttons = _buttons
        });

        state.NeedsRedraw = false;
        return new GroupNode { Id = 1, Children = children.ToArray() };
    }

    private FlexNode BuildToolbar(FrameState state, float w)
    {
        var strip = Flex.Row(gap: 4, pad: Theme.PadX, align: FlexAlign.Center);
        strip.Height = Theme.StripHeight;
        strip.BgColor = Theme.BgStrip;

        foreach (var item in _cfg.Toolbar!.Items)
        {
            if (item.Type == "separator")
            {
                strip.Child(new FlexNode
                {
                    Width = 1, Height = Theme.StripHeight - 16,
                    BgColor = Theme.Divider
                });
                continue;
            }

            var btn = new FlexNode
            {
                Height = 26,
                BgColor = Theme.BgButton,
                HoverBgColor = Theme.BgButtonHover,
                BgRadius = Theme.CornerRadius,
                Action = item.Action,
                Padding = 8
            };

            if (item.Icon != null)
            {
                btn.Text = item.Icon;
                btn.FontSize = 14;
                btn.TextColor = Theme.TextPrimary;
                btn.TextAlign = TextAlign.Center;
                btn.Width = 28;
            }
            else if (item.Label != null)
            {
                btn.Text = item.Label;
                btn.FontSize = 13;
                btn.TextColor = Theme.TextPrimary;
                btn.Font = FontId.Regular;
            }

            strip.Child(btn);
        }

        return strip;
    }

    public override HitTestResult? HitTest(float x, float y, float width, float height)
        => _buttons.HitTest(x, y);

    public override string? SerializeConfig()
        => System.Text.Json.JsonSerializer.Serialize(_cfg);

    public override void RestoreConfig(string json)
    {
        // Config is immutable for web windows — set at creation from keystone.json
    }
}
