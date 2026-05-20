#! python 3
import math
import Rhino.Geometry as rg

debug_lines = []

def log(message):
    debug_lines.append(str(message))

def number(value, fallback, minimum, maximum):
    try:
        parsed = float(value)
    except Exception:
        parsed = fallback
    return max(minimum, min(maximum, parsed))

def integer(value, fallback, minimum, maximum):
    return int(round(number(value, fallback, minimum, maximum)))

def rectangle_curve(width, depth, z):
    hw = width * 0.5
    hd = depth * 0.5
    pts = [
        rg.Point3d(-hw, -hd, z),
        rg.Point3d(hw, -hd, z),
        rg.Point3d(hw, hd, z),
        rg.Point3d(-hw, hd, z),
        rg.Point3d(-hw, -hd, z)
    ]
    return rg.Polyline(pts).ToNurbsCurve()

def scale_at_level(index, count):
    t = 0.0 if count <= 1 else float(index) / float(count - 1)
    taper = 1.0 - 0.16 * t
    if t > 0.62:
        taper *= 0.82
    return max(0.45, taper)

try:
    width_value = number(width, 36000.0, 6000.0, 120000.0)
    depth_value = number(depth, 24000.0, 6000.0, 120000.0)
    height_value = number(height, 42000.0, 6000.0, 240000.0)
    level_count = integer(levels, 12, 2, 80)
    grid_value = number(grid_spacing, 6000.0, 1000.0, 30000.0)

    step = height_value / max(1, level_count - 1)
    level_curves = []
    sections = []

    for i in range(level_count):
        z = i * step
        scale = scale_at_level(i, level_count)
        curve = rectangle_curve(width_value * scale, depth_value * scale, z)
        level_curves.append(curve)
        sections.append(curve)

    lofts = rg.Brep.CreateFromLoft(sections, rg.Point3d.Unset, rg.Point3d.Unset, rg.LoftType.Straight, False)
    massing = None
    if lofts and len(lofts) > 0:
        massing = lofts[0].CapPlanarHoles(0.01)
        if massing is None:
            massing = lofts[0]

    grid_curves = []
    x_grid_count = max(2, int(round(width_value / grid_value)))
    y_grid_count = max(2, int(round(depth_value / grid_value)) + 1)
    grid_z = 0.0

    for i in range(x_grid_count):
        x = -width_value * 0.5 + (i + 0.5) * width_value / x_grid_count
        grid_curves.append(rg.LineCurve(rg.Point3d(x, -depth_value * 0.62, grid_z), rg.Point3d(x, depth_value * 0.62, grid_z)))

    for i in range(y_grid_count):
        y = -depth_value * 0.5 + i * depth_value / max(1, y_grid_count - 1)
        grid_curves.append(rg.LineCurve(rg.Point3d(-width_value * 0.62, y, grid_z), rg.Point3d(width_value * 0.62, y, grid_z)))

    log("MASSING_OK | levels={} | grids={}".format(len(level_curves), len(grid_curves)))
    log("width={:.0f}mm depth={:.0f}mm height={:.0f}mm".format(width_value, depth_value, height_value))
    log("grid_spacing={:.0f}mm".format(grid_value))
except Exception as exc:
    massing = None
    level_curves = []
    grid_curves = []
    log("MASSING_ERROR | {}".format(exc))

dbg = "\n".join(debug_lines)
