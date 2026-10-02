namespace MNL {
  /* Determines decay function.  Used by NiPSysBombModifier. */
  public enum eDecayType : uint {
    DECAY_NONE = 0, /* No decay. */
    DECAY_LINEAR = 1, /* Linear decay. */
    DECAY_EXPONENTIAL = 2, /* Exponential decay. */
  }
}
