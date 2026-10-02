namespace MNL {
  /* Used by NiPoint3InterpControllers to select which type of color in the controlled object that will be animated. */
  public enum eTargetColor : ushort {
    TC_AMBIENT = 0, /* Control the ambient color. */
    TC_DIFFUSE = 1, /* Control the diffuse color. */
    TC_SPECULAR = 2, /* Control the specular color. */
    TC_SELF_ILLUM = 3, /* Control the self illumination color. */
  }
}
