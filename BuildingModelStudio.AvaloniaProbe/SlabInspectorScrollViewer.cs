using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class SlabInspectorScrollViewer : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);
    private ScrollBar? _bar;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_bar != null) _bar.TemplateApplied -= OnBarTemplate;
        base.OnApplyTemplate(e);
        _bar = e.NameScope.Find<ScrollBar>("PART_VerticalScrollBar");
        if (_bar == null) return;
        _bar.TemplateApplied += OnBarTemplate;
        _bar.ApplyTemplate();
        Wire(_bar.GetVisualDescendants().OfType<Track>().LastOrDefault());
    }

    private void OnBarTemplate(object? sender, TemplateAppliedEventArgs e)
        => Wire(e.NameScope.Find<Track>("PART_Track"));

    private void Wire(Track? track)
    {
        if (_bar == null || track == null) return;
        var bar = _bar;
        void Bind(Control? control, Action action)
        {
            if (control is not RepeatButton button || Equals(button.Tag, "slab-page")) return;
            button.Tag = "slab-page";
            button.Click += (_, e) => { action(); e.Handled = true; };
        }
        Bind(track.DecreaseButton, bar.PageUp);
        Bind(track.IncreaseButton, bar.PageDown);
    }
}
