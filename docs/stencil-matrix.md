
### Geometric outlines on a prepared character

In the Editor, call `VrmxtWarudoShadowExport.PreparePrefab(prefabPath,
geometricOutlineNormals: true)` on a fresh character prefab to use geometric
extrusion for outlined MToonXT stencil materials. Preparation encodes the
angle-weighted mesh directions in the shading tangent frame in UV8 of the
generated mesh. This preserves the lighting normals, blend shapes, and original
green-channel width texture. It requires UV8 to be available. Direction
generation runs during preparation. The default uses the material's ordinary
normal stream. The prepared prefab can be previewed in Unity and passed to the
Warudo SDK.
