FeatureScript 3095;
import(path : "onshape/std/common.fs", version : "3095.0");

/*
 * Auto Pocket
 *
 * Lightening pockets for a flat face. Existing circular holes become nodes with a boss ring,
 * other cutouts get a border, and extra nodes are generated to fill the face. The nodes are
 * turned into a rib pattern (triangles, Voronoi, isogrid, hex or quad) and the cells between
 * the ribs are cut through the part with filleted corners.
 *
 * All 2D math below works in plain numbers (meters) in the face plane's coordinate system.
 * Points are [x, y] arrays, segments are [ax, ay, bx, by] arrays.
 */

export enum PocketPattern
{
    annotation { "Name" : "Triangles" }
    TRIANGLES,
    annotation { "Name" : "Voronoi" }
    VORONOI,
    annotation { "Name" : "Isogrid (triangle grid)" }
    ISOGRID,
    annotation { "Name" : "Hex (honeycomb)" }
    HEX,
    annotation { "Name" : "Quad grid" }
    QUAD
}

export enum NodeLayout
{
    annotation { "Name" : "Evenly spaced random" }
    EVEN,
    annotation { "Name" : "Pure random" }
    RANDOM,
    annotation { "Name" : "Regular grid" }
    GRID,
    annotation { "Name" : "Perimeter + interior" }
    PERIMETER
}

const NODE_COUNT_BOUNDS = { (unitless) : [0, 20, 1000] } as IntegerBoundSpec;
const SEED_BOUNDS = { (unitless) : [0, 0, 1000000] } as IntegerBoundSpec;

const RIB_BOUNDS =
{
    (meter) : [1e-5, 0.003175, 500],
    (centimeter) : 0.3,
    (millimeter) : 3.0,
    (inch) : 0.125,
    (foot) : 0.01,
    (yard) : 0.004
} as LengthBoundSpec;

const BORDER_BOUNDS =
{
    (meter) : [0, 0.00635, 500],
    (centimeter) : 0.6,
    (millimeter) : 6.0,
    (inch) : 0.25,
    (foot) : 0.02,
    (yard) : 0.007
} as LengthBoundSpec;

const BOSS_BOUNDS =
{
    (meter) : [0, 0.003175, 500],
    (centimeter) : 0.3,
    (millimeter) : 3.0,
    (inch) : 0.125,
    (foot) : 0.01,
    (yard) : 0.004
} as LengthBoundSpec;

const FILLET_BOUNDS =
{
    (meter) : [0, 0.0015875, 500],
    (centimeter) : 0.16,
    (millimeter) : 1.6,
    (inch) : 0.0625,
    (foot) : 0.005,
    (yard) : 0.002
} as LengthBoundSpec;

const MIN_POCKET_BOUNDS =
{
    (meter) : [0, 0.00635, 500],
    (centimeter) : 0.6,
    (millimeter) : 6.0,
    (inch) : 0.25,
    (foot) : 0.02,
    (yard) : 0.007
} as LengthBoundSpec;

const BIG = 1e30;
const TOOL_OVERHANG = 0.0005; // pocket tools stick out 0.5 mm past each side of the part

annotation { "Feature Type Name" : "Auto Pocket",
        "Feature Type Description" : "Lightening pockets (triangles, Voronoi, isogrid, hex or quad) built around the holes in a flat face." }
