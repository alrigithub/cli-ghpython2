#! python 3
import Rhino
import Rhino.Geometry as rg
import Rhino.DocObjects as rd

debug_lines = []

def log(message):
    debug_lines.append(str(message))

def as_list(value):
    if value is None:
        return []
    if isinstance(value, list) or isinstance(value, tuple):
        return list(value)
    return [value]

def as_bool(value, fallback):
    if value is None:
        return fallback
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() in ("1", "true", "yes", "y")

def extract_values(param_index):
    comp = ghenv.Component
    param = comp.Params.Input[param_index]
    results = []
    for pi in range(param.VolatileData.PathCount):
        branch = param.VolatileData.get_Branch(pi)
        for goo in branch:
            if goo is None:
                continue
            geo = getattr(goo, "Value", None)
            if geo is not None:
                results.append(geo)
                continue
            if hasattr(goo, "ScriptVariable"):
                results.append(goo.ScriptVariable())
    return results

def extract_geo(param_index):
    results = []
    for value in extract_values(param_index):
        if isinstance(value, rg.GeometryBase):
            results.append(value)
            continue
        if isinstance(value, System.Guid):
            rhobj = Rhino.RhinoDoc.ActiveDoc.Objects.FindId(value)
            if rhobj:
                results.append(rhobj.Geometry)
    return results

def as_point(value):
    if isinstance(value, rg.Point3d):
        return value
    if isinstance(value, rg.Point):
        return value.Location
    if isinstance(value, System.Guid):
        rhobj = Rhino.RhinoDoc.ActiveDoc.Objects.FindId(value)
        if rhobj:
            return as_point(rhobj.Geometry)
    if isinstance(value, rg.GeometryBase):
        bbox = value.GetBoundingBox(True)
        if bbox.IsValid:
            return bbox.Center
    return None

def extract_points(param_index):
    points = []
    for value in extract_values(param_index):
        point = as_point(value)
        if point is not None:
            points.append(point)
    return points

def layer_index(doc, full_path):
    parent_id = System.Guid.Empty
    current_index = -1
    parts = [p for p in full_path.split("::") if p]
    for part in parts:
        found = None
        for layer in doc.Layers:
            if layer.IsDeleted:
                continue
            if layer.Name == part and layer.ParentLayerId == parent_id:
                found = layer
                break
        if found is None:
            layer = rd.Layer()
            layer.Name = part
            if parent_id != System.Guid.Empty:
                layer.ParentLayerId = parent_id
            index = doc.Layers.Add(layer)
            found = doc.Layers[index]
        parent_id = found.Id
        current_index = found.Index
    return current_index

def tagged_attributes(doc, full_path, name):
    attrs = rd.ObjectAttributes()
    attrs.LayerIndex = layer_index(doc, full_path)
    attrs.Name = name
    attrs.SetUserString("ghcli_smoke", "true")
    attrs.SetUserString("smoke_run", "massing_stack")
    attrs.SetUserString("source_node", "04_bake")
    return attrs

def delete_previous(doc, root):
    deleted = 0
    for obj in list(doc.Objects):
        attrs = obj.Attributes
        if attrs is None:
            continue
        if attrs.GetUserString("ghcli_smoke") != "true":
            continue
        layer = doc.Layers[attrs.LayerIndex]
        if layer is None or not layer.FullPath.startswith(root):
            continue
        if doc.Objects.Delete(obj, True):
            deleted += 1
    return deleted

def add_geometry(doc, geometry, attrs):
    if geometry is None:
        return None
    if isinstance(geometry, rg.Brep):
        return doc.Objects.AddBrep(geometry, attrs)
    if isinstance(geometry, rg.Curve):
        return doc.Objects.AddCurve(geometry, attrs)
    if isinstance(geometry, rg.Point3d):
        return doc.Objects.AddPoint(geometry, attrs)
    return None

try:
    import System

    should_run = as_bool(run, False)
    should_replace = as_bool(replace, True)
    root = "GHCLI_SMOKE" if layer_root is None else str(layer_root).strip() or "GHCLI_SMOKE"
    baked_ids = []

    if not should_run:
        log("BAKE_SKIPPED | run=false | objects=0")
    else:
        doc = Rhino.RhinoDoc.ActiveDoc
        if doc is None:
            raise Exception("No active Rhino document.")

        deleted = delete_previous(doc, root) if should_replace else 0

        mass_list = extract_geo(2) or as_list(massing)
        level_list = extract_geo(3) or as_list(level_curves)
        grid_list = extract_geo(4) or as_list(grid_curves)
        slab_list = extract_geo(5) or as_list(slab_breps)
        point_list = extract_points(6) or [p for p in (as_point(x) for x in as_list(centroids)) if p is not None]
        text_list = as_list(area_labels)

        if mass_list:
            gid = add_geometry(doc, mass_list[0], tagged_attributes(doc, root + "::00_Massing", "GHCLI_SMOKE_massing"))
            if gid:
                baked_ids.append(str(gid))

        for i, curve in enumerate(level_list):
            gid = add_geometry(doc, curve, tagged_attributes(doc, root + "::01_Levels", "GHCLI_SMOKE_level_L{:02d}".format(i + 1)))
            if gid:
                baked_ids.append(str(gid))

        for i, curve in enumerate(grid_list):
            gid = add_geometry(doc, curve, tagged_attributes(doc, root + "::02_Grids", "GHCLI_SMOKE_grid_{:02d}".format(i + 1)))
            if gid:
                baked_ids.append(str(gid))

        for i, brep in enumerate(slab_list):
            gid = add_geometry(doc, brep, tagged_attributes(doc, root + "::03_Slabs", "GHCLI_SMOKE_slab_L{:02d}".format(i + 1)))
            if gid:
                baked_ids.append(str(gid))

        for i, point in enumerate(point_list):
            text = text_list[i] if i < len(text_list) else "L{:02d}".format(i + 1)
            attrs = tagged_attributes(doc, root + "::04_Area_Labels", "GHCLI_SMOKE_label_L{:02d}".format(i + 1))
            gid = doc.Objects.AddTextDot(rg.TextDot(str(text), point), attrs)
            if gid:
                baked_ids.append(str(gid))

        doc.Views.Redraw()
        log("BAKE_OK | massing={} | levels={} | grids={} | slabs={} | labels={} | objects={} | deleted={}".format(
            1 if mass_list else 0,
            len(level_list),
            len(grid_list),
            len(slab_list),
            len(point_list),
            len(baked_ids),
            deleted))
except Exception as exc:
    baked_ids = []
    log("BAKE_ERROR | {}".format(exc))

dbg = "\n".join(debug_lines)
