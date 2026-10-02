namespace MNL {
  /*  Determines the way the billboard will react to the camera. */
  public enum eBillboardMode : ushort {
    ALWAYS_FACE_CAMERA = 0, /* The billboard will always face the camera. */
    ROTATE_ABOUT_UP = 1, /* The billboard will only rotate around the up axis. */
    RIGID_FACE_CAMERA = 2, /* Rigid Face Camera. */
    ALWAYS_FACE_CENTER = 3, /* Always Face Center. */
    RIGID_FACE_CENTER = 4, /* Rigid Face Center. */
    ROTATE_ABOUT_UP2 = 9, /* The billboard will only rotate around the up axis (same as ROTATE_ABOUT_UP?). */
  }
}