export const autoPocket = defineFeature(function(context is Context, id is Id, definition is map)
    precondition
    {
        annotation { "Name" : "Face to pocket", "Filter" : EntityType.FACE && GeometryType.PLANE && BodyType.SOLID && SketchObject.NO, "MaxNumberOfPicks" : 1 }
        definition.face is Query;

        annotation { "Name" : "Pattern" }
        definition.pattern is PocketPattern;

        if (definition.pattern == PocketPattern.TRIANGLES || definition.pattern == PocketPattern.VORONOI)
        {
            annotation { "Name" : "Node layout" }
            definition.layout is NodeLayout;
        }

        annotation { "Name" : "Node count" }
        isInteger(definition.nodeCount, NODE_COUNT_BOUNDS);

        if (definition.pattern == PocketPattern.ISOGRID || definition.pattern == PocketPattern.HEX || definition.pattern == PocketPattern.QUAD ||
            definition.layout == NodeLayout.GRID)
        {
            annotation { "Name" : "Grid angle" }
            isAngle(definition.gridAngle, ANGLE_360_ZERO_DEFAULT_BOUNDS);
        }

        if ((definition.pattern == PocketPattern.TRIANGLES || definition.pattern == PocketPattern.VORONOI) && definition.layout != NodeLayout.GRID)
        {
            annotation { "Name" : "Random seed" }
            isInteger(definition.seed, SEED_BOUNDS);
        }

        annotation { "Name" : "Rib width" }
        isLength(definition.ribWidth, RIB_BOUNDS);

        annotation { "Name" : "Border width" }
        isLength(definition.borderWidth, BORDER_BOUNDS);

        annotation { "Name" : "Hole boss width" }
        isLength(definition.bossWidth, BOSS_BOUNDS);

        annotation { "Name" : "Cutout border width" }
        isLength(definition.cutoutBorder, BOSS_BOUNDS);

        annotation { "Name" : "Corner fillet radius" }
        isLength(definition.filletRadius, FILLET_BOUNDS);

        annotation { "Name" : "Minimum pocket size" }
        isLength(definition.minPocketSize, MIN_POCKET_BOUNDS);

        annotation { "Name" : "Extra node points", "Filter" : EntityType.VERTEX }
        definition.extraPoints is Query;

        annotation { "Name" : "Keep-out regions", "Filter" : EntityType.FACE && GeometryType.PLANE }
        definition.keepOuts is Query;

        annotation { "Name" : "Preview nodes and ribs only" }
        definition.previewOnly is boolean;
    }
    {
        // ---------------------------------------------------------------- face and frame
        const faces = evaluateQuery(context, definition.face);
        if (size(faces) != 1)
            throw regenError("Select one flat face to pocket.", ["face"]);
        const face = faces[0];
        const facePlane = evPlane(context, { "face" : face });
        const cSys = planeToCSys(facePlane);
        const nrm = facePlane.normal;
        const xAxis = facePlane.x;
        const yAxis = cross(nrm, xAxis);
        const part = qOwnerBody(face);

        const w = definition.ribWidth / meter;
        const b = definition.borderWidth / meter;
        const bossW = definition.bossWidth / meter;
        const cb = definition.cutoutBorder / meter;
        const r = definition.filletRadius / meter;
        const minSize = definition.minPocketSize / meter;
        const N = definition.nodeCount;
        const pattern = definition.pattern;
        var layout = NodeLayout.EVEN;
        if (definition.layout != undefined)
            layout = definition.layout;
        var angle = 0;
        if (definition.gridAngle != undefined)
            angle = definition.gridAngle / radian;
        var seed = 0;
        if (definition.seed != undefined)
            seed = definition.seed;

        // ---------------------------------------------------------------- boundary, holes, keep-outs
        const geo = extractFaceGeometry(context, face, cSys, xAxis, yAxis);

        const keepFaces = evaluateQuery(context, definition.keepOuts);
        var keepoutSegs = [];
        for (var kf in keepFaces)
        {
            const kp = evPlane(context, { "face" : kf });
            if (abs(dot(kp.normal, nrm)) < 0.9999)
                throw regenError("Keep-out regions must be parallel to the pocketed face.", ["keepOuts"], kf);
            keepoutSegs = append(keepoutSegs, faceSegments(context, kf, cSys, xAxis, yAxis));
        }

        const ctx = {
                "outerSegs" : geo.outerSegs,
                "regionSegs" : geo.regionSegs,
                "holes" : geo.holes,
                "cutouts" : geo.cutoutSegs,
                "keepouts" : keepoutSegs,
                "b" : b,
                "boss" : bossW,
                "cb" : cb,
                "bbox" : geo.bbox
            };

        const faceArea = evArea(context, { "entities" : face }) / (meter * meter);

        var forced = [];
        for (var v in evaluateQuery(context, definition.extraPoints))
        {
            const lp = fromWorld(cSys, evVertexPoint(context, { "vertex" : v }));
            forced = append(forced, [lp[0] / meter, lp[1] / meter]);
        }
        var holeCenters = [];
        for (var h in geo.holes)
            holeCenters = append(holeCenters, [h[0], h[1]]);
        const cutCenters = geo.cutoutCenters;

        // ---------------------------------------------------------------- nodes and cells
        const rng = makeRng(seed);
        var nodes = [];
        var cells = [];
        var nMerged = 0;

        if (pattern == PocketPattern.TRIANGLES || pattern == PocketPattern.VORONOI)
        {
            const isTri = pattern == PocketPattern.TRIANGLES;
            var fixed = [];
            var spoke = [];
            if (isTri)
            {
                // Triangles: ribs run node to node, so anchor nodes on the boundary let ribs land on the border.
                const nSpecial = size(holeCenters) + size(forced);
                const s = sqrt(2 * faceArea / (sqrt(3) * max(N + nSpecial, 1)));
                fixed = boundaryAnchors(geo, s);
                fixed = concatenateArrays([fixed, holeCenters, forced]);
                const gen = generateNodes(ctx, geo, fixed, N, s, angle, layout, rng, 1.15, w);
                nodes = dedupePoints(concatenateArrays([fixed, gen]), 1e-6);
                const dt = delaunay(nodes);
                for (var t in dt.tris)
                    cells = append(cells, [nodes[t[0]], nodes[t[1]], nodes[t[2]]]);
                const mg = mergeSmallCells(cells, ctx, w, r, minSize);
                cells = mg.cells;
                nMerged = mg.merged;
            }
            else
            {
                // Voronoi: holes, cutouts and forced points are seeds whose cells get split by spokes.
                fixed = concatenateArrays([holeCenters, cutCenters, forced]);
                const s = sqrt(2 * faceArea / (sqrt(3) * max(N + size(fixed), 1)));
                const gen = generateNodes(ctx, geo, fixed, N, s, angle, layout, rng, 2.0, w);
                var seeds = [];
                for (var p in fixed)
                {
                    seeds = append(seeds, p);
                    spoke = append(spoke, true);
                }
                for (var p in gen)
                {
                    seeds = append(seeds, p);
                    spoke = append(spoke, false);
                }
                const dd = dedupeFlagged(seeds, spoke, 1e-6);
                nodes = dd.pts;
                cells = voronoiCells(dd.pts, dd.flags);
            }
        }
        else
        {
            const isQuad = pattern == PocketPattern.QUAD;
            var g = sqrt(2 * faceArea / (sqrt(3) * max(N, 1)));
            if (isQuad)
                g = sqrt(faceArea / max(N, 1));
            const specials = concatenateArrays([holeCenters, cutCenters, forced]);
            const lat = makeLattice(ctx, g, angle, isQuad);
            const snap = snapSpecials(lat.pts, specials, 0.45 * g);
            if (pattern == PocketPattern.HEX)
            {
                var seeds = snap.pts;
                var spoke = snap.snapped;
                for (var p in snap.unsnapped)
                {
                    seeds = append(seeds, p);
                    spoke = append(spoke, true);
                }
                nodes = seeds;
                cells = voronoiCells(seeds, spoke);
            }
            else
            {
                nodes = concatenateArrays([snap.pts, snap.unsnapped]);
                cells = latticeCells(lat, snap.pts, isQuad);
                cells = splitCellsAtPoints(cells, snap.unsnapped);
            }
        }

        const pk = buildPockets(cells, ctx, w, r, minSize);

        if (definition.previewOnly)
        {
            drawPreview(context, cSys, nodes, cells, pk.paths, pk.skipped, ctx);
            var pmsg = "Preview: " ~ size(nodes) ~ " nodes, " ~ size(pk.paths) ~ " candidate pockets, " ~ pk.small ~ " cells too small";
            if (nMerged > 0)
                pmsg = pmsg ~ ", " ~ nMerged ~ " small triangles merged into a neighbour";
            reportFeatureInfo(context, id, pmsg ~ ". Blue = nodes, red = rib centerlines, green = pocket outlines before clipping, " ~
                    "orange star = cell left solid.");
            return;
        }

        if (size(pk.paths) == 0)
        {
            reportFeatureWarning(context, id, "No pockets fit. Try fewer nodes, thinner ribs, or a smaller minimum pocket size.");
            return;
        }

        // ---------------------------------------------------------------- depth: through the wall under the face
        var depth = 0;
        var nProbe = 0;
        for (var p in pk.probes)
        {
            if (nProbe >= 24)
                break;
            nProbe += 1;
            const origin = toWorld(cSys, vector(p[0], p[1], 1e-5) * meter);
            const hits = try silent(evRaycast(context, { "entities" : part, "ray" : line(origin, -nrm), "closest" : false }));
            if (hits == undefined)
                continue;
            for (var hit in hits)
            {
                const d = hit.distance / meter - 1e-5;
                if (d > 1e-6)
                {
                    depth = max(depth, d);
                    break;
                }
            }
        }
        if (depth <= 0)
        {
            const partBox = evBox3d(context, { "topology" : part, "cSys" : cSys, "tight" : true });
            depth = -partBox.minCorner[2] / meter;
        }

        var zTop = TOOL_OVERHANG;
        var zBot = -depth - TOOL_OVERHANG;
        var keepHeights = [];
        for (var kf in keepFaces)
        {
            const kp = evPlane(context, { "face" : kf });
            const hk = dot(kp.origin - facePlane.origin, nrm) / meter;
            keepHeights = append(keepHeights, hk);
            zTop = max(zTop, hk + TOOL_OVERHANG);
            zBot = min(zBot, hk - TOOL_OVERHANG);
        }
        const topPlane = plane(toWorld(cSys, vector(0, 0, zTop) * meter), nrm, xAxis);
        const toolHeight = (zTop - zBot) * meter;

        // ---------------------------------------------------------------- pocket tool bodies
        var sketchIds = [];
        sketchPaths(context, id + "pocketSketch", topPlane, pk.paths);
        sketchIds = append(sketchIds, id + "pocketSketch");
        opExtrude(context, id + "pockets", {
                    "entities" : qSketchRegion(id + "pocketSketch"),
                    "direction" : -nrm,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : toolHeight
                });

        // ---------------------------------------------------------------- keep bodies (border, bosses, cutout borders, keep-outs)
        var rawShapes = [];
        for (var e in geo.outerEdges)
            rawShapes = concatenateArrays([rawShapes, edgeKeepShapes(e, b)]);
        for (var ce in geo.cutoutEdges)
            for (var e in ce)
                rawShapes = concatenateArrays([rawShapes, edgeKeepShapes(e, cb)]);
        var shapes = [];
        var annuli = [];
        for (var sh in rawShapes)
        {
            if (sh.kind == "annulus")
                annuli = append(annuli, sh);
            else
                shapes = append(shapes, sh);
        }
        if (bossW > 0)
            for (var hh in geo.holes)
                shapes = append(shapes, circleShape([hh[0], hh[1]], hh[2] + bossW));

        // Shapes that overlap go in different sketches so no sketch ever encloses an empty region.
        // All sketches first: operations under the "keep" id must run back to back.
        const families = colorShapes(shapes);
        for (var fi = 0; fi < size(families); fi += 1)
        {
            var famPaths = [];
            for (var sh in families[fi])
                famPaths = append(famPaths, sh.path);
            sketchPaths(context, id + ("keepSketch" ~ fi), topPlane, famPaths);
            sketchIds = append(sketchIds, id + ("keepSketch" ~ fi));
        }
        for (var ai = 0; ai < size(annuli); ai += 1)
        {
            const an = annuli[ai];
            const sk = newSketchOnPlane(context, id + ("annulusSketch" ~ ai), { "sketchPlane" : topPlane });
            skCircle(sk, "inner", { "center" : vec2(an.c), "radius" : an.ri * meter });
            skCircle(sk, "outer", { "center" : vec2(an.c), "radius" : an.ro * meter });
            skSolve(sk);
            sketchIds = append(sketchIds, id + ("annulusSketch" ~ ai));
        }
        for (var fi = 0; fi < size(families); fi += 1)
        {
            opExtrude(context, id + "keep" + ("k" ~ fi), {
                        "entities" : qSketchRegion(id + ("keepSketch" ~ fi)),
                        "direction" : -nrm,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : toolHeight
                    });
        }
        for (var ai = 0; ai < size(annuli); ai += 1)
        {
            const an = annuli[ai];
            const rm = (an.ri + an.ro) / 2;
            const probePt = toWorld(cSys, vector(an.c[0] + rm, an.c[1], zTop) * meter);
            opExtrude(context, id + "keep" + ("a" ~ ai), {
                        "entities" : qContainsPoint(qSketchRegion(id + ("annulusSketch" ~ ai)), probePt),
                        "direction" : -nrm,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : toolHeight
                    });
        }
        for (var ki = 0; ki < size(keepFaces); ki += 1)
        {
            const hk = keepHeights[ki];
            opExtrude(context, id + "keep" + ("o" ~ ki), {
                        "entities" : keepFaces[ki],
                        "direction" : -nrm,
                        "endBound" : BoundingType.BLIND,
                        "endDepth" : (hk - zBot) * meter,
                        "startBound" : BoundingType.BLIND,
                        "startDepth" : (zTop - hk) * meter
                    });
        }

        var sketchBodies = [];
        for (var sid in sketchIds)
            sketchBodies = append(sketchBodies, qCreatedBy(sid, EntityType.BODY));
        opDeleteBodies(context, id + "deleteSketches", { "entities" : qUnion(sketchBodies) });

        const keepQ = qCreatedBy(id + "keep", EntityType.BODY);
        if (!isQueryEmpty(context, keepQ))
        {
            opBoolean(context, id + "clipKeep", {
                        "tools" : keepQ,
                        "targets" : qCreatedBy(id + "pockets", EntityType.BODY),
                        "operationType" : BooleanOperationType.SUBTRACTION
                    });
        }

        // ---------------------------------------------------------------- clip to the face outline
        opExtrude(context, id + "faceTool", {
                    "entities" : face,
                    "direction" : -nrm,
                    "endBound" : BoundingType.BLIND,
                    "endDepth" : -zBot * meter,
                    "startBound" : BoundingType.BLIND,
                    "startDepth" : zTop * meter
                });
        const faceToolQ = qCreatedBy(id + "faceTool", EntityType.BODY);
        const solidsQ = qBodyType(qCreatedBy(id, EntityType.BODY), BodyType.SOLID);
        if (!isQueryEmpty(context, qSubtraction(solidsQ, faceToolQ)))
        {
            opBoolean(context, id + "clipFace", {
                        "tools" : faceToolQ,
                        "targets" : qSubtraction(solidsQ, faceToolQ),
                        "operationType" : BooleanOperationType.SUBTRACT_COMPLEMENT
                    });
        }
        else
        {
            opDeleteBodies(context, id + "deleteFaceTool", { "entities" : faceToolQ });
        }

        // ---------------------------------------------------------------- filter pockets: size and islands
        const pocketBodies = evaluateQuery(context, solidsQ);
        var good = [];
        var goodEdges = [];
        var bad = [];
        var nSmall = pk.small;
        var nIsland = 0;
        for (var pb in pocketBodies)
        {
            const top = qCoincidesWithPlane(qOwnedByBody(pb, EntityType.FACE), topPlane);
            if (isQueryEmpty(context, top))
            {
                bad = append(bad, pb);
                continue;
            }
            const topEdges = qAdjacent(top, AdjacencyType.EDGE, EntityType.EDGE);
            const area = evArea(context, { "entities" : top }) / (meter * meter);
            const perim = evLength(context, { "entities" : topEdges }) / meter;
            if (perim <= 0 || 4 * area / perim < minSize)
            {
                nSmall += 1;
                bad = append(bad, pb);
                continue;
            }
            // A pocket whose outline has an inner loop would leave a floating island of material.
            if (size(connectedComponents(context, topEdges, AdjacencyType.VERTEX)) > 1)
            {
                nIsland += 1;
                bad = append(bad, pb);
                continue;
            }
            var es = [];
            if (r > 0)
            {
                for (var e in evaluateQuery(context, qParallelEdges(qOwnedByBody(pb, EntityType.EDGE), nrm)))
                {
                    if (evEdgeConvexity(context, { "edge" : e }) == EdgeConvexityType.CONVEX)
                        es = append(es, e);
                }
            }
            good = append(good, pb);
            goodEdges = append(goodEdges, es);
        }

        // ---------------------------------------------------------------- corner fillets
        var nFilletFail = 0;
        if (r > 0)
        {
            var allEdges = [];
            for (var es in goodEdges)
                allEdges = concatenateArrays([allEdges, es]);
            if (size(allEdges) > 0)
            {
                var filletedAll = false;
                try silent
                {
                    opFillet(context, id + "filletAll", { "entities" : qUnion(allEdges), "radius" : r * meter });
                    filletedAll = true;
                }
                catch
                {
                    filletedAll = false;
                }
                if (!filletedAll)
                {
                    for (var i = 0; i < size(good); i += 1)
                    {
                        if (size(goodEdges[i]) == 0)
                            continue;
                        try silent
                        {
                            opFillet(context, id + ("fillet" ~ i), { "entities" : qUnion(goodEdges[i]), "radius" : r * meter });
                        }
                        catch
                        {
                            nFilletFail += 1;
                            bad = append(bad, good[i]);
                        }
                    }
                }
            }
        }

        if (size(bad) > 0)
            opDeleteBodies(context, id + "deleteSkipped", { "entities" : qUnion(bad) });

        // ---------------------------------------------------------------- cut
        const finalQ = qBodyType(qCreatedBy(id, EntityType.BODY), BodyType.SOLID);
        const nCut = size(evaluateQuery(context, finalQ));
        if (nCut == 0)
        {
            reportFeatureWarning(context, id, "Every pocket was too small to cut. Try fewer nodes, thinner ribs, or a smaller minimum pocket size.");
            return;
        }
        opBoolean(context, id + "cut", {
                    "tools" : finalQ,
                    "targets" : part,
                    "operationType" : BooleanOperationType.SUBTRACTION
                });

        var msg = "Cut " ~ nCut ~ " pockets.";
        if (nMerged > 0)
            msg = msg ~ " Merged " ~ nMerged ~ " small triangles into a neighbour.";
        if (nSmall > 0)
            msg = msg ~ " Skipped " ~ nSmall ~ " cells below the minimum pocket size (fewer nodes or thinner ribs gives more pockets).";
        if (nIsland > 0)
            msg = msg ~ " Kept " ~ nIsland ~ " solid to avoid floating islands.";
        if (nFilletFail > 0)
            msg = msg ~ " Kept " ~ nFilletFail ~ " solid because their corners could not be filleted.";
        if (nFilletFail > 0 || nIsland > 0)
            reportFeatureWarning(context, id, msg);
        else
            reportFeatureInfo(context, id, msg);
    });

