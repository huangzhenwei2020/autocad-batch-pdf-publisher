# Repository Working Context

For building-model, door/window, CAD/3D interop, or component-library work, first read
`docs/CURRENT_BUILDING_MODEL_CONTEXT.md` and its linked handoff. They supersede older
design-document workflows, especially manual CAD-plan export/import.

- Keep the product version at 1.5.6 unless the user requests a version change.
- CAD and Studio use one local component catalog and one stable resource ID. CAD
  stores and inserts plans; Studio adds the model to the same entry. Do not create
  a second catalog or require the user to re-import the CAD plan.
- Preserve existing parameter doors/windows, project templates, semantic model
  data, and standard-storey instances. External-resource placement, variable sizing,
  and motion are not yet implemented; do not present them as completed features.
- Follow the approved library grid design and existing Ribbon/property-panel UI.
  Part thumbnails must depict actual geometry, not placeholder boxes.
- Applied edits survive Esc; Ctrl+Z is undo. A grip drop commits without an extra
  Enter/Space. During opening movement, right-click centers along the wall.
- Do not overwrite a running CAD or model program. Save/close confirmation and
  a process check precede installation; back up and update the original directory.
- Never include user DWGs, model projects, local settings, credentials, `.artifacts`,
  `.tools`, or `dist` in a source/context upload without a specific request.
- Build/install guidance: `docs/BUILD_AND_INSTALL.md`. Release entry point:
  `build/Build-Release.ps1`. UI tests need a Windows desktop session; native CAD
  checks use a disposable drawing and temporary side database.
