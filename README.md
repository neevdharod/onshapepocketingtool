# Auto Pocket (Onshape FeatureScript)

Lightening pockets for a flat face. Existing holes become nodes, extra nodes fill the face,
and the cells between ribs are cut through the wall with filleted corners.

## Install

1. In any Onshape document, add a **Feature Studio** (the `+` tab menu).
2. Replace its contents with `AutoPocket.fs` and click **Commit**.
3. In a Part Studio, **Auto Pocket** shows up under the custom features icon
   (add it to the toolbar from there, or use it from another document via *Add custom features*).

Already set up: the Onshape document **AutoPocket**
(https://cad.onshape.com/documents/785124c4c605e725effc8f8c) holds the feature, saved as version V1,
and it is on your toolbar. After changing the script there, commit, create a new version, and update
to it from the custom features menu in documents that use it.

If Onshape's FeatureScript version is newer than the header (`FeatureScript 3095;`),
it keeps working; you can bump both numbers on lines 1-2 to the version a new Feature Studio shows.

## Inputs

| Input | What it does |
|---|---|
| Face to pocket | One flat face. Pockets cut through the wall under it (stops at the far side, so a U-channel's opposite wall is untouched). |
| Pattern | Triangles, Voronoi, Isogrid (triangle grid), Hex (honeycomb), Quad grid. |
| Node layout | Triangles / Voronoi only: evenly spaced random, pure random, regular grid, perimeter + interior. |
| Node count | Extra nodes on top of holes and forced points. For grid patterns it sets the grid density. |
| Grid angle | Rotates grid patterns and the regular-grid layout. |
| Random seed | Re-rolls random layouts without changing anything else. |
| Rib width | Width of every rib, including spokes. |
| Border width | Solid band along the outer edge. |
| Hole boss width | Solid ring around each circular hole. |
| Cutout border width | Solid band around non-circular cutouts (slots, rectangles). |
| Corner fillet radius | Pocket corner radius (end mill radius). 0 gives sharp corners. |
| Minimum pocket size | A circle this big must fit in a pocket, or the pocket is skipped. |
| Extra node points | Sketch points or vertices to force a node (rib junction) at. |
| Keep-out regions | Sketch regions or faces parallel to the pocketed face that must stay solid. |
| Preview nodes and ribs only | Draws nodes (blue), rib centerlines (red), pocket outlines (green) and an orange star on each cell that stays solid, while the dialog is open, without cutting. |

## How holes and cutouts are handled

- **Circular holes** are nodes with a boss ring. In Triangles mode ribs run straight to them; in Voronoi
  and Hex mode the hole's cell is split by spokes; in Isogrid and Quad mode the nearest grid node snaps
  onto the hole, or the cell is split by spokes when it is too far to snap.
- **Other cutouts** get a border, and ribs tie into that border.
- A pocket that would leave a floating island (for example around a keep-out) stays solid instead.

## Small triangles

In Triangles mode, a triangle too small for the minimum pocket size gives up one of its ribs and joins
the neighbour across it, as long as the merged cell is convex and a pocket fits in it. That is why some
pockets come out four-sided. When several neighbours work, the one giving the smallest merged pocket
wins. A triangle with no workable neighbour stays solid and shows an orange star in preview.

## Tips

- Lots of "skipped, below minimum pocket size" means the cells are too small: lower the node count or
  rib width, or reduce the minimum pocket size. Turn on preview to see which cells (orange stars).
- Small holes often end up with solid material around them because the wedges next to them are too
  thin to cut. That is intended and keeps the holes well supported.
- Regeneration takes a few seconds at 20-40 nodes and about 10 s at 100 nodes.