// ======================================================================== face geometry

function sampleEdge(context is Context, edge is Query, face is Query, cSys is CoordSystem, xA is Vector, yA is Vector) returns map
{
    const len = evLength(context, { "entities" : edge }) / meter;
    const curve = try silent(evCurveDefinition(context, { "edge" : edge }));
    var kind = "poly";
    var n = 4;
    var c = undefined;
    var R = 0;
    if (curve is Line)
    {
        kind = "line";
        n = 1;
    }
    else if (curve is Circle)
    {
        kind = "arc";
        R = curve.radius / meter;
        const o = fromWorld(cSys, curve.coordSystem.origin);
        c = [o[0] / meter, o[1] / meter];
        const sweep = len / R;
        // Chord sag at most 0.05 mm, and never more than 30 degrees per segment.
        const step = min(2 * sqrt(2 * 5e-5 / R), PI / 6);
        n = 4 * ceil(sweep / step / 4);
        n = min(max(n, 4), 96);
    }
    else
    {
        n = min(max(ceil(len / 0.002), 4), 64);
    }
    var params = [];
    for (var k = 0; k <= n; k += 1)
        params = append(params, k / n);
    const lines = evEdgeTangentLines(context, { "edge" : edge, "parameters" : params, "face" : face });
    var pts = [];
    var tans = [];
    for (var ln in lines)
    {
        const lp = fromWorld(cSys, ln.origin);
        pts = append(pts, [lp[0] / meter, lp[1] / meter]);
        tans = append(tans, [dot(ln.direction, xA), dot(ln.direction, yA)]);
    }
    const full = len > 1e-6 && dist2(pts[0], pts[n]) < 1e-14;
    return { "kind" : kind, "pts" : pts, "tans" : tans, "len" : len, "c" : c, "R" : R, "full" : full, "n" : n };
}

function extractFaceGeometry(context is Context, face is Query, cSys is CoordSystem, xA is Vector, yA is Vector) returns map
{
    const comps = connectedComponents(context, qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE), AdjacencyType.VERTEX);
    var loops = [];
    for (var comp in comps)
    {
        var edges = [];
        var bb = [BIG, BIG, -BIG, -BIG];
        for (var e in comp)
        {
            const info = sampleEdge(context, e, face, cSys, xA, yA);
            edges = append(edges, info);
            for (var p in info.pts)
                bb = growBox(bb, p);
        }
        loops = append(loops, { "edges" : edges, "bbox" : bb });
    }
    var oi = 0;
    var best = -1;
    for (var i = 0; i < size(loops); i += 1)
    {
        const bb = loops[i].bbox;
        const diag = (bb[2] - bb[0]) * (bb[2] - bb[0]) + (bb[3] - bb[1]) * (bb[3] - bb[1]);
        if (diag > best)
        {
            best = diag;
            oi = i;
        }
    }
    const outerSegs = edgesToSegs(loops[oi].edges);
    var res = {
        "outerEdges" : loops[oi].edges,
        "outerSegs" : outerSegs,
        "bbox" : loops[oi].bbox,
        "holes" : [],
        "cutoutSegs" : [],
        "cutoutEdges" : [],
        "cutoutCenters" : []
    };
    var regionSegs = outerSegs;
    for (var i = 0; i < size(loops); i += 1)
    {
        if (i == oi)
            continue;
        const circ = loopCircle(loops[i].edges);
        if (circ != undefined)
        {
            res.holes = append(res.holes, circ);
        }
        else
        {
            const segs = edgesToSegs(loops[i].edges);
            const bb = loops[i].bbox;
            res.cutoutSegs = append(res.cutoutSegs, segs);
            res.cutoutEdges = append(res.cutoutEdges, loops[i].edges);
            res.cutoutCenters = append(res.cutoutCenters, [(bb[0] + bb[2]) / 2, (bb[1] + bb[3]) / 2]);
            regionSegs = concatenateArrays([regionSegs, segs]);
        }
    }
    res.regionSegs = regionSegs;
    return res;
}

// Segments of every edge of a face (used for keep-out regions).
function faceSegments(context is Context, face is Query, cSys is CoordSystem, xA is Vector, yA is Vector) returns array
{
    var edges = [];
    for (var e in evaluateQuery(context, qAdjacent(face, AdjacencyType.EDGE, EntityType.EDGE)))
        edges = append(edges, sampleEdge(context, e, face, cSys, xA, yA));
    return edgesToSegs(edges);
}

