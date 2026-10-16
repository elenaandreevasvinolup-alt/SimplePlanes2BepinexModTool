// ============================================================
//  SP2 Pack — style & UI widgets
//
//  The whole window is built from UI Toolkit elements in code
//  (no .uss/.uxml). Colours, radii, spacing and the common
//  controls live here so the look stays in one place.
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sp2Pack
{
    public static class Sp2Style
    {
        // ---- Palette (dark, card based)
        public static readonly Color Bg      = new Color(0.145f, 0.149f, 0.161f);
        public static readonly Color CardBg  = new Color(0.196f, 0.204f, 0.220f);
        public static readonly Color CardHi  = new Color(0.239f, 0.247f, 0.267f);
        public static readonly Color Line    = new Color(0.294f, 0.306f, 0.333f);
        public static readonly Color Text    = new Color(0.878f, 0.886f, 0.902f);
        public static readonly Color Muted   = new Color(0.545f, 0.565f, 0.600f);
        public static readonly Color Accent  = new Color(0.357f, 0.780f, 0.980f);
        public static readonly Color Ok      = new Color(0.451f, 0.851f, 0.510f);
        public static readonly Color Warn    = new Color(0.949f, 0.718f, 0.302f);
        public static readonly Color Error   = new Color(0.925f, 0.353f, 0.353f);
        public static readonly Color Violet  = new Color(0.706f, 0.612f, 0.980f);

        public const int Pad = 8;
        public const int PadL = 12;
        public const int Radius = 6;

        // ---------------------------------------------------------------- containers

        public static VisualElement Root()
        {
            var root = new VisualElement();
            root.style.flexGrow = 1;
            root.style.backgroundColor = Bg;
            root.style.paddingTop = PadL;
            root.style.paddingBottom = PadL;
            root.style.paddingLeft = PadL;
            root.style.paddingRight = PadL;
            return root;
        }

        public static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        public static VisualElement Col()
        {
            var col = new VisualElement();
            col.style.flexDirection = FlexDirection.Column;
            return col;
        }

        public static VisualElement Card()
        {
            var c = new VisualElement();
            c.style.backgroundColor = CardBg;
            c.style.borderTopLeftRadius = Radius;
            c.style.borderTopRightRadius = Radius;
            c.style.borderBottomLeftRadius = Radius;
            c.style.borderBottomRightRadius = Radius;
            c.style.paddingTop = Pad;
            c.style.paddingBottom = Pad;
            c.style.paddingLeft = PadL;
            c.style.paddingRight = PadL;
            c.style.marginBottom = Pad;
            return c;
        }

        public static VisualElement Divider()
        {
            var d = new VisualElement();
            d.style.height = 1;
            d.style.backgroundColor = Line;
            d.style.marginTop = Pad;
            d.style.marginBottom = Pad;
            return d;
        }

        public static VisualElement Space(int px)
        {
            var s = new VisualElement();
            s.style.height = px;
            return s;
        }

        // ---------------------------------------------------------------- text

        public static Label Header(string text)
        {
            var l = new Label(text);
            l.style.fontSize = 13;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.color = Text;
            l.style.marginBottom = 4;
            l.style.marginTop = 2;
            return l;
        }

        public static Label Caption(string text)
        {
            var l = new Label(text);
            l.style.fontSize = 10;
            l.style.color = Muted;
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.marginBottom = 4;
            return l;
        }

        public static Label Body(string text)
        {
            var l = new Label(text);
            l.style.fontSize = 11;
            l.style.color = Text;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        public static Label Value(string text, Color color)
        {
            var l = new Label(text);
            l.style.fontSize = 12;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.color = color;
            return l;
        }

        public static Label Pill(string text, Color color)
        {
            var l = new Label(text);
            l.style.fontSize = 10;
            l.style.color = color;
            l.style.paddingLeft = 6;
            l.style.paddingRight = 6;
            l.style.paddingTop = 1;
            l.style.paddingBottom = 1;
            l.style.marginRight = 6;
            l.style.borderTopLeftRadius = 8;
            l.style.borderTopRightRadius = 8;
            l.style.borderBottomLeftRadius = 8;
            l.style.borderBottomRightRadius = 8;
            l.style.backgroundColor = new Color(color.r, color.g, color.b, 0.16f);
            return l;
        }

        // ---------------------------------------------------------------- controls

        public static Button Btn(string text, Action onClick, bool primary = false, Color? tint = null)
        {
            var b = new Button(onClick) { text = text };
            b.style.height = 24;
            b.style.fontSize = 11;
            b.style.marginRight = 6;
            b.style.marginBottom = 4;
            b.style.paddingLeft = 10;
            b.style.paddingRight = 10;
            Color c = tint ?? Accent;
            b.style.backgroundColor = primary ? new Color(c.r, c.g, c.b, 0.22f) : CardHi;
            b.style.color = primary ? c : Text;
            b.style.borderTopWidth = 0;
            b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0;
            b.style.borderRightWidth = 0;
            b.style.borderTopLeftRadius = 4;
            b.style.borderTopRightRadius = 4;
            b.style.borderBottomLeftRadius = 4;
            b.style.borderBottomRightRadius = 4;
            return b;
        }

        public static Toggle Check(string label, bool value, Action<bool> onChange)
        {
            var t = new Toggle(label) { value = value };
            t.style.fontSize = 11;
            t.style.color = Text;
            t.style.marginBottom = 2;
            t.RegisterValueChangedCallback(e => onChange(e.newValue));
            return t;
        }

        public static IntegerField IntField(string label, int value, Action<int> onChange)
        {
            var f = new IntegerField(label) { value = value };
            f.style.fontSize = 11;
            f.style.marginBottom = 2;
            f.RegisterValueChangedCallback(e => onChange(e.newValue));
            return f;
        }

        public static FloatField FloatField(string label, float value, Action<float> onChange)
        {
            var f = new FloatField(label) { value = value };
            f.style.fontSize = 11;
            f.style.marginBottom = 2;
            f.RegisterValueChangedCallback(e => onChange(e.newValue));
            return f;
        }

        public static Slider Slider(string label, float value, float min, float max, Action<float> onChange)
        {
            var s = new Slider(label, min, max) { value = value };
            s.style.fontSize = 11;
            s.style.marginBottom = 2;
            s.RegisterValueChangedCallback(e => onChange(e.newValue));
            return s;
        }

        public static DropdownField Dropdown(string label, List<string> choices, int index, Action<int> onChange)
        {
            var d = new DropdownField(label, choices, Mathf.Clamp(index, 0, Mathf.Max(0, choices.Count - 1)));
            d.style.fontSize = 11;
            d.style.marginBottom = 2;
            d.RegisterValueChangedCallback(e =>
            {
                int i = choices.IndexOf(e.newValue);
                if (i >= 0) onChange(i);
            });
            return d;
        }

        public static VisualElement Scroll()
        {
            var s = new ScrollView();
            s.style.flexGrow = 1;
            return s;
        }

        /// <summary>One "key — value" line: grey label on the left, value on the right.</summary>
        public static VisualElement KV(string key, string value, Color? valueColor = null)
        {
            var row = Row();
            row.style.marginBottom = 2;

            var k = new Label(key);
            k.style.fontSize = 11;
            k.style.color = Muted;
            k.style.minWidth = 96;

            var v = new Label(value);
            v.style.fontSize = 11;
            v.style.color = valueColor ?? Text;
            v.style.whiteSpace = WhiteSpace.Normal;

            row.Add(k);
            row.Add(v);
            return row;
        }

        /// <summary>Coloured notice box: accent bar on the left, text and an optional fix button.</summary>
        public static VisualElement Notice(string text, Color color, string fixLabel = null, Action onFix = null)
        {
            var box = new VisualElement();
            box.style.backgroundColor = new Color(color.r, color.g, color.b, 0.10f);
            box.style.borderLeftWidth = 3;
            box.style.borderLeftColor = color;
            box.style.paddingTop = 6;
            box.style.paddingBottom = 6;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            box.style.marginTop = 4;
            box.style.marginBottom = 4;

            var label = new Label(text);
            label.style.fontSize = 11;
            label.style.color = Text;
            label.style.whiteSpace = WhiteSpace.Normal;
            box.Add(label);

            if (onFix != null && !string.IsNullOrEmpty(fixLabel))
            {
                var btn = Btn(fixLabel, onFix);
                btn.style.marginTop = 5;
                btn.style.alignSelf = Align.FlexStart;
                box.Add(btn);
            }
            return box;
        }

        /// <summary>Horizontal share bar, used by the size preview to show the breakdown.</summary>
        public static VisualElement Bar(float fraction, Color color, int height = 8)
        {
            var track = new VisualElement();
            track.style.height = height;
            track.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);
            track.style.borderTopLeftRadius = 4;
            track.style.borderTopRightRadius = 4;
            track.style.borderBottomLeftRadius = 4;
            track.style.borderBottomRightRadius = 4;
            track.style.marginTop = 2;
            track.style.marginBottom = 4;

            var fill = new VisualElement();
            fill.style.height = height;
            fill.style.width = Length.Percent(Mathf.Clamp01(fraction) * 100f);
            fill.style.backgroundColor = color;
            fill.style.borderTopLeftRadius = 4;
            fill.style.borderTopRightRadius = 4;
            fill.style.borderBottomLeftRadius = 4;
            fill.style.borderBottomRightRadius = 4;
            track.Add(fill);
            return track;
        }

        // ---------------------------------------------------------------- formatting

        public static string Bytes(long b)
        {
            if (b < 1024) return b + " B";
            double kb = b / 1024.0;
            if (kb < 1024) return kb.ToString("0.0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.00") + " MB";
            return (mb / 1024.0).ToString("0.00") + " GB";
        }

        public static string Count(long n)
        {
            if (n < 1000) return n.ToString();
            if (n < 1000000) return (n / 1000.0).ToString("0.0") + "k";
            return (n / 1000000.0).ToString("0.00") + "M";
        }
    }
}
