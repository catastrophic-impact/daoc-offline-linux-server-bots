namespace MNL {
  /* 
      This enum defines the various actions used in conjunction with the stencil buffer.
      For a detailed description of the individual options please refer to the OpenGL docs.
       */
  public enum eStencilAction : uint {
    ACTION_KEEP = 0, /*  */
    ACTION_ZERO = 1, /*  */
    ACTION_REPLACE = 2, /*  */
    ACTION_INCREMENT = 3, /*  */
    ACTION_DECREMENT = 4, /*  */
    ACTION_INVERT = 5, /*  */
  }
}