// Returns [cx, cy, R] when every edge of the loop lies on the same circle.
function loopCircle(edges is array)
{
    if (size(edges) == 0)
        return undefined;
    const e0 = edges[0];
    if (e0.kind != "arc")
        return undefined;
    var total = 0;
    for (var e in edges)
    {
        if (e.kind != "arc")
            return undefined;
        if (abs(e.R - e0.R) > 1e-6 || dist2(e.c, e0.c) > 1e-12)
            return undefined;
        total += e.len;
    }
    if (abs(total - 2 * PI * e0.R) > 1e-5)
        return undefined;
    return [e0.c[0], e0.c[1], e0.R];
}

function edgesToSegs(edges is array) returns array
{
    var segs = [];
    for (var e in edges)
    {
        const pts = e.pts;
        for (var k = 0; k < size(pts) - 1; k += 1)
            segs = append(segs, [pts[k][0], pts[k][1], pts[k + 1][0], pts[k + 1][1]]);
    }
    return segs;
}

// ======================================================================== 2D helpers

function dist2(a, b)
{
    const dx = a[0] - b[0];
    const dy = a[1] - b[1];
    return dx * dx + dy * dy;
}

function growBox(bb, p)
{
    return [min(bb[0], p[0]), min(bb[1], p[1]), max(bb[2], p[0]), max(bb[3], p[1])];
}

function vec2(p)
{
    return vector(p[0], p[1]) * meter;
}

function distSeg2(px, py, s)
{
    const dx = s[2] - s[0];
    const dy = s[3] - s[1];
    const l2 = dx * dx + dy * dy;
    var t = 0;
    if (l2 > 0)
    {
        t = ((px - s[0]) * dx + (py - s[1]) * dy) / l2;
        if (t < 0)
            t = 0;
        else if (t > 1)
            t = 1;
    }
    const ex = s[0] + t * dx - px;
    const ey = s[1] + t * dy - py;
    return ex * ex + ey * ey;
}

function minDistSegs(p, segs)
{
    var best = BIG;
    for (var s in segs)
    {
        const d = distSeg2(p[0], p[1], s);
        if (d < best)
            best = d;
    }
    return sqrt(best);
}

function minDistPts(p, pts)
{
    var best = BIG;
    for (var q in pts)
    {
        const d = dist2(p, q);
        if (d < best)
            best = d;
    }
    return sqrt(best);
}

// Even-odd point in region test; segments need not be ordered.
function insideSegs(p, segs)
{
    var inside = false;
    const px = p[0];
    const py = p[1];
    for (var s in segs)
    {
        if ((s[1] > py) != (s[3] > py))
        {
            const xi = s[0] + (py - s[1]) * (s[2] - s[0]) / (s[3] - s[1]);
            if (xi > px)
                inside = !inside;
        }
    }
    return inside;
}

// Distance from p to the nearest thing that must stay solid. Negative when p is in solid-only area.
function obstacleDist(p, ctx)
{
    if (!insideSegs(p, ctx.regionSegs))
        return -1;
    var d = minDistSegs(p, ctx.outerSegs) - ctx.b;
    for (var h in ctx.holes)
    {
        const dd = sqrt(dist2(p, h)) - h[2] - ctx.boss;
        if (dd < d)
            d = dd;
    }
    for (var cs in ctx.cutouts)
    {
        const dd = minDistSegs(p, cs) - ctx.cb;
        if (dd < d)
            d = dd;
    }
    for (var ks in ctx.keepouts)
    {
        if (insideSegs(p, ks))
            return -1;
        const dd = minDistSegs(p, ks);
        if (dd < d)
            d = dd;
    }
    return d;
}

function polyArea(P)
{
    var a = 0;
    const n = size(P);
    for (var i = 0; i < n; i += 1)
    {
        const q = P[i];
        const s = P[(i + 1) % n];
        a += q[0] * s[1] - s[0] * q[1];
    }
    return a / 2;
}

function polyPerimeter(P)
{
    var L = 0;
    const n = size(P);
    for (var i = 0; i < n; i += 1)
        L += sqrt(dist2(P[i], P[(i + 1) % n]));
    return L;
}

function polyLength(pts)
{
    var L = 0;
    for (var k = 0; k < size(pts) - 1; k += 1)
        L += sqrt(dist2(pts[k], pts[k + 1]));
    return L;
}

// Point and segment index at arc length t along an open polyline.
function pointAlong(pts, t)
{
    var acc = 0;
    for (var k = 0; k < size(pts) - 1; k += 1)
    {
        const L = sqrt(dist2(pts[k], pts[k + 1]));
        if (acc + L >= t || k == size(pts) - 2)
        {
            var f = 0;
            if (L > 0)
                f = min(max((t - acc) / L, 0), 1);
            return { "p" : [pts[k][0] + f * (pts[k + 1][0] - pts[k][0]), pts[k][1] + f * (pts[k + 1][1] - pts[k][1])], "k" : k };
        }
        acc += L;
    }
    return { "p" : pts[0], "k" : 0 };
}

// Keep the part of polygon P where dot(p - a, n) >= d.
function clipHalf(P, ax, ay, nx, ny, d)
{
    const n = size(P);
    if (n == 0)
        return P;
    var out = [];
    for (var i = 0; i < n; i += 1)
    {
        const cur = P[i];
        const nxt = P[(i + 1) % n];
        const fc = (cur[0] - ax) * nx + (cur[1] - ay) * ny - d;
        const fnx = (nxt[0] - ax) * nx + (nxt[1] - ay) * ny - d;
        if (fc >= 0)
            out = append(out, cur);
        if ((fc >= 0) != (fnx >= 0))
        {
            const t = fc / (fc - fnx);
            out = append(out, [cur[0] + t * (nxt[0] - cur[0]), cur[1] + t * (nxt[1] - cur[1])]);
        }
    }
    return out;
}

// Drop repeated and collinear vertices. Returns [] if fewer than 3 remain.
function cleanPoly(P)
{
    var out = [];
    for (var p in P)
    {
        if (size(out) > 0 && dist2(p, out[size(out) - 1]) < 1e-12)
            continue;
        out = append(out, p);
    }
    while (size(out) > 1 && dist2(out[0], out[size(out) - 1]) < 1e-12)
        out = resize(out, size(out) - 1);
    var changed = true;
    while (changed && size(out) >= 3)
    {
        changed = false;
        const n = size(out);
        for (var i = 0; i < n; i += 1)
        {
            const a = out[(i + n - 1) % n];
            const m = out[i];
            const c = out[(i + 1) % n];
            const cr = (m[0] - a[0]) * (c[1] - m[1]) - (m[1] - a[1]) * (c[0] - m[0]);
            const scale = sqrt(dist2(a, m) * dist2(m, c));
            if (abs(cr) <= 1e-9 * scale)
            {
                out = removeElementAt(out, i);
                changed = true;
                break;
            }
        }
    }
    if (size(out) < 3)
        return [];
    return out;
}

// Inset a convex CCW polygon by d (intersection of offset half-planes).
function insetConvex(P, d)
{
    var Q = P;
    const n = size(P);
    for (var i = 0; i < n; i += 1)
    {
        const a = P[i];
        const c = P[(i + 1) % n];
        const ex = c[0] - a[0];
        const ey = c[1] - a[1];
        const L = sqrt(ex * ex + ey * ey);
        if (L < 1e-12)
            continue;
        Q = clipHalf(Q, a[0], a[1], -ey / L, ex / L, d);
        if (size(Q) < 3)
            return [];
    }
    return cleanPoly(Q);
}

function pointInConvex(p, P)
{
    const n = size(P);
    for (var i = 0; i < n; i += 1)
    {
        const a = P[i];
        const c = P[(i + 1) % n];
        if ((c[0] - a[0]) * (p[1] - a[1]) - (c[1] - a[1]) * (p[0] - a[0]) < 0)
            return false;
    }
    return true;
}

function ccwPoly(P)
{
    if (polyArea(P) < 0)
        return reverse(P);
    return P;
}

function dedupePoints(pts, tol)
{
    var out = [];
    for (var p in pts)
    {
        if (size(out) > 0 && minDistPts(p, out) < tol)
            continue;
        out = append(out, p);
    }
    return out;
}

function dedupeFlagged(pts, flags, tol)
{
    var out = [];
    var fl = [];
    for (var i = 0; i < size(pts); i += 1)
    {
        if (size(out) > 0 && minDistPts(pts[i], out) < tol)
            continue;
        out = append(out, pts[i]);
        fl = append(fl, flags[i]);
    }
    return { "pts" : out, "flags" : fl };
}

// ======================================================================== random numbers (Park-Miller)

function makeRng(seed is number) returns box
{
    var rng = new box(((seed * 7919 + 104729) % 2147483646) + 1);
    for (var i = 0; i < 4; i += 1)
        rngNext(rng);
    return rng;
}

function rngNext(rng is box) returns number
{
    rng[] = (rng[] * 16807) % 2147483647;
    return rng[] / 2147483647;
}

// ======================================================================== node generation

