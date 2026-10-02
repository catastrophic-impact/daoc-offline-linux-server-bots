namespace MNL {
  /* The type of texture. */
  public enum eTexType : uint {
    BASE_MAP = 0, /* The basic texture used by most meshes. */
    DARK_MAP = 1, /* Used to darken the model with false lighting. */
    DETAIL_MAP = 2, /* Combined with base map for added detail.  Usually tiled over the mesh many times for close-up view. */
    GLOSS_MAP = 3, /* Allows the specularity (glossyness) of an object to differ across its surface. */
    GLOW_MAP = 4, /* Creates a glowing effect.  Basically an incandescence map. */
    BUMP_MAP = 5, /* Used to make the object appear to have more detail than it really does. */
    NORMAL_MAP = 6, /* Used to make the object appear to have more detail than it really does. */
    UNKNOWN2_MAP = 7, /* Unknown map. */
    DECAL_0_MAP = 8, /* For placing images on the object like stickers. */
    DECAL_1_MAP = 9, /* For placing images on the object like stickers. */
    DECAL_2_MAP = 10, /* For placing images on the object like stickers. */
    DECAL_3_MAP = 11, /* For placing images on the object like stickers. */
  }
}
