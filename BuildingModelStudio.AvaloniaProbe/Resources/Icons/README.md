# Ribbon icons

Most SVGs come from [Lucide](https://github.com/lucide-icons/lucide), licensed under the ISC License. They retain the original geometry; only the stroke color is set to `#9ccfff` for the dark Ribbon. The icon names are the upstream filenames. `wall-plan.svg` is a custom plan-wall symbol for this application.

The matching transparent PNGs are mechanically rendered at 96px from the SVG sources and cached as bitmaps in the running UI, avoiding intermittent asynchronous SVG paint gaps. Keep the SVG as the editable source when changing an icon.

The opening editor uses the same library: `columns-2`, `rows-2`, `combine`,
`move-horizontal`, and `move-vertical` retain upstream Lucide geometry and the
existing ribbon stroke color. They distinguish vertical/horizontal subdivision,
merging, and width/height adjustment without relying on text glyphs.

Project-browser visibility and freezing use upstream Lucide `eye`, `eye-off`,
`lock-keyhole`, and `lock-keyhole-open` in the same stroke color and bitmap format.
