
namespace MNL {
  public enum eTexTransform : uint {
    TT_TRANSLATE_U = 0, /* Means this controller moves the U texture cooridnates. */
    TT_TRANSLATE_V = 1, /* Means this controller moves the V texture cooridnates. */
    TT_ROTATE = 2, /* Means this controller roates the UV texture cooridnates. */
    TT_SCALE_U = 3, /* Means this controller scales the U texture cooridnates. */
    TT_SCALE_V = 4, /* Means this controller scales the V texture cooridnates. */
  }
}
