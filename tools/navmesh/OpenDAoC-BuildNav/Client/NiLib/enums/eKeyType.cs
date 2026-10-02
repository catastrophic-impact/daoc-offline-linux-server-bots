namespace MNL {
  /* 
        The type of animation interpolation (blending) that will be used on the associated key frames.
        Use linear interpolation.Use quadratic interpolation.  Forward and back tangents will be stored.Use Tension Bias Continuity interpolation.  Tension, bias, and continuity will be stored.For use only with rotation data.  Separate X, Y, and Z keys will be stored instead of using quaternions.Step function. Used for visibility keys in NiBoolData. */
  public enum eKeyType : uint {
    LINEAR_KEY = 1, /* Use linear interpolation. */
    QUADRATIC_KEY = 2, /* Use quadratic interpolation.  Forward and back tangents will be stored. */
    TBC_KEY = 3, /* Use Tension Bias Continuity interpolation.  Tension, bias, and continuity will be stored. */
    XYZ_ROTATION_KEY = 4, /* For use only with rotation data.  Separate X, Y, and Z keys will be stored instead of using quaternions. */
    CONST_KEY = 5, /* Step function. Used for visibility keys in NiBoolData. */
  }
}
