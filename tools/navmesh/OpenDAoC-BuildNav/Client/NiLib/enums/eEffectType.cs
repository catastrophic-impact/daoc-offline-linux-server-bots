namespace MNL {
  public enum eEffectType : uint {
    EFFECT_PROJECTED_LIGHT = 0, /* Apply a projected light texture. */
    EFFECT_PROJECTED_SHADOW = 1, /* Apply a projected shaddow texture. */
    EFFECT_ENVIRONMENT_MAP = 2, /* Apply an environment map texture. */
    EFFECT_FOG_MAP = 3, /* Apply a fog map texture. */
  }
}
