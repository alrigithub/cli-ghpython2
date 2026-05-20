#! python 3
import Rhino
import Rhino.Geometry as rg
import System

debug_lines = []

def log(message):
    debug_lines.append(str(message))

def as_list(value):
    if value is None:
        return []
    if isinstance(value, list) or isinstance(value, tuple):
        return list(value)
    return [value]

def extract_geo(param_index):
    comp = ghenv.Component
    param = comp.Params.Input[param_index]
    results = []
    for pi in range(param.VolatileData.PathCount):
        branch = param.VolatileData.get_Branch(pi)
        for goo in branch:
            if goo is None:
                continue
            geo = getattr(goo, "Value", None)
            if isinstance(geo, rg.GeometryBase):
                results.append(geo)
                continue
            if isinstance(geo, System.Guid):
                rhobj = Rhino.RhinoDoc.ActiveDoc.Objects.FindId(geo)
                if rhobj:
                    results.append(rhobj.Geometry)
                    continue
            if hasattr(goo, "ScriptVariable"):
                sv = goo.ScriptVariable()
                if isinstance(sv, rg.GeometryBase):
                    results.append(sv)
    return results

def as_curve(value):
    if isinstance(value, rg.Curve):
        return value
    if isinstance(value, System.Guid):
        rhobj = Rhino.RhinoDoc.ActiveDoc.Objects.FindId(value)
        if rhobj and isinstance(rhobj.Geometry, rg.Curve):
            return rhobj.Geometry
    return None

def curve_z(curve):
    bbox = curve.GetBoundingBox(True)
    return 0.5 * (bbox.Min.Z + bbox.Max.Z)

try:
    source_levels = [c for c in extract_geo(1) if isinstance(c, rg.Curve)]
    if not source_levels:
        source_levels = [c for c in (as_curve(x) for x in as_list(level_curves)) if c is not None]

    grid_count = len([c for c in extract_geo(2) if isinstance(c, rg.Curve)])
    if grid_count == 0:
        grid_count = len([c for c in (as_curve(x) for x in as_list(grid_curves)) if c is not None])

    slab_breps = []
    slab_curves = []
    centroids = []
    areas = []
    level_names = []

    for index, level_curve in enumerate(source_levels):
        if level_curve is None:
            continue

        section_curve = level_curve
        breps = rg.Brep.CreatePlanarBreps([section_curve], 0.01)
        if not breps:
            continue

        brep = breps[0]
        area_props = rg.AreaMassProperties.Compute(brep)
        if area_props is None:
            continue

        slab_breps.append(brep)
        slab_curves.append(section_curve)
        centroids.append(area_props.Centroid)
        areas.append(area_props.Area)
        level_names.append("L{:02d}".format(len(slab_breps)))

    total_area_m2 = sum(areas) / 1000000.0
    log("SLABS_OK | slabs={} | total_area={:.2f}".format(len(slab_breps), total_area_m2))
    if areas:
        log("min_area={:.2f}m2 max_area={:.2f}m2".format(min(areas) / 1000000.0, max(areas) / 1000000.0))
    log("grids_in={}".format(grid_count))
    log("source=level_footprints")
except Exception as exc:
    slab_breps = []
    slab_curves = []
    centroids = []
    areas = []
    level_names = []
    total_area_m2 = 0.0
    log("SLABS_ERROR | {}".format(exc))

dbg = "\n".join(debug_lines)
