using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DotaArchive
{
    public static class ArchivePalette
    {
        public static readonly Color Background = Color.FromArgb(243, 245, 248);
        public static readonly Color Surface = Color.White;
        public static readonly Color Ink = Color.FromArgb(27, 41, 56);
        public static readonly Color Muted = Color.FromArgb(99, 116, 138);
        public static readonly Color Accent = Color.FromArgb(214, 83, 64);
        public static readonly Color Border = Color.FromArgb(222, 229, 238);
        public static readonly Color Header = Color.FromArgb(21, 39, 55);
    }

    internal static class ModernDrawing
    {
        internal static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            if(bounds.Width <= 0 || bounds.Height <= 0) return path;
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if(diameter <= 0) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static Color Blend(Color color, Color toward, float amount)
        {
            return Color.FromArgb(
                (int)Math.Round(color.R + (toward.R - color.R) * amount),
                (int)Math.Round(color.G + (toward.G - color.G) * amount),
                (int)Math.Round(color.B + (toward.B - color.B) * amount));
        }
    }

    public sealed class ModernButton : Button
    {
        private Color accentColor = ArchivePalette.Accent;
        private bool primary, danger, hovered, pressed;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = ArchivePalette.Ink;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Size = new Size(140, 42);
            Padding = new Padding(12, 0, 12, 0);
            Margin = new Padding(4);
            Cursor = Cursors.Hand;
        }

        public Color AccentColor
        {
            get { return accentColor; }
            set { accentColor = value; Invalidate(); }
        }

        public bool Primary
        {
            get { return primary; }
            set { primary = value; Invalidate(); }
        }

        public bool Danger
        {
            get { return danger; }
            set { danger = value; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // ButtonBase's themed/flat background painting is separate from our
            // rounded rendering. Explicitly paint every background pixel so an
            // otherwise untouched double-buffer does not leave black corners.
            if(Parent == null)
            {
                e.Graphics.Clear(ArchivePalette.Background);
                return;
            }
            GraphicsState state = e.Graphics.Save();
            try
            {
                e.Graphics.TranslateTransform(-Left, -Top);
                var clip = e.ClipRectangle;
                clip.Offset(Left, Top);
                var parentPaint = new PaintEventArgs(e.Graphics, clip);
                InvokePaintBackground(Parent, parentPaint);
                InvokePaint(Parent, parentPaint);
            }
            finally { e.Graphics.Restore(state); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if(Width < 2 || Height < 2) return;
            Control surface = Parent;
            while(surface != null && surface.BackColor.A != 255) surface = surface.Parent;
            e.Graphics.Clear(surface == null ? ArchivePalette.Background : surface.BackColor);
            float scale = e.Graphics.DpiX / 96F;
            Color accent = danger ? Color.FromArgb(189, 59, 49) : accentColor;
            Color fill = primary ? accent : ArchivePalette.Surface;
            Color border = primary ? accent : ArchivePalette.Border;
            Color ink = primary ? Color.White : (danger ? accent : ForeColor);
            if(!Enabled)
            {
                fill = Color.FromArgb(235, 239, 244);
                border = Color.FromArgb(226, 232, 239);
                ink = Color.FromArgb(145, 156, 170);
            }
            else if(pressed)
            {
                fill = primary ? ModernDrawing.Blend(accent, Color.Black, 0.14F) : ModernDrawing.Blend(ArchivePalette.Surface, accent, 0.13F);
                border = accent;
            }
            else if(hovered)
            {
                fill = primary ? ModernDrawing.Blend(accent, Color.Black, 0.06F) : ModernDrawing.Blend(ArchivePalette.Surface, accent, 0.055F);
                border = primary ? fill : ModernDrawing.Blend(ArchivePalette.Border, accent, 0.4F);
            }
            GraphicsState state = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float inset = Math.Max(0.5F, scale * 0.5F);
                var bounds = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
                using(var path = ModernDrawing.RoundedRectangle(bounds, 8F * scale))
                using(var brush = new SolidBrush(fill))
                using(var pen = new Pen(border, Math.Max(1F, scale)))
                {
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(pen, path);
                }
                if(Focused && ShowFocusCues && Enabled)
                {
                    float focusInset = 3.5F * scale;
                    var focusBounds = new RectangleF(focusInset, focusInset, Width - focusInset * 2 - 1, Height - focusInset * 2 - 1);
                    using(var path = ModernDrawing.RoundedRectangle(focusBounds, 5F * scale))
                    using(var pen = new Pen(primary ? Color.White : accent, Math.Max(1F, scale)))
                    {
                        pen.DashStyle = DashStyle.Dot;
                        e.Graphics.DrawPath(pen, path);
                    }
                }
            }
            finally { e.Graphics.Restore(state); }

            var textBounds = new Rectangle(Padding.Left, Padding.Top,
                Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            if(!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            else if(!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            if(RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ink, flags);
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if(e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if(!Capture) pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if(e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { pressed = false; hovered = false; Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    }

    public sealed class SurfacePanel : Panel
    {
        private Color borderColor = ArchivePalette.Border;
        private int cornerRadius = 12;

        public SurfacePanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = ArchivePalette.Surface;
            Padding = new Padding(16);
        }

        public Color BorderColor
        {
            get { return borderColor; }
            set { borderColor = value; Invalidate(); }
        }

        public int CornerRadius
        {
            get { return cornerRadius; }
            set { cornerRadius = Math.Max(0, value); Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Paint the actual parent surface behind the rounded corners. Filling
            // the whole rectangle with this panel's BackColor hides its rounding.
            if(Parent == null)
            {
                e.Graphics.Clear(ArchivePalette.Background);
                return;
            }
            GraphicsState state = e.Graphics.Save();
            try
            {
                e.Graphics.TranslateTransform(-Left, -Top);
                var clip = e.ClipRectangle;
                clip.Offset(Left, Top);
                // The wrapper borrows e.Graphics; its lifetime belongs to the
                // current paint event, so it must not be disposed here.
                var parentPaint = new PaintEventArgs(e.Graphics, clip);
                InvokePaintBackground(Parent, parentPaint);
                InvokePaint(Parent, parentPaint);
            }
            finally { e.Graphics.Restore(state); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if(Width >= 2 && Height >= 2)
            {
                GraphicsState state = e.Graphics.Save();
                try
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    float scale = e.Graphics.DpiX / 96F;
                    float inset = Math.Max(0.5F, scale * 0.5F);
                    var bounds = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
                    using(var path = ModernDrawing.RoundedRectangle(bounds, cornerRadius * scale))
                    using(var brush = new SolidBrush(BackColor))
                    using(var pen = new Pen(borderColor, Math.Max(1F, scale)))
                    {
                        e.Graphics.FillPath(brush, path);
                        if(borderColor.A != 0) e.Graphics.DrawPath(pen, path);
                    }
                }
                finally { e.Graphics.Restore(state); }
            }
            base.OnPaint(e);
        }
    }

    public sealed class ModernTabs : TabControl
    {
        private bool updatingMetrics;

        public ModernTabs()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(130, 42);
            Padding = new Point(16, 4);
            ForeColor = ArchivePalette.Ink;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateTabMetrics();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            UpdateTabMetrics();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateTabMetrics();
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            UpdateTabMetrics();
        }

        private void UpdateTabMetrics()
        {
            if(!IsHandleCreated || updatingMetrics || IsDisposed || Disposing) return;
            updatingMetrics = true;
            try
            {
                // The native tab item size is in device pixels. Always derive
                // it from a fixed logical baseline instead of multiplying the
                // previous item size during each font or DPI transition.
                float scale = DeviceDpi / 96F;
                int horizontalPadding = (int)Math.Round(28F * scale);
                int width = (int)Math.Round(130F * scale);
                foreach(TabPage page in TabPages)
                    width = Math.Max(width, TextRenderer.MeasureText(page.Text, Font,
                        Size.Empty, TextFormatFlags.SingleLine).Width + horizontalPadding);
                int height = Math.Max((int)Math.Round(42F * scale), Font.Height + (int)Math.Round(16F * scale));
                Size next = new Size(width, height);
                if(ItemSize != next) ItemSize = next;
                Padding = new Point((int)Math.Round(16F * scale), (int)Math.Round(4F * scale));
                Invalidate();
            }
            finally { updatingMetrics = false; }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if(e.Index < 0 || e.Index >= TabPages.Count) return;
            bool selected = e.Index == SelectedIndex;
            Rectangle bounds = GetTabRect(e.Index);
            using(var brush = new SolidBrush(selected ? ArchivePalette.Surface : ArchivePalette.Background))
                e.Graphics.FillRectangle(brush, bounds);
            Color ink = !Enabled ? Color.FromArgb(145, 156, 170) : selected ? ArchivePalette.Ink : ArchivePalette.Muted;
            Rectangle textBounds = Rectangle.Inflate(bounds, -8, -3);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            if(!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            if(RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
            TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, textBounds, ink, flags);
            if(selected)
            {
                int lineHeight = Math.Max(2, (int)Math.Round(3F * e.Graphics.DpiY / 96F));
                using(var brush = new SolidBrush(Enabled ? ArchivePalette.Accent : ArchivePalette.Border))
                    e.Graphics.FillRectangle(brush, bounds.Left + 10, bounds.Bottom - lineHeight, Math.Max(0, bounds.Width - 20), lineHeight);
                if(Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -5, -5), ink, ArchivePalette.Surface);
            }
            base.OnDrawItem(e);
        }
    }
}
