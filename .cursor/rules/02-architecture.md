# Architecture

Keep types small: `VisibilityService`, `ColorResolver`, `MepSystemTypeColor`, `GeometryBuilder`, `GltfExporter`, `XktConverter`, `ViewExporter`, `MetadataExporter`, `IfcExporter`, `IfcAppearanceApplier`, `IfcColorPatcher`, `FlowFittingProxies`, `DuctFittingPath`. Do not merge XKT conversion into the glTF writer.

## Commands
- `ExportGltfCommand` — validate `View3D`, pick `.glb`/`.gltf`, build meshes, save glTF
- `ExportXktCommand` — same mesh pipeline, require xeokit-convert first, pick `.xkt`, write sibling `.glb` and `metadata.json`, convert with `-m`
- `ExportIfcCommand` — any open view, pick a Revit IFC setup, pick `.ifc`, `Document.Export` with that setup's options

Shared UI lives in `CommandUi`. Shared mesh + glTF write lives in `ViewExporter`.

## Visibility
`FilteredElementCollector(doc, view.Id)` + `VisibleInViewFilter`. Then skip:
- Hidden elements (`element.IsHidden(view)`)
- Hidden categories (`view.GetCategoryHidden`)
- Temporary hide/isolate (`view.IsTemporaryHideIsolateActive()` and `IsElementVisibleInTemporaryViewMode`)
- Hidden worksets
- Non-model categories, types, and `RevitLinkInstance`

Then add pipe, duct, cable tray, and conduit runs, fittings, and accessories that pass the same checks. A fitting with no view bounding box is still included when its model bounding box intersects the view crop.

## Color (3D shaded, last matching filter wins)
Use **surface / fill** colors, never projection-line color, as mesh albedo.

1. View filter surface overrides — `GetOrderedFilters()`, `GetFilterOverrides`
2. Element **surface** overrides (category overrides are skipped for pipes and for every fitting and accessory so they cannot hide system-type material)
3. **`MepSystemTypeColor`**: system-type material (never the fitting, accessory, or segment/type material), then system FillColor, then LineColor
4. Material shading color, only when no earlier step produced a color. Use the instance material first (instance material parameters, then paint). If the instance has no material, use the type material (type material parameters, then the pipe segment material). If neither has a material, use the material inside the family: the shading color on the faces and meshes, including a nested family, and the largest visible area wins. Category material, then neutral gray, when the element still has no material.

View filters are tested on the element the filter is assigned to. Every fitting and accessory (pipe, duct, conduit, cable tray, and any category whose name contains "Fitting" or "Accessory") uses that same order and does not inherit a host color before the filter test. `PipeInsulation` / other `InsulationLiningBase` elements inherit appearance from `HostElementId` only after their own filter misses. Fittings and accessories use the connected system type when they have no system parameter of their own.

## Geometry and glTF
- `element.get_Geometry(new Options { View = view })`
- Cable tray runs stay on that view geometry. Do not replace them with a Fine solid
- Pipe, duct, and conduit runs use the view mesh. If it is empty or paper-thin, use the placed Fine solid
- Pipe, duct, and conduit fittings use the placed Fine solid, including nested family parts, and fall back to the view mesh only when that body is empty
- A cable tray fitting uses the view solid when that view already shows one. Otherwise the elbow, tee, or cross is built from its connectors at the tray width and height, so the corner matches the straight run
- Triangulate solids/meshes (Coarse 0.50, Medium 0.60, Fine 0.80), feet → meters, Z-up → glTF Y-up
- Group by resolved color; split large color batches so xeokit-convert can process them; `SceneBuilder` → `SaveGLB` / `SaveGLTF`

## XKT
Resolve `node` + `convert2xkt.js` (or `xeokit-convert` on PATH). Write the same color-batched glTF as Export glTF, plus xeokit `metadata.json` (metaObjects + propertySets) next to the `.xkt`. Run:

```text
node --max-old-space-size=16384 convert2xkt.js -s "<glb>" -o "<xkt>" -m "<metadata.json>" -e 1
```

Detect the CLI before conversion. Missing tool message: `npm install -g @xeokit/xeokit-convert`.

## IFC
Load Revit's IFC setups from `Autodesk.IFC.Export.UI` or `Revit.IFC.Export` (in-session, built-in, and document-saved when those can be read). Missing saved setups must not block the built-in list. Before `document.Export`, apply the same `ColorResolver` colors used by glTF: view filter, surface override, MEP system type, then instance material, type material, and the material inside the family. After export, patch `IfcStyledItem` / `IfcColourRgb` by IFC GlobalId (22-char) or Revit UniqueId, never by Mark/ElementId. Shared fitting `IfcMappedItem` geometry is cloned per color so elbows on different systems do not keep the family metal style. Snapshot the IFC, then roll back temporary materials. Export with the file name **without** extension, `ActiveViewId` as the numeric view id, and tessellation **0.8**. If Revit rejects that detail because a curve is shorter than `Application.ShortCurveTolerance`, retry at **0.5** and then **0.25**, and tell the user which tessellation was written. Retry without `FilterViewId` if the file is empty. For that export only, set the view detail level to Fine and set duct, pipe, and conduit runs to Fine, turn on solid model representation, and regenerate so Revit's IFC exporter writes those run solids. Then restore the view. Duct, pipe, and conduit fittings are replaced for that export only with the connected run's cross-section. End connectors only are used. The solid follows those connectors through the bend, including a riser, and each end reaches the connected run so the joint is closed. The family insertion point is not the bend. A straight fitting stays straight. The original fitting is hidden and marked DontExport so its family solid is not written beside the stand-in. Cable tray runs stay on the view’s stored detail level. Cable tray runs and fittings are replaced for that export only with a thin open channel (about 8 mm wall), not a filled box. A curved fitting follows that curve; a straight fitting stays straight. The open section turns with the bend, so an L stays a thin channel instead of a filled solid. The profile matches the connected tray, and each end reaches into the run so the joint is closed. Hidden originals are left out of the IFC, so the filled family solid is not written with the thin channel. The stand-ins use the fitting or run color and are deleted when the export transaction ends. glTF and XKT do the same: fittings use the placed Fine solid, including nested family parts, and a cable tray run uses `get_Geometry` for that view.
