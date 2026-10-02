namespace MNL {
  /* An unsigned 32-bit integer, describing the color depth of a texture. */
  public enum ePixelLayout : uint {
    PIX_LAY_PALETTISED = 0, /* Texture is in 8-bit paletized format. */
    PIX_LAY_HIGH_COLOR_16 = 1, /* Texture is in 16-bit high color format. */
    PIX_LAY_TRUE_COLOR_32 = 2, /* Texture is in 32-bit true color format. */
    PIX_LAY_COMPRESSED = 3, /* Texture is compressed. */
    PIX_LAY_BUMPMAP = 4, /* Texture is a grayscale bump map. */
    PIX_LAY_PALETTISED_4 = 5, /* Texture is in 4-bit paletized format. */
    PIX_LAY_DEFAULT = 6, /* Use default setting. */
  }
}
