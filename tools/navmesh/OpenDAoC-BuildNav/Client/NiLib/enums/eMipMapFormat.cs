namespace MNL {
  /*An unsigned 32-bit integer, describing how mipmaps are handled in a texture. */

  public enum eMipMapFormat : uint {
    MIP_FMT_NO = 0, /* Texture does not use mip maps. */
    MIP_FMT_YES = 1, /* Texture uses mip maps. */
    MIP_FMT_DEFAULT = 2, /* Use default setting. */
  }
}