// Nodes spaced along the outer boundary and every cutout so triangle ribs land on the borders.
function boundaryAnchors(geo is map, s is number) returns array
{
    var anchors = [];
    for (var e in geo.outerEdges)
        anchors = addPolyAnchors(anchors, e.pts, s, 0.4 * s);
    for (var ce in geo.cutoutEdges)
    {
        var per = 0;
        for (var e in ce)
            per += e.len;
        const sc = min(s, per / 4);
        for (var e in ce)
            anchors = addPolyAnchors(anchors, e.pts, sc, 0.4 * sc);
    }
    return anchors;
}

function addPolyAnchors(anchors is array, pts is array, step is number, minD is number) returns array
{
    var out = anchors;
    const L = polyLength(pts);
    const m = max(1, round(L / step));
    for (var k = 0; k <= m; k += 1)
    {
        const p = pointAlong(pts, k * L / m).p;
        if (size(out) == 0 || minDistPts(p, out) >= minD)
            out = append(out, p);
    }
    return out;
}

function generateNodes(ctx is map, geo is map, fixed is array, count is number, s is number, angle is number,
    layout is NodeLayout, rng is box, factor is number, w is number) returns array
{
    if (count <= 0)
        return [];
    if (layout == NodeLayout.RANDOM)
        return generateRandom(ctx, fixed, count, rng, w);
    if (layout == NodeLayout.GRID)
        return generateLattice(ctx, fixed, count, s, angle);
    if (layout == NodeLayout.PERIMETER)
        return generatePerimeter(ctx, geo, fixed, count, s, rng, factor);
    return generateEven(ctx, fixed, count, rng, factor);
}

// Mitchell's best-candidate sampling: each new node is the candidate farthest from everything else.
// Solid areas count as obstacles at (factor * distance), so nodes keep a sensible gap from borders.
function generateEven(ctx is map, fixed is array, count is number, rng is box, factor is number) returns array
{
    var all = fixed;
    var gen = [];
    const bb = ctx.bbox;
    for (var i = 0; i < count; i += 1)
    {
        var best = undefined;
        var bestScore = -1;
        var found = 0;
        var tries = 0;
        while (found < 15 && tries < 600)
        {
            tries += 1;
            const p = [bb[0] + rngNext(rng) * (bb[2] - bb[0]), bb[1] + rngNext(rng) * (bb[3] - bb[1])];
            const od = obstacleDist(p, ctx);
            if (od < 0)
                continue;
            found += 1;
            var sc2 = (od * factor) * (od * factor);
            for (var q in all)
            {
                const dx = p[0] - q[0];
                const dy = p[1] - q[1];
                const d2 = dx * dx + dy * dy;
                if (d2 < sc2)
                    sc2 = d2;
            }
            if (sc2 > bestScore)
            {
                bestScore = sc2;
                best = p;
            }
        }
        if (best == undefined)
            break;
        gen = append(gen, best);
        all = append(all, best);
    }
    return gen;
}

function generateRandom(ctx is map, fixed is array, count is number, rng is box, minD is number) returns array
{
    var all = fixed;
    var gen = [];
    const bb = ctx.bbox;
    var tries = 0;
    while (size(gen) < count && tries < count * 400 + 100)
    {
        tries += 1;
        const p = [bb[0] + rngNext(rng) * (bb[2] - bb[0]), bb[1] + rngNext(rng) * (bb[3] - bb[1])];
        if (obstacleDist(p, ctx) < 0)
            continue;
        if (size(all) > 0 && minDistPts(p, all) < minD)
            continue;
        gen = append(gen, p);
        all = append(all, p);
    }
    return gen;
}

// Hex-packed lattice, with spacing tuned until the node count is close to the request.
function generateLattice(ctx is map, fixed is array, count is number, s is number, angle is number) returns array
{
    var g = s;
    var pts = [];
    for (var iter = 0; iter < 10; iter += 1)
    {
        pts = hexLatticeIn(ctx, fixed, g, angle);
        const m = size(pts);
        if (m == 0)
        {
            g = g * 0.7;
            continue;
        }
        if (abs(m - count) <= max(1, 0.03 * count))
            break;
        g = g * sqrt(m / count);
    }
    return pts;
}

function hexLatticeIn(ctx is map, fixed is array, g is number, angle is number) returns array
{
    const fr = latticeFrame(ctx.bbox, angle, g);
    const h = g * sqrt(3) / 2;
    var pts = [];
    for (var j = floor(fr.ymin / h); j <= ceil(fr.ymax / h); j += 1)
    {
        const shift = (((j % 2) + 2) % 2) * g / 2;
        for (var i = floor((fr.xmin - shift) / g); i <= ceil((fr.xmax - shift) / g); i += 1)
        {
            const p = frameToWorld(fr, i * g + shift, j * h);
            if (obstacleDist(p, ctx) < 0.25 * g)
                continue;
            if (size(fixed) > 0 && minDistPts(p, fixed) < 0.5 * g)
                continue;
            pts = append(pts, p);
        }
    }
    return pts;
}

// A ring of nodes inset from the outer border, then evenly spaced nodes inside.
function generatePerimeter(ctx is map, geo is map, fixed is array, count is number, s is number, rng is box, factor is number) returns array
{
    var ring = [];
    const inset = ctx.b + 0.5 * s;
    for (var e in geo.outerEdges)
    {
        const L = polyLength(e.pts);
        const m = max(1, round(L / s));
        for (var k = 0; k < m; k += 1)
        {
            const pa = pointAlong(e.pts, (k + 0.5) * L / m);
            const t = e.tans[pa.k];
            const tl = sqrt(t[0] * t[0] + t[1] * t[1]);
            if (tl < 1e-12)
                continue;
            // Tangents are oriented with the face on the left, so the left normal points inward.
            const q = [pa.p[0] - t[1] / tl * inset, pa.p[1] + t[0] / tl * inset];
            if (obstacleDist(q, ctx) < 0)
                continue;
            const others = concatenateArrays([fixed, ring]);
            if (size(others) > 0 && minDistPts(q, others) < 0.7 * s)
                continue;
            ring = append(ring, q);
        }
    }
    if (size(ring) > count)
    {
        var sub = [];
        for (var k = 0; k < count; k += 1)
            sub = append(sub, ring[floor(k * size(ring) / count)]);
        ring = sub;
    }
    const rest = generateEven(ctx, concatenateArrays([fixed, ring]), count - size(ring), rng, factor);
    return concatenateArrays([ring, rest]);
}

// ======================================================================== lattices (isogrid / hex / quad)

function latticeFrame(bbox is array, angle is number, g is number) returns map
{
    const o = [(bbox[0] + bbox[2]) / 2, (bbox[1] + bbox[3]) / 2];
    const u = [cos(angle * radian), sin(angle * radian)];
    const v = [-u[1], u[0]];
    var xmin = BIG;
    var xmax = -BIG;
    var ymin = BIG;
    var ymax = -BIG;
    for (var c in [[bbox[0], bbox[1]], [bbox[2], bbox[1]], [bbox[2], bbox[3]], [bbox[0], bbox[3]]])
    {
        const dx = c[0] - o[0];
        const dy = c[1] - o[1];
        const lx = dx * u[0] + dy * u[1];
        const ly = dx * v[0] + dy * v[1];
        xmin = min(xmin, lx);
        xmax = max(xmax, lx);
        ymin = min(ymin, ly);
        ymax = max(ymax, ly);
    }
    const pad = 2 * g;
    return { "o" : o, "u" : u, "v" : v, "xmin" : xmin - pad, "xmax" : xmax + pad, "ymin" : ymin - pad, "ymax" : ymax + pad };
}

function frameToWorld(fr is map, lx is number, ly is number) returns array
{
    return [fr.o[0] + lx * fr.u[0] + ly * fr.v[0], fr.o[1] + lx * fr.u[1] + ly * fr.v[1]];
}

// Triangular (sheared rows) or square lattice covering the face, indexed by "i_j".
function makeLattice(ctx is map, g is number, angle is number, quad is boolean) returns map
{
    const fr = latticeFrame(ctx.bbox, angle, g);
    var h = g * sqrt(3) / 2;
    if (quad)
        h = g;
    var pts = [];
    var index = {};
    var ijs = [];
    for (var j = floor(fr.ymin / h); j <= ceil(fr.ymax / h); j += 1)
    {
        var shift = j * g / 2;
        if (quad)
            shift = 0;
        for (var i = floor((fr.xmin - shift) / g); i <= ceil((fr.xmax - shift) / g); i += 1)
        {
            const p = frameToWorld(fr, i * g + shift, j * h);
            if (!insideSegs(p, ctx.regionSegs) && minDistSegs(p, ctx.outerSegs) > 1.5 * g)
                continue;
            index[i ~ "_" ~ j] = size(pts);
            pts = append(pts, p);
            ijs = append(ijs, [i, j]);
        }
    }
    return { "pts" : pts, "index" : index, "ijs" : ijs };
}

