namespace MNL {
  /* This enum lists the different face culling options. */
  public enum eFaceDrawMode : uint {
    DRAW_CCW_OR_BOTH = 0, /* use application defaults? */
    DRAW_CCW = 1, /* Draw counter clock wise faces, cull clock wise faces. This is the default for most (all?) Nif Games so far. */
    DRAW_CW = 2, /* Draw clock wise faces, cull counter clock wise faces. This will flip all the faces. */
    DRAW_BOTH = 3, /* Draw double sided faces. */
  }
}
