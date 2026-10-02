namespace MNL {
  public enum eTexClampMode : uint {
    CLAMP_S_CLAMP_T = 0, /* Clamp in both directions. */
    CLAMP_S_WRAP_T = 1, /* Clamp in the S(U) direction but wrap in the T(V) direction. */
    WRAP_S_CLAMP_T = 2, /* Wrap in the S(U) direction but clamp in the T(V) direction. */
    WRAP_S_WRAP_T = 3, /* Wrap in both directions. */
  }
}
