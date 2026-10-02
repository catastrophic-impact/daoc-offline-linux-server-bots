using System.Threading.Tasks;

namespace MNL {
  /* An unsigned 32-bit integer, describing how transparency is handled in a texture. */

  public enum eAlphaFormat : uint {
    ALPHA_NONE = 0, /* No alpha blending; the texture is fully opaque. */
    ALPHA_BINARY = 1, /* Texture is either fully transparent or fully opaque.  There are no partially transparent areas. */
    ALPHA_SMOOTH = 2, /* Full range of alpha values can be used from fully transparent to fully opaque including all partially transparent values in between. */
    ALPHA_DEFAULT = 3, /* Use default setting. */
  }
}