// Move the nearest lattice node onto each hole / cutout / forced point when it is close enough.
function snapSpecials(pts is array, specials is array, tol is number) returns map
{
    var out = pts;
    var snapped = makeArray(size(pts), false);
    var uns = [];
    for (var sp in specials)
    {
        var bestK = -1;
        var bd = tol * tol;
        for (var k = 0; k < size(out); k += 1)
        {
            if (snapped[k])
                continue;
            const d = dist2(sp, out[k]);
            if (d < bd)
            {
                bd = d;
                bestK = k;
            }
        }
        if (bestK >= 0)
        {
            out[bestK] = sp;
            snapped[bestK] = true;
        }
        else
        {
            uns = append(uns, sp);
        }
    }
    return { "pts" : out, "snapped" : snapped, "unsnapped" : uns };
}

function latticeCells(lat is map, pts is array, quad is boolean) returns array
{
    var cells = [];
    for (var t = 0; t < size(lat.ijs); t += 1)
    {
        const i = lat.ijs[t][0];
        const j = lat.ijs[t][1];
        const b = lat.index[(i + 1) ~ "_" ~ j];
        const c = lat.index[i ~ "_" ~ (j + 1)];
        const d = lat.index[(i + 1) ~ "_" ~ (j + 1)];
        if (quad)
        {
            if (b == undefined || c == undefined || d == undefined)
                continue;
            cells = concatenateArrays([cells, quadCells(pts[t], pts[b], pts[d], pts[c])]);
        }
        else
        {
            if (b != undefined && c != undefined)
                cells = append(cells, ccwPoly([pts[t], pts[b], pts[c]]));
            if (b != undefined && c != undefined && d != undefined)
                cells = append(cells, ccwPoly([pts[b], pts[d], pts[c]]));
        }
    }
    return cells;
}

// A quad cell, or two triangles if snapping made it non-convex.
function quadCells(p0, p1, p2, p3) returns array
{
    const P = ccwPoly([p0, p1, p2, p3]);
    var convex = true;
    for (var i = 0; i < 4; i += 1)
    {
        const a = P[i];
        const m = P[(i + 1) % 4];
        const c = P[(i + 2) % 4];
        if ((m[0] - a[0]) * (c[1] - m[1]) - (m[1] - a[1]) * (c[0] - m[0]) <= 0)
            convex = false;
    }
    if (convex)
        return [P];
    return [ccwPoly([P[0], P[1], P[2]]), ccwPoly([P[0], P[2], P[3]])];
}

// Split the cell containing each point into wedges around it, so ribs (spokes) reach that point.
function splitCellsAtPoints(cells is array, points is array) returns array
{
    var out = cells;
    for (var p in points)
    {
        for (var ci = 0; ci < size(out); ci += 1)
        {
            const C = out[ci];
            if (!pointInConvex(p, C))
                continue;
            out = removeElementAt(out, ci);
            for (var k = 0; k < size(C); k += 1)
                out = append(out, [p, C[k], C[(k + 1) % size(C)]]);
            break;
        }
    }
    return out;
}

// ======================================================================== Delaunay / Voronoi

function makeTri(P, a, b, c)
{
    var bb = b;
    var cc = c;
    if ((P[b][0] - P[a][0]) * (P[c][1] - P[a][1]) - (P[b][1] - P[a][1]) * (P[c][0] - P[a][0]) < 0)
    {
        bb = c;
        cc = b;
    }
    const ax = P[a][0];
    const ay = P[a][1];
    const bx = P[bb][0];
    const by = P[bb][1];
    const cx = P[cc][0];
    const cy = P[cc][1];
    const d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
    if (abs(d) < 1e-20)
        return [a, bb, cc, (ax + bx + cx) / 3, (ay + by + cy) / 3, BIG];
    const a2 = ax * ax + ay * ay;
    const b2 = bx * bx + by * by;
    const c2 = cx * cx + cy * cy;
    const ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
    const uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
    return [a, bb, cc, ux, uy, (ax - ux) * (ax - ux) + (ay - uy) * (ay - uy)];
}

// Bowyer-Watson. Returns CCW triangles (indices into pts) and their circumcenters.
function delaunay(pts is array) returns map
{
    const n = size(pts);
    if (n < 3)
        return { "tris" : [], "cc" : [] };
    var bb = [BIG, BIG, -BIG, -BIG];
    for (var p in pts)
        bb = growBox(bb, p);
    const cx0 = (bb[0] + bb[2]) / 2;
    const cy0 = (bb[1] + bb[3]) / 2;
    var sc = max(bb[2] - bb[0], bb[3] - bb[1]);
    if (sc <= 0)
        sc = 1;
    var P = [];
    for (var p in pts)
        P = append(P, [(p[0] - cx0) / sc, (p[1] - cy0) / sc]);
    P = append(P, [-100, -60]);
    P = append(P, [100, -60]);
    P = append(P, [0, 120]);
    var tris = [makeTri(P, n, n + 1, n + 2)];
    for (var i = 0; i < n; i += 1)
    {
        const px = P[i][0];
        const py = P[i][1];
        var keep = [];
        var badEdges = [];
        var count = {};
        for (var t in tris)
        {
            const dx = px - t[3];
            const dy = py - t[4];
            if (dx * dx + dy * dy < t[5])
            {
                for (var e in [[t[0], t[1]], [t[1], t[2]], [t[2], t[0]]])
                {
                    badEdges = append(badEdges, e);
                    const key = min(e[0], e[1]) ~ "_" ~ max(e[0], e[1]);
                    if (count[key] == undefined)
                        count[key] = 1;
                    else
                        count[key] = count[key] + 1;
                }
            }
            else
            {
                keep = append(keep, t);
            }
        }
        for (var e in badEdges)
        {
            if (count[min(e[0], e[1]) ~ "_" ~ max(e[0], e[1])] == 1)
                keep = append(keep, makeTri(P, e[0], e[1], i));
        }
        tris = keep;
    }
    var outT = [];
    var outC = [];
    for (var t in tris)
    {
        if (t[0] >= n || t[1] >= n || t[2] >= n)
            continue;
        if (t[5] >= BIG)
            continue;
        outT = append(outT, [t[0], t[1], t[2]]);
        outC = append(outC, [t[3] * sc + cx0, t[4] * sc + cy0]);
    }
    return { "tris" : outT, "cc" : outC };
}

// Voronoi cells from the Delaunay dual. A ring of far-away ghost seeds bounds the outer cells.
// Seeds flagged in "spoke" are split into wedges so spokes tie them to the cell corners.
function voronoiCells(seeds is array, spoke is array) returns array
{
    const n = size(seeds);
    if (n == 0)
        return [];
    var bb = [BIG, BIG, -BIG, -BIG];
    for (var p in seeds)
        bb = growBox(bb, p);
    const cx0 = (bb[0] + bb[2]) / 2;
    const cy0 = (bb[1] + bb[3]) / 2;
    var sc = max(bb[2] - bb[0], bb[3] - bb[1]);
    if (sc <= 0)
        sc = 0.01;
    var pts = seeds;
    const G = 24;
    for (var k = 0; k < G; k += 1)
    {
        const a = 2 * PI * k / G;
        pts = append(pts, [cx0 + 3 * sc * cos(a * radian), cy0 + 3 * sc * sin(a * radian)]);
    }
    const dt = delaunay(pts);
    var inc = makeArray(n, []);
    for (var ti = 0; ti < size(dt.tris); ti += 1)
    {
        for (var vi in dt.tris[ti])
        {
            if (vi < n)
                inc[vi] = append(inc[vi], ti);
        }
    }
    var cells = [];
    for (var i = 0; i < n; i += 1)
    {
        const s = seeds[i];
        var verts = [];
        for (var ti in inc[i])
        {
            const c = dt.cc[ti];
            verts = append(verts, [c[0], c[1], atan2(c[1] - s[1], c[0] - s[0]) / radian]);
        }
        if (size(verts) < 3)
            continue;
        verts = sort(verts, function(a, b)
            {
                return a[2] - b[2];
            });
        var poly = [];
        for (var vv in verts)
            poly = append(poly, [vv[0], vv[1]]);
        poly = cleanPoly(poly);
        if (size(poly) < 3)
            continue;
        if (spoke[i])
        {
            for (var k = 0; k < size(poly); k += 1)
                cells = append(cells, [s, poly[k], poly[(k + 1) % size(poly)]]);
        }
        else
        {
            cells = append(cells, poly);
        }
    }
    return cells;
}

// ======================================================================== pocket outlines

// Inset each cell by half a rib, round its corners with the fillet radius, and drop ones that are too small.
function buildPockets(cells is array, ctx is map, w is number, r is number, minSize is number) returns map
{
    var paths = [];
    var probes = [];
    var skipped = []; // cells in pocketable area that were too small to cut
    for (var cell in cells)
    {
        var C = cleanPoly(cell);
        if (size(C) < 3)
            continue;
        C = ccwPoly(C);
        const pocket = cellPocket(C, ctx, w, r, minSize);
        if (pocket == undefined)
        {
            if (obstacleDist(polyCentroid(C), ctx) >= 0)
                skipped = append(skipped, C);
            continue;
        }
        paths = append(paths, pocket.path);
        probes = append(probes, pocket.probe);
    }
    return { "paths" : paths, "probes" : probes, "small" : size(skipped), "skipped" : skipped };
}

