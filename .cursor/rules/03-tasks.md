# Tasks

Work in this order. Ask before skipping.

## Done
- [x] Scaffold Revit 2025 add-in (**.NET 8**, Nice3point Revit 2025 API refs, `.addin`)
- [x] Add `SharpGLTF.Toolkit`
- [x] `ExportGltfCommand`, `ExportXktCommand`, and `ExportIfcCommand`
- [x] Ribbon tab **Automation**, panel **Export**, buttons **glTF**, **XKT**, and **IFC** with logos
- [x] `VisibilityService`, `ColorResolver`, `GeometryBuilder`, `GltfExporter`, `XktConverter`, `IfcExporter`
- [x] Detect missing Node.js / xeokit-convert before XKT conversion
- [x] Project/assembly/add-in name **MetroCIM**. Setup display name **Export**. `dist\Export-Setup.exe`
- [x] IFC duct, pipe, and conduit fittings follow the connected run through the bend. Cable trays are a thin channel. glTF and XKT fittings use the placed solid

## Remaining
- [x] 1. Keep glTF, XKT, and IFC as separate ribbon commands; do not recombine them
- [x] 2. Install XKT runtime: Node.js + `npm install -g @xeokit/xeokit-convert`
- [x] 3. After code changes: `dotnet build -p:DeployAddin=true`, then restart Revit
- [ ] 4. Test a 3D view with a filter, an element override, and MEP system-type coloring
- [ ] 5. Confirm `.glb` opens in a glTF viewer and `.xkt` opens in a xeokit viewer
- [ ] 6. Confirm IFC export uses a saved/built-in Revit IFC setup

## Constraints
- No telemetry and no unprompted network calls (only the local xeokit-convert process)
- Revit 2025 API only
- `XktConverter` stays swappable if a native writer appears later
