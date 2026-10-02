namespace MNL {
  /*  Determines the way that UV texture coordinates are generated. */
  public enum eCoordGenType : uint {
    CG_WORLD_PARALLEL = 0, /* Use plannar mapping. */
    CG_WORLD_PERSPECTIVE = 1, /* Use perspective mapping. */
    CG_SPHERE_MAP = 2, /* Use spherical mapping. */
    CG_SPECULAR_CUBE_MAP = 3, /* Use specular cube mapping. */
    CG_DIFFUSE_CUBE_MAP = 4, /* Use Diffuse cube mapping. */
  }
}