// Pocket outline and probe point for convex CCW cell C, or undefined if it is too small to cut.
function cellPocket(C is array, ctx is map, w is number, r is number, minSize is number)
{
    const bb = ctx.bbox;
    const m = r + 0.0005;
    var Q = insetConvex(C, w / 2);
    Q = clipHalf(Q, bb[0] - m, 0, 1, 0, 0);
    Q = clipHalf(Q, bb[2] + m, 0, -1, 0, 0);
    Q = clipHalf(Q, 0, bb[1] - m, 0, 1, 0);
    Q = clipHalf(Q, 0, bb[3] + m, 0, -1, 0);
    Q = cleanPoly(Q);
    if (size(Q) < 3 || polyArea(Q) <= 0 || 4 * polyArea(Q) / polyPerimeter(Q) < minSize)
        return undefined;
    const probe = fitPoint(Q, ctx, max(minSize / 2, r));
    if (probe == undefined)
        return undefined;
    const path = roundedOutline(Q, r);
    if (path == undefined)
        return undefined;
    return { "path" : path, "probe" : probe };
}

// Triangles: a cell too small to cut gives up one rib and joins the neighbour across it, as long as the
// merged cell is convex and a pocket fits in it. Among neighbours that work, the smallest merged cell wins.
// When no neighbour works, two small cells may merge anyway so the pair can try again next round.
function mergeSmallCells(cells is array, ctx is map, w is number, r is number, minSize is number) returns map
{
    var cs = [];
    var ok = [];
    for (var cell in cells)
    {
        var C = cleanPoly(cell);
        if (size(C) < 3)
            continue;
        C = ccwPoly(C);
        cs = append(cs, C);
        ok = append(ok, cellPocket(C, ctx, w, r, minSize) != undefined);
    }
    var alive = makeArray(size(cs), true);
    var nMerged = 0;
    for (var pass = 0; pass < 4; pass += 1)
    {
        var progress = false;
        var fallback = undefined; // best small + small merge seen this round, used only if nothing else merged
        for (var i = 0; i < size(cs); i += 1)
        {
            if (!alive[i] || ok[i])
                continue;
            var best = undefined;
            var bestArea = BIG;
            for (var nb in cellNeighbours(cs, alive, i, ctx))
            {
                const U = nb.union;
                const area = polyArea(U);
                if (cellPocket(U, ctx, w, r, minSize) != undefined)
                {
                    if (area < bestArea)
                    {
                        bestArea = area;
                        best = nb;
                    }
                }
                else if (!ok[nb.j] && (fallback == undefined || area > fallback.area))
                {
                    fallback = { "i" : i, "j" : nb.j, "union" : U, "area" : area };
                }
            }
            if (best != undefined)
            {
                cs[i] = best.union;
                ok[i] = true;
                alive[best.j] = false;
                nMerged += 1;
                progress = true;
            }
        }
        if (!progress)
        {
            if (fallback == undefined || !alive[fallback.i] || !alive[fallback.j])
                break;
            cs[fallback.i] = fallback.union;
            alive[fallback.j] = false;
        }
    }
    var out = [];
    for (var i = 0; i < size(cs); i += 1)
    {
        if (alive[i])
            out = append(out, cs[i]);
    }
    return { "cells" : out, "merged" : nMerged };
}

// Live cells sharing an edge with cell i where dropping that edge leaves a convex cell.
// The shared edge must lie in material, otherwise there is no rib there to remove.
function cellNeighbours(cs is array, alive is array, i is number, ctx is map) returns array
{
    const A = cs[i];
    const n = size(A);
    var out = [];
    for (var ia = 0; ia < n; ia += 1)
    {
        const a = A[ia];
        const b = A[(ia + 1) % n];
        if (obstacleDist([(a[0] + b[0]) / 2, (a[1] + b[1]) / 2], ctx) < 0)
            continue;
        for (var j = 0; j < size(cs); j += 1)
        {
            if (j == i || !alive[j])
                continue;
            const B = cs[j];
            const m = size(B);
            for (var jb = 0; jb < m; jb += 1)
            {
                if (dist2(B[jb], b) > 1e-14 || dist2(B[(jb + 1) % m], a) > 1e-14)
                    continue;
                // Walk A from b around to a, then B from a around to b, skipping the shared corners.
                var U = [];
                for (var k = 1; k <= n; k += 1)
                    U = append(U, A[(ia + k) % n]);
                for (var k = 2; k < m; k += 1)
                    U = append(U, B[(jb + k) % m]);
                U = cleanPoly(U);
                if (size(U) >= 3 && isConvex(U))
                    out = append(out, { "j" : j, "union" : U });
            }
        }
    }
    return out;
}

function isConvex(P) returns boolean
{
    const n = size(P);
    for (var i = 0; i < n; i += 1)
    {
        const a = P[(i + n - 1) % n];
        const m = P[i];
        const c = P[(i + 1) % n];
        const cr = (m[0] - a[0]) * (c[1] - m[1]) - (m[1] - a[1]) * (c[0] - m[0]);
        if (cr < -1e-9 * sqrt(dist2(a, m) * dist2(m, c)))
            return false;
    }
    return true;
}

function polyCentroid(P)
{
    var c = [0, 0];
    for (var p in P)
        c = [c[0] + p[0] / size(P), c[1] + p[1] / size(P)];
    return c;
}

// The center of a circle of radius rad that fits inside cell Q and clear of every solid area,
// or undefined if no sampled point works. This is what rejects slivers left after clipping.
function fitPoint(Q, ctx, rad)
{
    var c = [0, 0];
    var bb = [BIG, BIG, -BIG, -BIG];
    for (var p in Q)
    {
        c = [c[0] + p[0] / size(Q), c[1] + p[1] / size(Q)];
        bb = growBox(bb, p);
    }
    var qSegs = [];
    for (var i = 0; i < size(Q); i += 1)
        qSegs = append(qSegs, [Q[i][0], Q[i][1], Q[(i + 1) % size(Q)][0], Q[(i + 1) % size(Q)][1]]);
    var cand = [c];
    const step = max(rad / 1.5, max(bb[2] - bb[0], bb[3] - bb[1]) / 12);
    for (var x = bb[0] + step / 2; x < bb[2]; x += step)
        for (var y = bb[1] + step / 2; y < bb[3]; y += step)
            cand = append(cand, [x, y]);
    for (var p in cand)
    {
        if (!pointInConvex(p, Q) || minDistSegs(p, qSegs) < rad)
            continue;
        if (obstacleDist(p, ctx) >= rad)
            return p;
    }
    return undefined;
}

// Closed outline of convex polygon Q with every corner rounded to radius r.
// Segments are { "arc" : bool, "p" : [start, (mid,) end] }.
function roundedOutline(Q, r)
{
    var path = [];
    if (r <= 1e-9)
    {
        for (var i = 0; i < size(Q); i += 1)
            path = append(path, { "arc" : false, "p" : [Q[i], Q[(i + 1) % size(Q)]] });
        return path;
    }
    const Qi = insetConvex(Q, r);
    if (size(Qi) < 3 || polyArea(Qi) < 1e-14)
        return undefined;
    const n = size(Qi);
    var nr = [];
    for (var i = 0; i < n; i += 1)
    {
        const a = Qi[i];
        const c = Qi[(i + 1) % n];
        const ex = c[0] - a[0];
        const ey = c[1] - a[1];
        const L = sqrt(ex * ex + ey * ey);
        nr = append(nr, [ey / L, -ex / L]);
    }
    for (var i = 0; i < n; i += 1)
    {
        const v = Qi[i];
        const np = nr[(i + n - 1) % n];
        const nc = nr[i];
        const s0 = [v[0] + r * np[0], v[1] + r * np[1]];
        const s1 = [v[0] + r * nc[0], v[1] + r * nc[1]];
        if (np[0] * nc[0] + np[1] * nc[1] < 0.99996)
        {
            const mx = np[0] + nc[0];
            const my = np[1] + nc[1];
            const ml = sqrt(mx * mx + my * my);
            path = append(path, { "arc" : true, "p" : [s0, [v[0] + r * mx / ml, v[1] + r * my / ml], s1] });
        }
        else if (dist2(s0, s1) > 1e-14)
        {
            path = append(path, { "arc" : false, "p" : [s0, s1] });
        }
        const v1 = Qi[(i + 1) % n];
        path = append(path, { "arc" : false, "p" : [s1, [v1[0] + r * nc[0], v1[1] + r * nc[1]]] });
    }
    return path;
}

function sketchPaths(context is Context, sketchId is Id, sketchPlane is Plane, paths is array)
{
    const sk = newSketchOnPlane(context, sketchId, { "sketchPlane" : sketchPlane });
    var k = 0;
    for (var path in paths)
    {
        for (var seg in path)
        {
            if (seg.circle == true)
                skCircle(sk, "e" ~ k, { "center" : vec2(seg.c), "radius" : seg.r * meter });
            else if (seg.arc)
                skArc(sk, "e" ~ k, { "start" : vec2(seg.p[0]), "mid" : vec2(seg.p[1]), "end" : vec2(seg.p[2]) });
            else
                skLineSegment(sk, "e" ~ k, { "start" : vec2(seg.p[0]), "end" : vec2(seg.p[1]) });
            k += 1;
        }
    }
    skSolve(sk);
}

