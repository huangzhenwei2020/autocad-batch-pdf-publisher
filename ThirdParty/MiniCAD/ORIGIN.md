# MiniCAD source provenance

Source is the MiniCAD document, editor, scene, text, viewport, serialization, node and D3D11 rendering subset of https://github.com/CHMOSE023/MiniCAD at commit `9740e7fbc288f1ea5a71af2b89503fce1496389c` (MiniCAD 0.8.4). This directory supersedes the initial MiniDWG-only subset.

MiniCAD copyright and MIT license are retained in LICENSE. ACadSharp provenance and MIT license are retained in THIRD_PARTY_NOTICES.md. This subset uses the upstream source and generated data model with its original CMake library definitions. The surrounding application, fonts, solid kernel and test assets are outside this source subset.

Wanluo's read-only diagnostic adapter is in DrawingReader. The persistent document, editor and D3D11 viewport bridge is in CadKernel. Local patches add a system TrueType display fallback for unavailable SHX files, default new drawings to millimetres, and preserve insertion units/base points across format changes. Original text-style metadata stays in the source document. SMLib and upstream font assets are excluded.

Interaction patches on 2026-11-13 add click-release-click window/crossing selection, additive selection with Shift removal, and a shared selection phase for command-first Move/Copy/Rotate/Scale/Mirror/Erase/Explode. Enter/Space finish line and polyline input. Circle, rectangle and three-point arc finish after one valid construction. Esc cancels transient selection/tool state while command-stack edits remain available to Undo.

A follow-up fix makes rectangle selection uniformly two-click. Empty-space button-down anchors the first corner, movement with either button state updates the preview, button-up retains it, and only the next button-down commits. The former 2-pixel drag threshold and release-to-commit branch are removed. Point selection uses the press location so small motion before release preserves the clicked target.

Cold-start document creation removes the renderer-presence guard in DocumentManager.New. Document and layout creation can run before attaching a native viewport. The Wanluo bridge establishes the new drawing's default units, font and view before marking that initial blank state clean.

The later 2026-11-13 tool batch adds native dimension export and semantic round-trip restoration, table and jogged-dimension compatibility blocks, table picking, hatch parameter recovery, paper-space ownership for new text, layout undo activation checks, and editor parameter/dialog hooks. The WPF forms and C ABI adapter remain in StandaloneWorkbench and CadKernel. These changes extend the vendored subset while retaining the upstream license and provenance.
