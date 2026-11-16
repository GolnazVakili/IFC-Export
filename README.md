# IFC Export (MetroCIM for Revit 2025)

Revit **2025** add-in that exports the **active 3D view** to **glTF**, **XKT**, and **IFC**, using the colors you see on screen.

![Mechanical room export with piping system colors](docs/images/sample-mechanical-room.png)

*Sample export of a mechanical room. Pipes, fittings, and accessories keep the same system colors as the Revit 3D view.*

## What it exports

The **Automation** ribbon tab has an **Export** panel. Each button shows its logo above the name:

| Button | Output |
| --- | --- |
| **glTF** | `.glb` / `.gltf` from the active 3D view |
| **XKT** | `.xkt` for xeokit (writes a sibling `.glb` and `metadata.json`, then converts) |
| **IFC** | `.ifc` using a Revit IFC setup (in-session, built-in, or saved in the model) |

glTF and XKT include what is visible in the current 3D view, plus pipe, duct, cable tray, and conduit fittings that sit in that view. A cable tray run uses the same geometry as the 3D view. A cable tray fitting uses that view solid when the view already shows it; otherwise the elbow, tee, or cross is built from its connectors at the tray width and height. Other fittings are exported as their placed solid, including parts nested inside the family. IFC uses the selected Revit IFC setup and the same colors as glTF: view filter, surface override, system type, then instance material, type material, and the material inside the family. Cable tray runs and fittings are written as a thin open channel, about 8 mm thick, instead of a filled box. A curved fitting follows that curve, and its profile matches the tray. The same view colors are applied to all three formats.

Color order, after view filters, surface overrides, and the MEP system type:

1. Instance material, including paint.
2. Type material, including the pipe segment material.
3. Material inside the family. The largest visible area wins.
4. Category material, then neutral gray.

## Requirements

- Autodesk **Revit 2025**
- For **Export XKT** only:
  - [Node.js](https://nodejs.org/)
  - `npm install -g @xeokit/xeokit-convert`

## Install

Run `dist\Export-Setup.exe`. Fully quit Revit first (it locks the add-in DLL). The setup copies:

- `%AppData%\Autodesk\Revit\Addins\2025\MetroCIM.addin`
- `%AppData%\Autodesk\Revit\Addins\2025\MetroCIM\MetroCIM.dll`

Start Revit and use the **Automation** tab, **Export** panel.

Rebuild the setup from the current Release build with:

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
```

That writes `dist\Export-Setup.exe`. From a developer checkout you can also copy the add-in without packaging:

```bat
dotnet build src/MetroCIM/MetroCIM.csproj -p:DeployAddin=true
```

## Use

1. Open a project and activate a **3D view** (glTF / XKT require this; IFC can use the active view).
2. Choose **glTF**, **XKT**, or **IFC**.
3. For IFC, pick a Revit IFC setup, then choose the output path.

If Node.js or xeokit-convert is missing, XKT export stops and shows the install command above.

## Build

```bat
dotnet build src/MetroCIM/MetroCIM.csproj
dotnet test src/MetroCIM.Tests/MetroCIM.Tests.csproj
```

The project targets `net8.0-windows` (x64) with the Revit 2025 API.

## Repository

https://github.com/GolnazVakili/IFC-Export