// ======================================================================== keep shapes (solid bands)

function shapeFromPath(path is array, segs is array, rad is number) returns map
{
    var bb = [BIG, BIG, -BIG, -BIG];
    for (var s in segs)
    {
        bb = growBox(bb, [s[0], s[1]]);
        bb = growBox(bb, [s[2], s[3]]);
    }
    return { "kind" : "path", "path" : path, "segs" : segs, "rad" : rad,
            "bbox" : [bb[0] - rad, bb[1] - rad, bb[2] + rad, bb[3] + rad] };
}

function circleShape(c is array, rr is number) returns map
{
    return shapeFromPath([{ "circle" : true, "c" : c, "r" : rr }], [[c[0], c[1], c[0], c[1]]], rr);
}

// Every point within wd of segment a-b.
function stadiumShape(a is array, e is array, wd is number) returns map
{
    const dx = e[0] - a[0];
    const dy = e[1] - a[1];
    const L = sqrt(dx * dx + dy * dy);
    if (L < 1e-9)
        return circleShape(a, wd);
    const ux = dx / L;
    const uy = dy / L;
    const nx = -uy * wd;
    const ny = ux * wd;
    const a1 = [a[0] + nx, a[1] + ny];
    const b1 = [e[0] + nx, e[1] + ny];
    const b2 = [e[0] - nx, e[1] - ny];
    const a2 = [a[0] - nx, a[1] - ny];
    const path = [
            { "arc" : false, "p" : [a1, b1] },
            { "arc" : true, "p" : [b1, [e[0] + ux * wd, e[1] + uy * wd], b2] },
            { "arc" : false, "p" : [b2, a2] },
            { "arc" : true, "p" : [a2, [a[0] - ux * wd, a[1] - uy * wd], a1] }
        ];
    return shapeFromPath(path, [[a[0], a[1], e[0], e[1]]], wd);
}

// Every point within wd of an arc (sweep at most 180 degrees, R > wd).
function arcbandShape(c is array, R is number, p0 is array, pm is array, p1 is array, wd is number, polyPts is array) returns map
{
    const u0 = unit2([p0[0] - c[0], p0[1] - c[1]]);
    const um = unit2([pm[0] - c[0], pm[1] - c[1]]);
    const u1 = unit2([p1[0] - c[0], p1[1] - c[1]]);
    var sgn = 1;
    if (u0[0] * um[1] - u0[1] * um[0] < 0)
        sgn = -1;
    const t0 = [-sgn * u0[1], sgn * u0[0]];
    const t1 = [-sgn * u1[1], sgn * u1[0]];
    const ro = R + wd;
    const ri = R - wd;
    const at = function(u, rad)
        {
            return [c[0] + rad * u[0], c[1] + rad * u[1]];
        };
    const path = [
            { "arc" : true, "p" : [at(u0, ro), at(um, ro), at(u1, ro)] },
            { "arc" : true, "p" : [at(u1, ro), [c[0] + R * u1[0] + wd * t1[0], c[1] + R * u1[1] + wd * t1[1]], at(u1, ri)] },
            { "arc" : true, "p" : [at(u1, ri), at(um, ri), at(u0, ri)] },
            { "arc" : true, "p" : [at(u0, ri), [c[0] + R * u0[0] - wd * t0[0], c[1] + R * u0[1] - wd * t0[1]], at(u0, ro)] }
        ];
    var segs = [];
    for (var k = 0; k < size(polyPts) - 1; k += 1)
        segs = append(segs, [polyPts[k][0], polyPts[k][1], polyPts[k + 1][0], polyPts[k + 1][1]]);
    return shapeFromPath(path, segs, wd);
}

function unit2(v)
{
    const L = sqrt(v[0] * v[0] + v[1] * v[1]);
    return [v[0] / L, v[1] / L];
}

// Solid band of width wd on both sides of a boundary edge.
function edgeKeepShapes(e is map, wd is number) returns array
{
    if (wd <= 0)
        return [];
    const n = e.n;
    if (e.kind == "line")
        return [stadiumShape(e.pts[0], e.pts[n], wd)];
    if (e.kind == "arc")
    {
        if (e.full)
        {
            if (e.R > wd * 1.001)
                return [{ "kind" : "annulus", "c" : e.c, "ri" : e.R - wd, "ro" : e.R + wd }];
            return [circleShape(e.c, e.R + wd)];
        }
        if (e.R <= wd * 1.001)
            return [circleShape(e.c, e.R + wd)];
        if (e.len / e.R > PI * 0.9)
        {
            const h = n / 2;
            return [arcbandShape(e.c, e.R, e.pts[0], e.pts[n / 4], e.pts[h], wd, subArray(e.pts, 0, h + 1)),
                    arcbandShape(e.c, e.R, e.pts[h], e.pts[3 * n / 4], e.pts[n], wd, subArray(e.pts, h, n + 1))];
        }
        return [arcbandShape(e.c, e.R, e.pts[0], e.pts[n / 2], e.pts[n], wd, e.pts)];
    }
    var out = [];
    for (var k = 0; k < n; k += 1)
        out = append(out, stadiumShape(e.pts[k], e.pts[k + 1], wd));
    return out;
}

function segsIntersect(s, t)
{
    const o1 = (s[2] - s[0]) * (t[1] - s[1]) - (s[3] - s[1]) * (t[0] - s[0]);
    const o2 = (s[2] - s[0]) * (t[3] - s[1]) - (s[3] - s[1]) * (t[2] - s[0]);
    const o3 = (t[2] - t[0]) * (s[1] - t[1]) - (t[3] - t[1]) * (s[0] - t[0]);
    const o4 = (t[2] - t[0]) * (s[3] - t[1]) - (t[3] - t[1]) * (s[2] - t[0]);
    return ((o1 > 0) != (o2 > 0)) && ((o3 > 0) != (o4 > 0));
}

function segSegDist2(s, t)
{
    if (segsIntersect(s, t))
        return 0;
    return min(min(distSeg2(s[0], s[1], t), distSeg2(s[2], s[3], t)), min(distSeg2(t[0], t[1], s), distSeg2(t[2], t[3], s)));
}

function shapesOverlap(a is map, b is map) returns boolean
{
    if (a.bbox[0] > b.bbox[2] || b.bbox[0] > a.bbox[2] || a.bbox[1] > b.bbox[3] || b.bbox[1] > a.bbox[3])
        return false;
    const lim = a.rad + b.rad + 1e-6;
    const lim2 = lim * lim;
    for (var s in a.segs)
    {
        for (var t in b.segs)
        {
            if (segSegDist2(s, t) < lim2)
                return true;
        }
    }
    return false;
}

// Greedy coloring: shapes in the same family never overlap.
function colorShapes(shapes is array) returns array
{
    var fams = [];
    for (var s in shapes)
    {
        var placed = false;
        for (var f = 0; f < size(fams); f += 1)
        {
            var ok = true;
            for (var o in fams[f])
            {
                if (shapesOverlap(s, o))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                fams[f] = append(fams[f], s);
                placed = true;
                break;
            }
        }
        if (!placed)
            fams = append(fams, [s]);
    }
    return fams;
}

// ======================================================================== preview

function drawPreview(context is Context, cSys is CoordSystem, nodes is array, cells is array, paths is array, skipped is array,
    ctx is map)
{
    const toW = function(p)
        {
            return toWorld(cSys, vector(p[0], p[1], 0) * meter);
        };
    for (var p in nodes)
    {
        if (insideSegs(p, ctx.regionSegs))
            addDebugPoint(context, toW(p), DebugColor.BLUE);
    }
    var seen = {};
    for (var cell in cells)
    {
        for (var i = 0; i < size(cell); i += 1)
        {
            const a = cell[i];
            const c = cell[(i + 1) % size(cell)];
            const mid = [(a[0] + c[0]) / 2, (a[1] + c[1]) / 2];
            if (!insideSegs(mid, ctx.regionSegs))
                continue;
            const ka = round(a[0] * 1e6) ~ "," ~ round(a[1] * 1e6);
            const kc = round(c[0] * 1e6) ~ "," ~ round(c[1] * 1e6);
            if (seen[ka ~ "|" ~ kc] != undefined)
                continue;
            seen[ka ~ "|" ~ kc] = true;
            seen[kc ~ "|" ~ ka] = true;
            addDebugLine(context, toW(a), toW(c), DebugColor.RED);
        }
    }
    for (var path in paths)
    {
        for (var seg in path)
        {
            if (seg.arc)
            {
                addDebugLine(context, toW(seg.p[0]), toW(seg.p[1]), DebugColor.GREEN);
                addDebugLine(context, toW(seg.p[1]), toW(seg.p[2]), DebugColor.GREEN);
            }
            else
            {
                addDebugLine(context, toW(seg.p[0]), toW(seg.p[1]), DebugColor.GREEN);
            }
        }
    }
    // Cells left solid get an orange star from their center to each corner.
    for (var C in skipped)
    {
        const c = polyCentroid(C);
        for (var p in C)
            addDebugLine(context, toW(c), toW(p), DebugColor.ORANGE);
    }
}
