namespace MNL {
  /* 
  Specifies the availiable texture filter modes.  That is, the way pixels within a texture are blended together when textures are displayed on the screen at a size other than their original dimentions.
  Simply uses the nearest pixel.  Very grainy.Uses bilinear filtering.Uses trilinear filtering.Uses the nearest pixel from the mipmap that is closest to the display size.Blends the two mipmaps closest to the display size linearly, and then uses the nearest pixel from the result.Uses the closest mipmap to the display size and then uses bilinear filtering on the pixels. */
  public enum eTexFilterMode : uint {
    FILTER_NEAREST = 0, /* Simply uses the nearest pixel.  Very grainy. */
    FILTER_BILERP = 1, /* Uses bilinear filtering. */
    FILTER_TRILERP = 2, /* Uses trilinear filtering. */
    FILTER_NEAREST_MIPNEAREST = 3, /* Uses the nearest pixel from the mipmap that is closest to the display size. */
    FILTER_NEAREST_MIPLERP = 4, /* Blends the two mipmaps closest to the display size linearly, and then uses the nearest pixel from the result. */
    FILTER_BILERP_MIPNEAREST = 5, /* Uses the closest mipmap to the display size and then uses bilinear filtering on the pixels. */
  }
}
