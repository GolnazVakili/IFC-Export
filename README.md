# IFC Export (MetroCIM for Revit 2025)

Revit **2025** add-in that exports the **active 3D view** to **glTF**, **XKT**, and **IFC**, using the colors you see on screen (view filters, graphic overrides, and MEP system types).

![Mechanical room export with piping system colors](docs/images/sample-mechanical-room.png)

*Sample export of a mechanical room. Pipes, fittings, and accessories keep the same system colors as the Revit 3D view.*

## What it exports

The **Automation** ribbon tab has an **Export** panel with three commands:

| Command | Output |
| --- | --- |
| **Export glTF** | `.glb` / `.gltf` from the active 3D view |
| **Export XKT** | `.xkt` for xeokit (writes a sibling `.glb` and `metadata.json`, then converts) |
| **Export IFC** | `.ifc` using a Revit IFC setup (in-session, built-in, or saved in the model) |

glTF and XKT include only what is visible in the current 3D view. IFC uses the selected Revit IFC setup; MetroCIM then applies the same system-type / view colors so piping matches glTF.

## Requirements

- Autodesk **Revit 2025**
- For **Export XKT** only:
  - [Node.js](https://nodejs.org/)
  - `npm install -g @xeokit/xeokit-convert`

## Install

Build and copy the add-in into your Revit 2025 Addins folder:

```bat
dotnet build src/MetroCIM/MetroCIM.csproj -p:DeployAddin=true
```

That writes:

- `%AppData%\Autodesk\Revit\Addins\2025\MetroCIM.addin`
- `%AppData%\Autodesk\Revit\Addins\2025\MetroCIM\MetroCIM.dll`

Fully quit Revit before installing (it locks the DLL). Then start Revit and use the **Automation** tab.

## Use

1. Open a project and activate a **3D view** (glTF / XKT require this; IFC can use the active view).
2. Choose **Export glTF**, **Export XKT**, or **Export IFC**.
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
