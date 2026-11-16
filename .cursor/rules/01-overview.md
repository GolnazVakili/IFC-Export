# Revit 2025 Add-in: MetroCIM

## Goal
Export the **active 3D view** so glTF/XKT match what is on screen, and export **IFC** using a Revit IFC setup.

glTF / XKT:
- Currently visible elements (view visibility, temporary hide/isolate, category/workset), plus pipe, duct, cable tray, and conduit fittings that sit in the view
- Cable tray runs use the 3D view geometry. Fittings use the placed Fine solid, including nested family parts
- View filter colors, patterns, and transparency
- Element and category graphic overrides
- MEP system type, then instance material, type material, and the material inside the family

## Stack
- Revit 2025 API, C#, **.NET 8** (`net8.0-windows`)
- `IExternalApplication` ribbon tab **Automation**
- glTF via **SharpGLTF.Toolkit** (NuGet)
- XKT via **xeokit-convert** (Node.js CLI): glTF → `.xkt`
  - Install: `npm install -g @xeokit/xeokit-convert`
  - The XKT command calls this CLI with `Process.Start`
  - If Node.js or xeokit-convert is missing, XKT export stops and shows the install hint
- IFC via Revit's `Document.Export` and the official IFC export setups (in-session, built-in, and saved in the model)
- Installer: `installer\Build-Installer.ps1` writes `dist\Export-Setup.exe`

## Ribbon
Tab **Automation**, panel **Export**. Logo above the label:
- **glTF** — writes `.glb` or `.gltf` only
- **XKT** — writes intermediate `.glb` and `metadata.json` next to the chosen `.xkt`, then converts
- **IFC** — writes `.ifc` using the selected Revit IFC setup. Fitting categories are set to Fine for that export only; cable tray runs stay on the view’s detail level

## Non-goals
- No import
- No native C# XKT writer
- No 2D views or sheets for glTF/XKT
